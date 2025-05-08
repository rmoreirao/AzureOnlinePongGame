// File: backend/Services/GameStateService.cs
using AzureOnlinePongGame.Models; // Assuming Models are now in backend/Models
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AzureOnlinePongGame.Services // Changed namespace
{
    // Made class non-static
    public class GameStateService : IDisposable
    {
        private readonly ILogger<GameStateService> _logger;
        private readonly Lazy<ConnectionMultiplexer> _lazyConnection;
        private readonly string _redisConnectionString;

        private const string MATCHMAKING_QUEUE_KEY = "pong:matchmaking_queue";
        private const string ACTIVE_GAMES_KEY_PREFIX = "pong:game:";
        private const string PLAYER_SESSION_MAP_KEY_PREFIX = "pong:player_session:";
        private const string PLAYER_INPUT_KEY_PREFIX = "pong:player_input:";
        private const int SESSION_EXPIRY_MINUTES = 10; // Adjust as needed

        // Inject IConfiguration and ILogger
        public GameStateService(IConfiguration configuration, ILogger<GameStateService> logger)
        {
            _logger = logger;
            // Read connection string from configuration
            _redisConnectionString = configuration.GetConnectionString("RedisConnection")
                ?? throw new InvalidOperationException("Redis connection string 'RedisConnection' not found in configuration.");

            _lazyConnection = new Lazy<ConnectionMultiplexer>(() =>
            {
                try
                {
                    _logger.LogInformation("Initializing Redis connection...");
                    var connection = ConnectionMultiplexer.Connect(_redisConnectionString);
                    _logger.LogInformation("Redis connection established successfully.");
                    connection.ConnectionFailed += (_, args) => _logger.LogError(args.Exception, "Redis connection failed: {FailureType}", args.FailureType);
                    connection.ConnectionRestored += (_, args) => _logger.LogInformation("Redis connection restored: {FailureType}", args.FailureType);
                    connection.ErrorMessage += (_, args) => _logger.LogError("Redis error message: {Message}", args.Message);
                    connection.InternalError += (_, args) => _logger.LogError(args.Exception, "Redis internal error: {Origin}", args.Origin);
                    return connection;
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Failed to connect to Redis on initial connection attempt.");
                    throw; // Rethrow critical failure
                }
            });
        }

        private IDatabase GetDatabase()
        {
            if (!_lazyConnection.Value.IsConnected)
            {
                _logger.LogError("Attempted to get Redis database, but connection is not established.");
                // Optionally attempt to reconnect or throw a more specific exception
                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis connection is not available.");
            }
            return _lazyConnection.Value.GetDatabase();
        }

        // --- Methods are now instance methods (removed static) ---

        public async Task AddToMatchmakingAsync(string playerId)
        {
            try
            {
                var db = GetDatabase();
                // Use ListRightPush to add to the end of the queue
                await db.ListRightPushAsync(MATCHMAKING_QUEUE_KEY, playerId).ConfigureAwait(false);
                _logger.LogInformation($"Player {playerId} added to matchmaking queue.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error adding player {playerId} to matchmaking queue.");
                throw; // Rethrow to indicate failure
            }
        }

        public async Task RemoveFromMatchmakingAsync(string playerId)
        {
            try
            {
                var db = GetDatabase();
                // Remove all occurrences of the player ID from the list
                long removedCount = await db.ListRemoveAsync(MATCHMAKING_QUEUE_KEY, playerId).ConfigureAwait(false);
                if (removedCount > 0)
                {
                    _logger.LogInformation($"Player {playerId} removed from matchmaking queue ({removedCount} instances).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error removing player {playerId} from matchmaking queue.");
                // Decide if throwing is appropriate or just logging
            }
        }

        public async Task<GameSession?> TryMatchPlayersAsync()
        {
            IDatabase db = GetDatabase();
            string? player1Id = null;
            string? player2Id = null;

            try
            {
                // Use Lua script for atomic pop of two players if available
                // This prevents race conditions where two server instances might grab the same players
                var script = @"
                    local p1 = redis.call('LPOP', KEYS[1])
                    if not p1 then return nil end
                    local p2 = redis.call('LPOP', KEYS[1])
                    if not p2 then
                        redis.call('LPUSH', KEYS[1], p1) -- Put p1 back if no p2
                        return nil
                    end
                    return {p1, p2}";

                var result = (RedisResult[]?)await db.ScriptEvaluateAsync(script, new RedisKey[] { MATCHMAKING_QUEUE_KEY }).ConfigureAwait(false);

                if (result != null && result.Length == 2)
                {
                    player1Id = (string?)result[0];
                    player2Id = (string?)result[1];

                    if (player1Id != null && player2Id != null)
                    {
                        _logger.LogInformation($"Atomically matched players: {player1Id} and {player2Id}");
                        var session = new GameSession { Player1Id = player1Id, Player2Id = player2Id };
                        // Initialize basic state immediately after matching
                        session.State = new GameState();
                        session.LastUpdateTime = DateTime.UtcNow;
                        // Store the initial session state right away
                        if (await UpdateSessionForBothPlayersAsync(session).ConfigureAwait(false))
                        {
                            return session;
                        }
                        else
                        {
                            _logger.LogError($"Failed to store initial session for matched players {player1Id} and {player2Id}. Putting them back in queue.");
                            // Attempt to put players back in the queue if session storage fails
                            await db.ListRightPushAsync(MATCHMAKING_QUEUE_KEY, new RedisValue[] { player1Id, player2Id }).ConfigureAwait(false);
                            return null;
                        }
                    }
                }
                // Fallback or if Lua script fails/returns null
                _logger.LogDebug("Lua script didn't match players or is unavailable. Checking queue length.");
                long queueLength = await db.ListLengthAsync(MATCHMAKING_QUEUE_KEY).ConfigureAwait(false);
                if (queueLength < 2)
                {
                    _logger.LogInformation($"Matchmaking queue length ({queueLength}) is less than 2. No match found.");
                    return null;
                }

                // Less atomic way (potential race condition without Lua)
                player1Id = await db.ListLeftPopAsync(MATCHMAKING_QUEUE_KEY).ConfigureAwait(false);
                player2Id = await db.ListLeftPopAsync(MATCHMAKING_QUEUE_KEY).ConfigureAwait(false);

                if (player1Id != null && player2Id != null)
                {
                     _logger.LogWarning($"Matched players using non-atomic LPOP: {player1Id} and {player2Id}. Potential race condition exists.");
                     var session = new GameSession { Player1Id = player1Id, Player2Id = player2Id };
                     session.State = new GameState();
                     session.LastUpdateTime = DateTime.UtcNow;
                     if (await UpdateSessionForBothPlayersAsync(session).ConfigureAwait(false))
                     {
                         return session;
                     }
                     else
                     {
                         _logger.LogError($"Failed to store initial session for matched players {player1Id} and {player2Id} (non-atomic). Putting them back in queue.");
                         await db.ListRightPushAsync(MATCHMAKING_QUEUE_KEY, new RedisValue[] { player1Id, player2Id }).ConfigureAwait(false);
                         return null;
                     }
                }
                else
                {
                    // Put player1 back if player2 couldn't be popped
                    if (player1Id != null)
                    {
                        await db.ListLeftPushAsync(MATCHMAKING_QUEUE_KEY, player1Id).ConfigureAwait(false);
                        _logger.LogWarning($"Popped player {player1Id} but no second player available. Put player 1 back.");
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during matchmaking attempt.");
                // Attempt to put players back if they were popped but an error occurred
                if (player1Id != null && player2Id != null)
                {
                     await db.ListRightPushAsync(MATCHMAKING_QUEUE_KEY, new RedisValue[] { player1Id, player2Id }).ConfigureAwait(false);
                }
                else if (player1Id != null)
                {
                     await db.ListLeftPushAsync(MATCHMAKING_QUEUE_KEY, player1Id).ConfigureAwait(false);
                }
                return null;
            }
        }

        private string GetSessionKey(string player1Id, string player2Id)
        {
            // Ensure consistent key regardless of player order
            var ids = new List<string> { player1Id, player2Id };
            ids.Sort(StringComparer.Ordinal);
            return $"{ACTIVE_GAMES_KEY_PREFIX}{ids[0]}:{ids[1]}";
        }

        private string GetPlayerMapKey(string playerId)
        {
            return $"{PLAYER_SESSION_MAP_KEY_PREFIX}{playerId}";
        }

        public async Task<bool> StoreSessionAsync(string playerId, GameSession session)
        {
            try
            {
                var db = GetDatabase();
                string sessionKey = GetSessionKey(session.Player1Id, session.Player2Id);
                string playerMapKey = GetPlayerMapKey(playerId);
                string sessionJson = JsonConvert.SerializeObject(session, new JsonSerializerSettings {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
                    NullValueHandling = NullValueHandling.Ignore,
                    DefaultValueHandling = DefaultValueHandling.Ignore
                });

                // Use a transaction to ensure atomicity
                var tran = db.CreateTransaction();
                _ = tran.StringSetAsync(sessionKey, sessionJson, TimeSpan.FromMinutes(SESSION_EXPIRY_MINUTES));
                _ = tran.StringSetAsync(playerMapKey, sessionKey, TimeSpan.FromMinutes(SESSION_EXPIRY_MINUTES)); // Map player to session key

                // If there's a second player, index them too
                if (!string.IsNullOrEmpty(session.Player2Id) && session.Player2Id != playerId)
                {
                    var player2MapKey = GetPlayerMapKey(session.Player2Id);
                    _ = tran.StringSetAsync(player2MapKey, sessionKey, TimeSpan.FromMinutes(SESSION_EXPIRY_MINUTES));
                }

                bool success = await tran.ExecuteAsync().ConfigureAwait(false);

                if (success)
                {
                    _logger.LogInformation($"Session {sessionKey} stored successfully for player {playerId}.");
                }
                else
                {
                    _logger.LogError($"Failed to execute transaction for storing session {sessionKey} for player {playerId}.");
                }
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error storing session for player {playerId}.");
                return false;
            }
        }

        public async Task<bool> UpdateSessionForBothPlayersAsync(GameSession session)
        {
            if (string.IsNullOrEmpty(session.Player1Id) || string.IsNullOrEmpty(session.Player2Id))
            {
                _logger.LogError($"Attempted to update session with missing player IDs: P1={session.Player1Id}, P2={session.Player2Id}");
                return false;
            }

            try
            {
                var db = GetDatabase();
                string sessionKey = GetSessionKey(session.Player1Id, session.Player2Id);
                string player1MapKey = GetPlayerMapKey(session.Player1Id);
                string player2MapKey = GetPlayerMapKey(session.Player2Id);
                session.LastUpdateTime = DateTime.UtcNow; // Update timestamp before saving
                
                // Only serialize if needed (reduces CPU overhead)
                string? sessionJson = null;
                    
                // Use a transaction to reduce round-trips
                var tran = db.CreateTransaction();
                    
                // Prepare for serialization only when needed
                sessionJson = JsonConvert.SerializeObject(session, new JsonSerializerSettings {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
                    NullValueHandling = NullValueHandling.Ignore,
                    DefaultValueHandling = DefaultValueHandling.Ignore
                });
                    
                // Update session data with expiry
                _ = tran.StringSetAsync(sessionKey, sessionJson, TimeSpan.FromMinutes(SESSION_EXPIRY_MINUTES));
                    
                // Update player-to-session mapping with expiry for both players
                _ = tran.StringSetAsync(player1MapKey, sessionKey, TimeSpan.FromMinutes(SESSION_EXPIRY_MINUTES));
                    
                // Don't set the mapping for bot players
                if (!session.Player2Id.StartsWith("bot_"))
                {
                    _ = tran.StringSetAsync(player2MapKey, sessionKey, TimeSpan.FromMinutes(SESSION_EXPIRY_MINUTES));
                }
                    
                bool success = await tran.ExecuteAsync().ConfigureAwait(false);

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating session for players {session.Player1Id} and {session.Player2Id}.");
                return false;
            }
        }

        public async Task<GameSession?> GetSessionAsync(string playerIdOrSessionId)
        {
            try
            {
                var db = GetDatabase();
                string? sessionId = null;

                // First, try to see if the provided ID is a direct session ID
                var directSessionData = await db.StringGetAsync($"{ACTIVE_GAMES_KEY_PREFIX}{playerIdOrSessionId}");
                if (directSessionData.HasValue)
                {
                    sessionId = playerIdOrSessionId;
                }
                else
                {
                    // If not, assume it's a player ID and look up the session ID in the index
                    var sessionLookupKey = GetPlayerMapKey(playerIdOrSessionId);
                    var indexedSessionId = await db.StringGetAsync(sessionLookupKey);
                    if (indexedSessionId.HasValue)
                    {
                        sessionId = indexedSessionId.ToString();
                    }
                }

                if (string.IsNullOrEmpty(sessionId))
                {
                    _logger.LogDebug($"No session ID found for identifier: {playerIdOrSessionId}");
                    return null;
                }

                var sessionKey = $"{ACTIVE_GAMES_KEY_PREFIX}{sessionId}";
                var sessionData = await db.StringGetAsync(sessionKey);
                if (!sessionData.HasValue)
                {
                    _logger.LogWarning($"Session data not found for session ID: {sessionId} (looked up via {playerIdOrSessionId})");
                    return null;
                }

                try
                {
                    return JsonConvert.DeserializeObject<GameSession>(sessionData.ToString());
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, $"Failed to deserialize session data for session ID: {sessionId}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving session for identifier {playerIdOrSessionId}.");
                return null;
            }
        }

        public async Task<List<GameSession>> GetAllActiveSessionsAsync()
        {
            var sessions = new List<GameSession>();
            try
            {
                var server = _lazyConnection.Value.GetServer(_lazyConnection.Value.GetEndPoints().First());
                var db = GetDatabase();
                
                // Use SCAN instead of KEYS for better memory management
                var pattern = $"{ACTIVE_GAMES_KEY_PREFIX}*";
                var pageSize = 20; // Process in reasonable batches
                var sessionKeys = new List<RedisKey>();
                
                await foreach (var key in server.KeysAsync(pattern: pattern, pageSize: pageSize))
                {
                    sessionKeys.Add(key);
                    
                    // Process in batches to avoid large memory allocations
                    if (sessionKeys.Count >= pageSize)
                    {
                        await ProcessSessionBatchAsync(db, sessionKeys, sessions);
                        sessionKeys.Clear();
                    }
                }
                
                // Process any remaining keys
                if (sessionKeys.Count > 0)
                {
                    await ProcessSessionBatchAsync(db, sessionKeys, sessions);
                }
                
                _logger.LogDebug($"Retrieved {sessions.Count} active game sessions.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving all active game sessions.");
            }
            return sessions;
        }

        private async Task ProcessSessionBatchAsync(IDatabase db, List<RedisKey> sessionKeys, List<GameSession> sessions)
        {
            if (sessionKeys.Count == 0) return;
            
            var sessionJsonValues = await db.StringGetAsync(sessionKeys.ToArray());
            
            for (int i = 0; i < sessionJsonValues.Length; i++)
            {
                if (sessionJsonValues[i].HasValue)
                {
                    try
                    {
                        var session = JsonConvert.DeserializeObject<GameSession>(sessionJsonValues[i].ToString());
                        if (session != null && !session.State.GameOver) // Only add active games
                        {
                            sessions.Add(session);
                        }
                        else if (session != null && session.State.GameOver)
                        {
                            // Clean up finished game sessions
                            _ = db.KeyDeleteAsync(sessionKeys[i]);
                            
                            // Also clean up player-to-session mappings
                            if (!string.IsNullOrEmpty(session.Player1Id))
                                _ = db.KeyDeleteAsync(GetPlayerMapKey(session.Player1Id));
                                
                            if (!string.IsNullOrEmpty(session.Player2Id) && !session.Player2Id.StartsWith("bot_"))
                                _ = db.KeyDeleteAsync(GetPlayerMapKey(session.Player2Id));
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        _logger.LogError(jsonEx, $"Failed to deserialize session data for key {sessionKeys[i]}.");
                    }
                }
            }
        }

        public async Task<long> GetMatchmakingQueueSizeAsync()
        {
            try
            {
                return await GetDatabase().ListLengthAsync(MATCHMAKING_QUEUE_KEY).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting matchmaking queue size.");
                return -1; // Indicate error
            }
        }

        public long GetActiveGameCount()
        {
            try
            {
                // This relies on KEYS/SCAN, consider alternatives if performance is critical
                 var server = _lazyConnection.Value.GetServer(_lazyConnection.Value.GetEndPoints().First());
                 // Use Count() on the IEnumerable returned by Keys
                 long count = server.Keys(pattern: $"{ACTIVE_GAMES_KEY_PREFIX}*").LongCount();
                 return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active game count.");
                return -1; // Indicate error
            }
        }

        public bool IsRedisConnected(out string? errorMessage)
        {
            errorMessage = null;
            try
            {
                if (!_lazyConnection.IsValueCreated)
                {
                    // Force connection attempt if not already tried
                     var _ = _lazyConnection.Value;
                }

                if (_lazyConnection.Value == null || !_lazyConnection.Value.IsConnected)
                {
                    errorMessage = "Redis connection multiplexer is null or not connected.";
                     _logger.LogWarning(errorMessage);
                    return false;
                }

                // Optional: Perform a quick PING command to be extra sure
                // var pong = GetDatabase().Ping();
                // _logger.LogDebug($"Redis PING response time: {pong.TotalMilliseconds}ms");

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to check Redis connection status: {ex.Message}";
                _logger.LogError(ex, "Exception while checking Redis connection status.");
                return false;
            }
        }

        public async Task StorePlayerInputAsync(string playerId, float targetY)
        {
            try
            {
                var db = GetDatabase();
                var inputKey = $"{PLAYER_INPUT_KEY_PREFIX}{playerId}";
                // Store the input. It will be cleared by GameLoopService after processing.
                await db.StringSetAsync(inputKey, targetY.ToString(), TimeSpan.FromSeconds(5)); // Expire if not processed quickly
                _logger.LogDebug($"Stored input for player {playerId}: {targetY}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error storing input for player {playerId}.");
            }
        }

        public async Task<(float? Player1Input, float? Player2Input)> GetAndClearPlayerInputsAsync(string sessionId, string? player1Id, string? player2Id)
        {
            var db = GetDatabase();
            float? p1Input = null;
            float? p2Input = null;

            try
            {
                if (!string.IsNullOrEmpty(player1Id))
                {
                    var inputKey1 = $"{PLAYER_INPUT_KEY_PREFIX}{player1Id}";
                    var inputVal1 = await db.StringGetAsync(inputKey1);
                    if (inputVal1.HasValue && float.TryParse(inputVal1, out float p1TargetY))
                    {
                        p1Input = p1TargetY;
                        await db.KeyDeleteAsync(inputKey1); // Clear after reading
                    }
                }

                if (!string.IsNullOrEmpty(player2Id) && !player2Id.StartsWith("bot_"))
                {
                    var inputKey2 = $"{PLAYER_INPUT_KEY_PREFIX}{player2Id}";
                    var inputVal2 = await db.StringGetAsync(inputKey2);
                    if (inputVal2.HasValue && float.TryParse(inputVal2, out float p2TargetY))
                    {
                        p2Input = p2TargetY;
                        await db.KeyDeleteAsync(inputKey2); // Clear after reading
                    }
                }

                if (p1Input.HasValue || p2Input.HasValue)
                {
                    _logger.LogDebug($"Inputs for session {sessionId}: P1: {p1Input?.ToString() ?? "N/A"}, P2: {p2Input?.ToString() ?? "N/A"}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving and clearing inputs for session {sessionId}.");
            }

            return (p1Input, p2Input);
        }

        // Implement IDisposable
        public void Dispose()
        {
            if (_lazyConnection.IsValueCreated)
            {
                _lazyConnection.Value.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}