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

        // Constants for paddle update throttling
        private const int PADDLE_UPDATE_THROTTLE_MS = 50; // Throttle updates to 20 updates per second

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
            
            // Store last update time to throttle
            string cacheKey = $"paddle_update_time:{playerId}";
            
            // Throttle updates going to Redis
            if (!_memoryCache.TryGetValue(cacheKey, out _))
            {
                // Apply update to Redis if throttle expired
                await ApplyPaddleUpdateAsync(playerId, targetY);
                
                // Store update time with sliding expiration
                _memoryCache.Set(cacheKey, DateTime.UtcNow, 
                    new MemoryCacheEntryOptions().SetAbsoluteExpiration(
                        TimeSpan.FromMilliseconds(PADDLE_UPDATE_THROTTLE_MS)));
            }
        }
        
        private async Task ApplyPaddleUpdateAsync(string playerId, float paddleY)
        {
            try
            {
                var session = await _gameStateService.GetSessionAsync(playerId);
                if (session == null || session.State.GameOver)
                {
                    if (session == null) 
                        _logger.LogDebug($"[UpdatePaddle] Session not found for player {playerId}. Ignoring update.");
                    else 
                        _logger.LogDebug($"[UpdatePaddle] Game already over for player {playerId}. Ignoring update.");
                    return;
                }
                
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
                
                // Indicate state needs update
                session.State.NeedsUpdate = true;
                
                // Update Redis (now less frequently due to throttling)
                await _gameStateService.UpdateSessionForBothPlayersAsync(session);
                
                // Also send to opponent directly for better responsiveness
                string opponentId = side == 1 ? session.Player2Id : session.Player1Id;
                if (!string.IsNullOrEmpty(opponentId) && !opponentId.StartsWith("bot_"))
                {
                    await Clients.Client(opponentId).SendAsync("OpponentPaddleInput", paddleY);
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

            // Clean up cached paddle updates 
            _paddleUpdateCache.TryRemove(connectionId, out _);

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
