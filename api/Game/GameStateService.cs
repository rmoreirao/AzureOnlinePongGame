using System;
using System.Threading.Tasks;
using AzureOnlinePongGame.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;

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
            bool success1 = await StoreSessionAsync(session.Player1Id, session, log);
            bool success2 = await StoreSessionAsync(session.Player2Id, session, log);
            return success1 && success2;
        }

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
