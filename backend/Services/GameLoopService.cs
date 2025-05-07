using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using AzureOnlinePongGame.Services;
using Microsoft.AspNetCore.SignalR;
using AzureOnlinePongGame.Models;
using System.Collections.Generic;

namespace AzureOnlinePongGame.Services
{
    public class GameLoopService : BackgroundService
    {
        private readonly GameStateService _gameStateService;
        private readonly IHubContext<PongHub> _hubContext;
        private readonly ILogger<GameLoopService> _logger;
        
        // Default interval at ~30 FPS
        private TimeSpan _baseInterval = TimeSpan.FromMilliseconds(33);
        
        // State update intervals (send to Redis less frequently)
        private readonly TimeSpan _stateUpdateInterval = TimeSpan.FromMilliseconds(100);
        
        // Client sync interval (send to clients even less frequently)
        private readonly TimeSpan _stateSyncInterval = TimeSpan.FromMilliseconds(100);
        
        private DateTime _lastStateSyncTime = DateTime.MinValue;
        private const float DELTA_TIME = 0.033f; // 33ms per tick
        
        // Cache of active sessions to reduce Redis calls
        private readonly Dictionary<string, (GameSession Session, DateTime LastUpdate)> _sessionCache = 
            new Dictionary<string, (GameSession, DateTime)>();
        
        // Track when the cache was last refreshed
        private DateTime _lastCacheRefresh = DateTime.MinValue;
        private readonly TimeSpan _cacheRefreshInterval = TimeSpan.FromSeconds(5);

        public GameLoopService(GameStateService gameStateService, IHubContext<PongHub> hubContext, ILogger<GameLoopService> logger)
        {
            _gameStateService = gameStateService;
            _hubContext = hubContext;
            _logger = logger;
        }

