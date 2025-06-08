using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using AzureOnlinePongGame.Services;
using Microsoft.AspNetCore.SignalR;
using AzureOnlinePongGame.Models;
using System.Collections.Generic;
using System.Linq;

namespace AzureOnlinePongGame.Services
{
    public class GameLoopService : BackgroundService
    {private readonly GameStateService _gameStateService;
        private readonly IHubContext<PongHub> _hubContext;
        private readonly ILogger<GameLoopService> _logger;
        
        // Default interval at ~30 FPS
        private TimeSpan _baseInterval = TimeSpan.FromMilliseconds(33);
        
        // Reduced: State update intervals (send to Redis less frequently)
        private readonly TimeSpan _stateUpdateInterval = TimeSpan.FromMilliseconds(500); // Increased from 100ms to 500ms
        
        // Client sync interval (send to clients even less frequently)
        private readonly TimeSpan _stateSyncInterval = TimeSpan.FromMilliseconds(100);
        
        private DateTime _lastStateSyncTime = DateTime.MinValue;
        private DateTime _lastRedisUpdateTime = DateTime.MinValue; // Track last Redis update
        private const float DELTA_TIME = 0.033f; // 33ms per tick
        
        // Cache of active sessions to reduce Redis calls
        private readonly Dictionary<string, (GameSession Session, DateTime LastUpdate)> _sessionCache = 
            new Dictionary<string, (GameSession, DateTime)>();
        
        // Track when the cache was last refreshed
        private DateTime _lastCacheRefresh = DateTime.MinValue;
        private readonly TimeSpan _cacheRefreshInterval = TimeSpan.FromSeconds(5);
        
        // Track sessions that need to be persisted to Redis
        private readonly HashSet<string> _sessionsWithCriticalChanges = new HashSet<string>();

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
                    bool shouldUpdateRedis = (now - _lastRedisUpdateTime) >= _stateUpdateInterval;
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
                    
                    foreach (var cacheEntry in _sessionCache.ToList()) // ToList() to allow modification if a session ends
                    {
                        var session = cacheEntry.Value.Session;
                        var sessionId = session.SessionId;

                        // Skip inactive or non-existent games
                        if (session.State.GameOver || !session.State.PlayersReady)
                        {
                            // Optional: Clean up ended sessions from cache if not handled by RefreshSessionCacheAsync timely
                            if (session.State.GameOver && (now - cacheEntry.Value.LastUpdate) > TimeSpan.FromSeconds(30)) // Example cleanup delay
                            {
                                _sessionCache.Remove(sessionId);
                                _sessionsWithCriticalChanges.Remove(sessionId);
                            }
                            continue;
                        }
                            
                        activeSessions.Add(session);
                        
                        // Get player inputs from Redis (new step)
                        var (player1Input, player2Input) = await _gameStateService.GetAndClearPlayerInputsAsync(
                            sessionId, session.Player1Id, session.Player2Id);
                            
                        bool stateChangedByInput = false;
                        
                        // Apply inputs if provided
                        if (player1Input.HasValue)
                        {
                            session.State.LeftPaddleTargetY = player1Input.Value;
                            stateChangedByInput = true;
                        }
                        
                        if (player2Input.HasValue) // Bot inputs are handled by UpdateBotPaddle
                        {
                            session.State.RightPaddleTargetY = player2Input.Value;
                            stateChangedByInput = true;
                        }
                        
                        // Bot games: update bot paddle
                        bool isBot = session.Player2Id != null && session.Player2Id.StartsWith("bot_");
                        if (isBot)
                        {
                            session.State = GameEngine.UpdateBotPaddle(session.State); // This sets RightPaddleTargetY for bot
                            // stateChangedByInput = true; // Bot movement is part of regular engine update cycle
                        }
                        
                        // Update game state with deltaTime
                        var oldBallX = session.State.Ball.X;
                        var oldBallY = session.State.Ball.Y;
                        var oldLeftScore = session.State.LeftScore;
                        var oldRightScore = session.State.RightScore;
                        var oldGameOver = session.State.GameOver;
                        
                        session.State = GameEngine.UpdateGameState(session.State, DELTA_TIME);
                        
                        bool criticalStateChange = false;
                        // Check if score or game state changed - these are critical changes
                        if (oldLeftScore != session.State.LeftScore || 
                            oldRightScore != session.State.RightScore || 
                            oldGameOver != session.State.GameOver)
                        {
                            criticalStateChange = true;
                            _sessionsWithCriticalChanges.Add(session.SessionId);
                            
                            // Always update Redis immediately for critical changes
                            await _gameStateService.UpdateSessionForBothPlayersAsync(session);
                        }
                        
                        // Check if anything important changed (ball movement, input-driven paddle change, or critical change)
                        if (stateChangedByInput || 
                            oldBallX != session.State.Ball.X || 
                            oldBallY != session.State.Ball.Y ||
                            criticalStateChange) // criticalStateChange implies something important changed
                        {
                            session.State.NeedsUpdate = true;
                            updatedSessions.Add(session);
                            
                            // If something significant changed (critical or new input affecting paddles), update the client immediately
                            if (criticalStateChange || stateChangedByInput)
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
                    
                    // Batch update Redis less frequently
                    if (shouldUpdateRedis && updatedSessions.Count > 0)
                    {
                        var sessionsToUpdate = updatedSessions
                            .Where(s => s.State.NeedsUpdate && !_sessionsWithCriticalChanges.Contains(s.SessionId))
                            .ToList();
                            
                        foreach (var session in sessionsToUpdate)
                        {
                            await _gameStateService.UpdateSessionForBothPlayersAsync(session);
                            session.State.NeedsUpdate = false;
                        }
                        
                        _lastRedisUpdateTime = now;
                        _sessionsWithCriticalChanges.Clear();
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
