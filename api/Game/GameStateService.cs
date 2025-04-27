using System;
using System.Threading.Tasks;
using AzureOnlinePongGame.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;
using System.Collections.Generic; // Added for List

namespace AzureOnlinePongGame.Game
{
    public static class GameStateService
    {
        // Redis connection
        private static readonly Lazy<ConnectionMultiplexer> redisConnection = new Lazy<ConnectionMultiplexer>(() =>
        {
            var configuration = ConfigurationOptions.Parse(Environment.GetEnvironmentVariable("RedisConnectionString") ?? "localhost");
            configuration.ConnectTimeout = 5000; // 5 seconds
            configuration.SyncTimeout = 5000;
            configuration.AbortOnConnectFail = false; // More resilient connections
            return ConnectionMultiplexer.Connect(configuration);
        });

        private static IDatabase RedisDb => redisConnection.Value.GetDatabase();

        private static string GetSessionKey(string playerId) => $"pong:session:{playerId}";
        private static string GetSessionKeyBySessionId(string sessionId) => $"pong:sessionid:{sessionId}";
        private static string MatchmakingQueueKey => "pong:matchmaking_queue"; // Key for the Redis list
        private static string ActiveGamesSetKey => "pong:active_games"; // Key for tracking active game sessions

        public static async Task<bool> StoreSessionAsync(string playerId, GameSession session, ILogger log = null)
        {
            try
            {
                var json = JsonConvert.SerializeObject(session,
                    new JsonSerializerSettings {
                        NullValueHandling = NullValueHandling.Ignore,
                        DefaultValueHandling = DefaultValueHandling.Ignore
                    });

                await RedisDb.StringSetAsync(GetSessionKey(playerId), json);
                // Also store by sessionId if you want to support multi-player lookup
                // Consider using a more robust session ID generation if needed
                string sessionId = $"{session.Player1Id}:{session.Player2Id}";
                await RedisDb.StringSetAsync(GetSessionKeyBySessionId(sessionId), json);
                return true;
            }
            catch (Exception ex)
            {
                log?.LogError(ex, $"Error storing session for player {playerId}");
                return false;
            }
        }

        public static async Task<GameSession> GetSessionAsync(string playerId, ILogger log = null)
        {
            try
            {
                var json = await RedisDb.StringGetAsync(GetSessionKey(playerId));
                if (json.IsNullOrEmpty) return null;
                return JsonConvert.DeserializeObject<GameSession>(json!);
            }
            catch (Exception ex)
            {
                log?.LogError(ex, $"Error retrieving session for player {playerId}");
                return null;
            }
        }

        public static async Task<bool> UpdateSessionForBothPlayersAsync(GameSession session, ILogger log = null)
        {
            // Ensure both players have IDs before attempting to store
            if (string.IsNullOrEmpty(session.Player1Id) || string.IsNullOrEmpty(session.Player2Id))
            {
                log?.LogError($"Attempted to update session with missing player IDs: P1={session.Player1Id}, P2={session.Player2Id}");
                return false;
            }

            string sessionId = $"{session.Player1Id}:{session.Player2Id}";
            
            try
            {
                // Store session for both players
                var json = JsonConvert.SerializeObject(session,
                    new JsonSerializerSettings {
                        NullValueHandling = NullValueHandling.Ignore,
                        DefaultValueHandling = DefaultValueHandling.Ignore
                    });

                var batch = RedisDb.CreateBatch();
                var tasks = new List<Task>();
                
                // Add all tasks to our list so we can await them
                tasks.Add(batch.StringSetAsync(GetSessionKey(session.Player1Id), json));
                tasks.Add(batch.StringSetAsync(GetSessionKey(session.Player2Id), json));
                tasks.Add(batch.StringSetAsync(GetSessionKeyBySessionId(sessionId), json));

                // Add to active games set if not already over, otherwise remove
                if (session.State.GameOver)
                {
                    tasks.Add(batch.SetRemoveAsync(ActiveGamesSetKey, sessionId));
                    log?.LogInformation($"Game over, removing session {sessionId} from active games");
                }
                else
                {
                    tasks.Add(batch.SetAddAsync(ActiveGamesSetKey, sessionId));
                }

                // Execute the batch
                batch.Execute();
                
                // Await all tasks to complete
                await Task.WhenAll(tasks);
                return true;
            }
            catch (Exception ex)
            {
                log?.LogError(ex, $"Error updating session for both players: {sessionId}");
                return false;
            }
        }