        // Calculate optimal interval based on number of active games
        private TimeSpan CalculateOptimalInterval(int activeGameCount)
        {
            // With no active games, run much less frequently (e.g., every 500ms)
            if (activeGameCount == 0)
                return TimeSpan.FromMilliseconds(500);
            
            // With few games, run less frequently (e.g., every 66ms)
            if (activeGameCount < 3)
                return TimeSpan.FromMilliseconds(66);
                
            // Normal interval for active games
            return _baseInterval;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Game loop background service started.");
            var currentInterval = _baseInterval;
            
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    bool shouldSyncState = (now - _lastStateSyncTime) >= _stateSyncInterval;
                    bool shouldRefreshCache = (now - _lastCacheRefresh) >= _cacheRefreshInterval;
                    
                    // Refresh our cache of active sessions only periodically
                    if (shouldRefreshCache || _sessionCache.Count == 0)
                    {
                        await RefreshSessionCacheAsync();
                        _lastCacheRefresh = now;
                    }
                    
                    // Process each game session
                    var activeSessions = new List<GameSession>();
                    var updatedSessions = new List<GameSession>();
                    
                    foreach (var cacheEntry in _sessionCache)
                    {
                        var session = cacheEntry.Value.Session;
                        
                        // Skip inactive games
                        if (session.State.GameOver || !session.State.PlayersReady)
                            continue;
                            
                        activeSessions.Add(session);
                        
                        // Get player inputs
                        var (leftInput, rightInput) = await _gameStateService.GetAndClearPlayerInputsAsync(
                            session.SessionId, session.Player1Id, session.Player2Id);
                            
                        bool stateChanged = false;
                        
                        // Apply inputs if provided
                        if (leftInput.HasValue)
                        {
                            session.State.LeftPaddleTargetY = leftInput.Value;
                            stateChanged = true;
                        }
                        
                        if (rightInput.HasValue)
                        {
                            session.State.RightPaddleTargetY = rightInput.Value;
                            stateChanged = true;
                        }
                        
                        // Bot games: update bot paddle
                        bool isBot = session.Player2Id != null && session.Player2Id.StartsWith("bot_");
                        if (isBot)
                        {
                            session.State = GameEngine.UpdateBotPaddle(session.State);
                            stateChanged = true;
                        }
                        
                        // Update game state with deltaTime
                        var oldBallX = session.State.Ball.X;
                        var oldBallY = session.State.Ball.Y;
                        var oldLeftScore = session.State.LeftScore;
                        var oldRightScore = session.State.RightScore;
                        
                        session.State = GameEngine.UpdateGameState(session.State, DELTA_TIME);
                        
                        // Check if anything important changed
                        if (oldBallX != session.State.Ball.X || 
                            oldBallY != session.State.Ball.Y ||
                            oldLeftScore != session.State.LeftScore ||
                            oldRightScore != session.State.RightScore ||
                            stateChanged)
                        {
                            session.State.NeedsUpdate = true;
                            updatedSessions.Add(session);
                            
                            // If something significant changed, update the client immediately
                            if (oldLeftScore != session.State.LeftScore || 
                                oldRightScore != session.State.RightScore ||
                                session.State.GameOver)
                            {
                                if (!string.IsNullOrEmpty(session.Player1Id))
                                    await _hubContext.Clients.Client(session.Player1Id).SendAsync("GameUpdate", session.State);
                                    
                                if (!isBot && !string.IsNullOrEmpty(session.Player2Id))
                                    await _hubContext.Clients.Client(session.Player2Id).SendAsync("GameUpdate", session.State);
                                    
                                session.State.LastClientSyncTime = now;
                            }
                        }
                        
                        // Update session in cache
                        _sessionCache[cacheEntry.Key] = (session, now);
                    }
                    
                    // Periodic state sync to clients (position updates)
                    if (shouldSyncState)
                    {
                        foreach (var session in activeSessions)
                        {
                            // Send updates to clients at a lower frequency
                            if ((now - session.State.LastClientSyncTime) >= _stateSyncInterval)
                            {
                                if (!string.IsNullOrEmpty(session.Player1Id))
                                    await _hubContext.Clients.Client(session.Player1Id).SendAsync("GameUpdate", session.State);
                                    
                                bool isBot = session.Player2Id != null && session.Player2Id.StartsWith("bot_");
                                if (!isBot && !string.IsNullOrEmpty(session.Player2Id))
                                    await _hubContext.Clients.Client(session.Player2Id).SendAsync("GameUpdate", session.State);
                                    
                                session.State.LastClientSyncTime = now;
                            }
                        }
                        _lastStateSyncTime = now;
                    }
                    
                    // Batch update any changed sessions to Redis
                    if (updatedSessions.Count > 0)
                    {
                        foreach (var session in updatedSessions)
                        {
                            if (session.State.NeedsUpdate)
                            {
                                await _gameStateService.UpdateSessionForBothPlayersAsync(session);
                                session.State.NeedsUpdate = false;
                            }
                        }
                    }
                    
                    // Calculate the optimal interval based on activity
                    currentInterval = CalculateOptimalInterval(activeSessions.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in game loop execution.");
                    currentInterval = TimeSpan.FromMilliseconds(100); // Back off on error
                }
                
                await Task.Delay(currentInterval, stoppingToken);
            }
            _logger.LogInformation("Game loop background service stopped.");
        }
        
        // Refresh the cache of active game sessions from Redis
        private async Task RefreshSessionCacheAsync()
        {
            try
            {
                var sessions = await _gameStateService.GetAllActiveSessionsAsync();
                var now = DateTime.UtcNow;
                
                // Update cache with fresh sessions
                foreach (var session in sessions)
                {
                    _sessionCache[session.SessionId] = (session, now);
                }
                
                // Remove stale sessions
                var staleSessions = new List<string>();
                foreach (var key in _sessionCache.Keys)
                {
                    if (!sessions.Exists(s => s.SessionId == key))
                    {
                        staleSessions.Add(key);
                    }
                }
                
                foreach (var key in staleSessions)
                {
                    _sessionCache.Remove(key);
                }
                
                _logger.LogDebug($"Session cache refreshed. Active sessions: {_sessionCache.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing session cache");
            }
        }
    }
}
