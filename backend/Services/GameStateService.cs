// File: backend/Services/GameStateService.cs
using AzureOnlinePongGame.Models; // Assuming Models are now in backend/Models
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
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
        private readonly PaddlePositionCache _paddlePositionCache;

        private const string MATCHMAKING_QUEUE_KEY = "pong:matchmaking_queue";
        private const string ACTIVE_GAMES_KEY_PREFIX = "pong:game:";
        private const string PLAYER_SESSION_MAP_KEY_PREFIX = "pong:player_session:";
        private const string PLAYER_INPUT_KEY_PREFIX = "pong:player_input:";
        private const int SESSION_EXPIRY_MINUTES = 10; // Adjust as needed        // Inject IConfiguration and ILogger
        public GameStateService(IConfiguration configuration, ILogger<GameStateService> logger, PaddlePositionCache paddlePositionCache)
        {
            _logger = logger;
            _paddlePositionCache = paddlePositionCache;
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

        // In-memory session storage for active sessions
        private readonly ConcurrentDictionary<string, GameSession> _activeSessions = new();
        // Helper for consistent session key
        private string GetSessionKey(string player1Id, string player2Id)
        {
            var ids = new List<string> { player1Id, player2Id };
            ids.Sort(StringComparer.Ordinal);
            return $"{ACTIVE_GAMES_KEY_PREFIX}{ids[0]}:{ids[1]}";
        }
        // In-memory session storage
        public Task<bool> StoreSessionAsync(string playerId, GameSession session)
        {
            try
            {
                string sessionKey = GetSessionKey(session.Player1Id, session.Player2Id);
                _activeSessions[sessionKey] = session;
                _logger.LogInformation($"Session {sessionKey} stored in memory for player {playerId}.");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error storing session for player {playerId} in memory.");
                return Task.FromResult(false);
            }
        }

        public Task<bool> UpdateSessionForBothPlayersAsync(GameSession session)
        {
            if (string.IsNullOrEmpty(session.Player1Id) || string.IsNullOrEmpty(session.Player2Id))
            {
                _logger.LogError($"Attempted to update session with missing player IDs: P1={session.Player1Id}, P2={session.Player2Id}");
                return Task.FromResult(false);
            }
            try
            {
                string sessionKey = GetSessionKey(session.Player1Id, session.Player2Id);
                session.LastUpdateTime = DateTime.UtcNow;
                _activeSessions[sessionKey] = session;
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating session for players {session.Player1Id} and {session.Player2Id} in memory.");
                return Task.FromResult(false);
            }
        }

        public Task<GameSession?> GetSessionAsync(string playerIdOrSessionId)
        {
            try
            {
                // Try as session key
                string sessionKey = $"{ACTIVE_GAMES_KEY_PREFIX}{playerIdOrSessionId}";
                if (_activeSessions.TryGetValue(sessionKey, out var session))
                {
                    return Task.FromResult<GameSession?>(session);
                }
                // Try as player ID
                var found = _activeSessions.Values.FirstOrDefault(s => s.Player1Id == playerIdOrSessionId || s.Player2Id == playerIdOrSessionId);
                if (found != null)
                {
                    return Task.FromResult<GameSession?>(found);
                }
                _logger.LogDebug($"No session found in memory for identifier: {playerIdOrSessionId}");
                return Task.FromResult<GameSession?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving session for identifier {playerIdOrSessionId} from memory.");
                return Task.FromResult<GameSession?>(null);
            }
        }

        public Task<List<GameSession>> GetAllActiveSessionsAsync()
        {
            try
            {
                var sessions = _activeSessions.Values.Where(s => s.State != null && !s.State.GameOver).ToList();
                _logger.LogDebug($"Retrieved {sessions.Count} active game sessions from memory.");
                return Task.FromResult(sessions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving all active game sessions from memory.");
                return Task.FromResult(new List<GameSession>());
            }
        }

        public long GetActiveGameCount()
        {
            try
            {
                return _activeSessions.Values.LongCount(s => s.State != null && !s.State.GameOver);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active game count from memory.");
                return -1;
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

        public void StorePlayerInputAsync(string playerId, float targetY)
        {
            try
            {
                // Use memory cache instead of Redis for paddle positions
                _paddlePositionCache.StorePaddlePosition(playerId, targetY);
                _logger.LogDebug($"Stored input for player {playerId}: {targetY}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error storing input for player {playerId}.");
            }
        }

        public Task<(float? Player1Input, float? Player2Input)> GetAndClearPlayerInputsAsync(string sessionId, string? player1Id, string? player2Id)
        {
            try
            {
                // Get paddle positions from memory cache
                var (p1Input, p2Input) = _paddlePositionCache.GetPlayerInputs(player1Id, player2Id);

                if (p1Input.HasValue || p2Input.HasValue)
                {
                    _logger.LogDebug($"Inputs for session {sessionId}: P1: {p1Input?.ToString() ?? "N/A"}, P2: {p2Input?.ToString() ?? "N/A"}");
                }

                // We don't need to remove the paddle positions here, they'll be overwritten with new positions
                // or will expire based on the cache configuration in PaddlePositionCache

                return Task.FromResult((p1Input, p2Input));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving and clearing inputs for session {sessionId}.");
                return Task.FromResult<(float? Player1Input, float? Player2Input)>((null, null));
            }
        }

        public Task<long> GetMatchmakingQueueSizeAsync()
        {
            try
            {
                return GetDatabase().ListLengthAsync(MATCHMAKING_QUEUE_KEY);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting matchmaking queue size.");
                return Task.FromResult(-1L); // Indicate error
            }
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