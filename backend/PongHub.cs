using Microsoft.AspNetCore.SignalR;
using AzureOnlinePongGame.Services;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System;

namespace AzureOnlinePongGame
{
    public class PongHub : Hub
    {
        private readonly GameStateService _gameStateService;
        private readonly ILogger<PongHub> _logger;
        private readonly IMemoryCache _memoryCache;
        
        // Memory cache for paddle updates to reduce Redis write frequency
        private static readonly ConcurrentDictionary<string, float> _paddleUpdateCache = new();
        
        // In-memory mapping of player connections to sessions for faster lookups
        private static readonly ConcurrentDictionary<string, string> _playerSessionMap = new();
        
        // Track paddle positions in memory to avoid Redis reads
        private static readonly ConcurrentDictionary<string, (string SessionId, int Side, float Position)> _playerPaddlePositions = new();

        // Constants for paddle update throttling
        private const int PADDLE_UPDATE_THROTTLE_MS = 30; // ~33 updates per second
        
        // Constants for Redis persistence throttling
        private const int REDIS_PERSISTENCE_THROTTLE_MS = 500; // Persist to Redis every 500ms

        public PongHub(GameStateService gameStateService, ILogger<PongHub> logger, IMemoryCache memoryCache)
        {
            _gameStateService = gameStateService;
            _logger = logger;
            _memoryCache = memoryCache;
        }

        public async Task JoinMatchmaking()
        {
            var playerId = Context.ConnectionId;
            _logger.LogInformation($"Player {playerId} requested matchmaking.");

            var existingSession = await _gameStateService.GetSessionAsync(playerId);
            if (existingSession != null && !existingSession.State.GameOver)
            {
                _logger.LogWarning($"Player {playerId} already in active session {existingSession.Player1Id}:{existingSession.Player2Id}, not joining matchmaking");
                await Clients.Caller.SendAsync("AlreadyInGame");
                return;
            }

            await _gameStateService.AddToMatchmakingAsync(playerId);
            _logger.LogInformation($"Player {playerId} added to matchmaking queue.");

            var matchedSession = await _gameStateService.TryMatchPlayersAsync();
            if (matchedSession != null)
            {
                _logger.LogInformation($"Match found: {matchedSession.Player1Id} vs {matchedSession.Player2Id}. Initializing session.");
                // Game state is initialized in TryMatchPlayersAsync
                await _gameStateService.UpdateSessionForBothPlayersAsync(matchedSession);
                // Notify both players
                await Clients.Client(matchedSession.Player1Id).SendAsync("MatchFound", new { opponent = matchedSession.Player2Id, side = 1 });
                await Clients.Client(matchedSession.Player2Id).SendAsync("MatchFound", new { opponent = matchedSession.Player1Id, side = 2 });
            }
            else
            {
                _logger.LogInformation($"Player {playerId} is waiting for an opponent.");
                await Clients.Caller.SendAsync("WaitingForOpponent");
            }
        }

        public async Task StartBotMatch()
        {
            var playerId = Context.ConnectionId;
            var botId = $"bot_{System.Guid.NewGuid()}";
            _logger.LogInformation($"Player {playerId} requested bot match.");

            var existingSession = await _gameStateService.GetSessionAsync(playerId);
            if (existingSession != null && !existingSession.State.GameOver)
            {
                _logger.LogWarning($"Player {playerId} already in active session, cannot start bot match");
                await Clients.Caller.SendAsync("AlreadyInGame");
                return;
            }

            var session = new Models.GameSession { Player1Id = playerId, Player2Id = botId, State = new Models.GameState(), LastUpdateTime = System.DateTime.UtcNow };
            // Mark both as ready for bot match
            session.State.LeftPlayerReady = true;
            session.State.RightPlayerReady = true;
            await _gameStateService.StoreSessionAsync(playerId, session);
            _logger.LogInformation($"Bot session created for {playerId}. Notifying player.");
            await Clients.Caller.SendAsync("MatchFound", new { opponent = "Bot", side = 1, isBot = true });
        }

        public async Task SendPaddleInput(float targetY)
        {
            var playerId = Context.ConnectionId;
            
            // Clamp to valid range
            targetY = Math.Max(0, Math.Min(600 - 100, targetY));
            
            // Store paddle update in cache
            _paddleUpdateCache[playerId] = targetY;
            
            // Get the player's session info from memory if available
            if (!_playerPaddlePositions.TryGetValue(playerId, out var paddleInfo))
            {
                // If not in memory, get from Redis (should only happen once per connection)
                var session = await _gameStateService.GetSessionAsync(playerId);
                if (session == null)
                {
                    _logger.LogDebug($"No active session found for player {playerId}");
                    return;
                }
                
                // Store in our in-memory map for future lookups
                int side = session.Player1Id == playerId ? 1 : 2;
                paddleInfo = (session.SessionId, side, targetY);
                _playerPaddlePositions[playerId] = paddleInfo;
                _playerSessionMap[playerId] = session.SessionId;
            }
            else
            {
                // Update the cached position
                paddleInfo.Position = targetY;
                _playerPaddlePositions[playerId] = paddleInfo;
            }
            
            // OPTIMIZATION: Directly notify opponent of paddle movement for real-time updates
            // This happens every time without throttling for best responsiveness
            if (!string.IsNullOrEmpty(_playerSessionMap[playerId]))
            {
                // Find the opponent
                var sessionId = _playerSessionMap[playerId];
                string? opponentId = null;
                
                // Get opponent based on our side
                if (paddleInfo.Side == 1)
                {
                    // We're player 1, find player 2
                    opponentId = _playerSessionMap.FirstOrDefault(p => 
                        p.Value == sessionId && p.Key != playerId).Key;
                }
                else
                {
                    // We're player 2, find player 1
                    opponentId = _playerSessionMap.FirstOrDefault(p => 
                        p.Value == sessionId && p.Key != playerId).Key;
                }
                
                // If found, send paddle update directly
                if (!string.IsNullOrEmpty(opponentId) && opponentId != null && !opponentId.StartsWith("bot_"))
                {
                    await Clients.Client(opponentId).SendAsync("OpponentPaddleInput", targetY);
                }
            }
            
            // Throttle Redis persistence to reduce writes
            string persistCacheKey = $"paddle_persist_time:{playerId}";
            
            // Check throttle for Redis persistence
            if (!_memoryCache.TryGetValue(persistCacheKey, out _))
            {
                // Only apply to Redis if throttle expired
                await ApplyPaddleUpdateToRedisAsync(playerId, targetY);
                
                // Store update time with fixed expiration
                _memoryCache.Set(persistCacheKey, DateTime.UtcNow, 
                    new MemoryCacheEntryOptions().SetAbsoluteExpiration(
                        TimeSpan.FromMilliseconds(REDIS_PERSISTENCE_THROTTLE_MS)));
            }
        }
        