        // --- Matchmaking Methods ---

        public static async Task AddToMatchmakingAsync(string playerId, ILogger log = null)
        {
            try
            {
                await RedisDb.ListRightPushAsync(MatchmakingQueueKey, playerId);
                log?.LogInformation($"Player {playerId} added to matchmaking queue in Redis.");
            }
            catch (Exception ex)
            {
                log?.LogError(ex, $"Error adding player {playerId} to matchmaking queue in Redis.");
                throw; // Rethrow to allow caller to handle appropriately
            }
        }

        public static async Task<GameSession> TryMatchPlayersAsync(ILogger log = null)
        {
            try
            {
                long queueLength = await RedisDb.ListLengthAsync(MatchmakingQueueKey);
                log?.LogInformation($"Matchmaking queue length: {queueLength}");

                if (queueLength >= 2)
                {
                    // Use Lua script for atomic pop of two players to avoid race conditions
                    string script = @"
                        local p1 = redis.call('LPOP', KEYS[1])
                        if not p1 then return nil end
                        local p2 = redis.call('LPOP', KEYS[1])
                        if not p2 then
                            redis.call('LPUSH', KEYS[1], p1) -- Put p1 back if no p2
                            return nil
                        end
                        return {p1, p2}";

                    var result = await RedisDb.ScriptEvaluateAsync(script, new RedisKey[] { MatchmakingQueueKey });

                    // Use Resp2Type instead of deprecated Type property
                    if (!result.IsNull && result.Resp2Type == ResultType.Array)
                    {
                        var players = (RedisValue[])result;
                        if (players.Length == 2)
                        {
                            string p1 = players[0];
                            string p2 = players[1];
                            log?.LogInformation($"Atomically matched players from Redis: {p1} and {p2}");
                            // Create a new session and add to active games set
                            var session = new GameSession { Player1Id = p1, Player2Id = p2 };
                            string sessionId = $"{p1}:{p2}";
                            await RedisDb.SetAddAsync(ActiveGamesSetKey, sessionId);
                            return session;
                        }
                    }
                    else
                    {
                        log?.LogWarning("Lua script for matching did not return two players as expected.");
                    }
                }
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "Error trying to match players from Redis queue.");
            }
            return null; // No match found or error occurred
        }

        public static async Task RemoveFromMatchmakingAsync(string playerId, ILogger log = null)
        {
            try
            {
                // LREM removes occurrences of playerId from the list
                long removedCount = await RedisDb.ListRemoveAsync(MatchmakingQueueKey, playerId);
                if (removedCount > 0)
                {
                    log?.LogInformation($"Removed player {playerId} from matchmaking queue in Redis ({removedCount} instances).");
                }
            }
            catch (Exception ex)
            {
                log?.LogError(ex, $"Error removing player {playerId} from matchmaking queue in Redis.");
                // Don't throw since this is often called during cleanup
            }
        }

        public static async Task<long> GetMatchmakingQueueSizeAsync(ILogger log = null)
        {
            try
            {
                return await RedisDb.ListLengthAsync(MatchmakingQueueKey);
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "Error getting matchmaking queue size from Redis.");
                return -1; // Indicate error
            }
        }

        public static async Task<long> GetActiveGameCountAsync(ILogger log = null)
        {
            try
            {
                return await RedisDb.SetLengthAsync(ActiveGamesSetKey);
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "Error getting active game count from Redis.");
                return -1; // Indicate error
            }
        }

        // --- End Matchmaking Methods ---

        // Public method to check Redis connection status
        public static bool IsRedisConnected(out string errorMessage)
        {
            errorMessage = null;
            try
            {
                if (!redisConnection.IsValueCreated) // Don't force connection just for health check
                {
                    return true; // Assume connectable if not yet attempted
                }
                return redisConnection.Value.IsConnected;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