        private async Task ApplyPaddleUpdateToRedisAsync(string playerId, float paddleY)
        {
            try
            {
                // Check our in-memory mapping first
                if (!_playerSessionMap.TryGetValue(playerId, out var sessionId))
                {
                    // Fall back to Redis lookup (should only happen once)
                    var session = await _gameStateService.GetSessionAsync(playerId);
                    if (session == null || session.State.GameOver)
                    {
                        if (session == null) 
                            _logger.LogDebug($"[UpdatePaddle] Session not found for player {playerId}. Ignoring update.");
                        else 
                            _logger.LogDebug($"[UpdatePaddle] Game already over for player {playerId}. Ignoring update.");
                        return;
                    }
                    
                    // Cache for future use
                    sessionId = session.SessionId;
                    _playerSessionMap[playerId] = sessionId;
                    
                    // Update the appropriate paddle
                    int side = session.Player1Id == playerId ? 1 : 2;
                    if (side == 1)
                    {
                        session.State.LeftPaddleTargetY = paddleY;
                        session.State.LeftPaddle.Y = paddleY;
                    }
                    else
                    {
                        session.State.RightPaddleTargetY = paddleY;
                        session.State.RightPaddle.Y = paddleY; 
                    }
                    
                    // Store side and position in memory
                    _playerPaddlePositions[playerId] = (sessionId, side, paddleY);
                    
                    // Update Redis (less frequently due to throttling)
                    session.State.NeedsUpdate = true;
                    await _gameStateService.UpdateSessionForBothPlayersAsync(session);
                }
                else
                {
                    // We have the mapping in memory, make more efficient update
                    var session = await _gameStateService.GetSessionAsync(playerId);
                    if (session != null && !session.State.GameOver)
                    {
                        // Update the appropriate paddle
                        if (_playerPaddlePositions.TryGetValue(playerId, out var paddleInfo))
                        {
                            if (paddleInfo.Side == 1)
                            {
                                session.State.LeftPaddleTargetY = paddleY;
                                session.State.LeftPaddle.Y = paddleY;
                            }
                            else
                            {
                                session.State.RightPaddleTargetY = paddleY;
                                session.State.RightPaddle.Y = paddleY; 
                            }
                            
                            // Update Redis (less frequently due to throttling)
                            session.State.NeedsUpdate = true;
                            await _gameStateService.UpdateSessionForBothPlayersAsync(session);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing paddle update from {playerId}");
            }
        }

        public async Task KeepAlive()
        {
            _logger.LogDebug($"Received keepalive from {Context.ConnectionId}");
            await Clients.Caller.SendAsync("Pong", System.DateTime.UtcNow);
        }

        public async Task RequestStartGame()
        {
            var playerId = Context.ConnectionId;
            var session = await _gameStateService.GetSessionAsync(playerId);
            if (session != null)
            {
                if (session.Player1Id == playerId)
                    session.State.LeftPlayerReady = true;
                else if (session.Player2Id == playerId)
                    session.State.RightPlayerReady = true;

                await _gameStateService.UpdateSessionForBothPlayersAsync(session);

                // Optionally notify both players when both are ready
                if (session.State.PlayersReady)
                {
                    await Clients.Client(session.Player1Id).SendAsync("GameStarted");
                    if (!session.Player2Id.StartsWith("bot_"))
                        await Clients.Client(session.Player2Id).SendAsync("GameStarted");
                }
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var connectionId = Context.ConnectionId;
            _logger.LogInformation($"Client disconnected: {connectionId}");

            // Clean up cached paddle updates and mappings
            _paddleUpdateCache.TryRemove(connectionId, out _);
            _playerPaddlePositions.TryRemove(connectionId, out _);
            _playerSessionMap.TryRemove(connectionId, out _);

            await _gameStateService.RemoveFromMatchmakingAsync(connectionId);

            var session = await _gameStateService.GetSessionAsync(connectionId);
            if (session != null && !session.State.GameOver)
            {
                _logger.LogInformation($"Player {connectionId} disconnected during active game session. Ending session.");
                session.State.GameOver = true;
                session.State.Winner = session.Player1Id == connectionId ? 2 : 1;
                session.LastUpdateTime = System.DateTime.UtcNow;

                string opponentId = session.Player1Id == connectionId ? session.Player2Id : session.Player1Id;
                if (!opponentId.StartsWith("bot_"))
                {
                    await Clients.Client(opponentId).SendAsync("OpponentDisconnected", session.State);
                }

                await _gameStateService.UpdateSessionForBothPlayersAsync(session);
            }

            await base.OnDisconnectedAsync(exception);
        }
    }
}
