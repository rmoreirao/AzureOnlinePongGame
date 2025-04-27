using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.Mvc;
using AzureOnlinePongGame.Models;

namespace AzureOnlinePongGame.Game
{
    public static class PongHub
    {
        // In-memory matchmaking state
        private static ConcurrentQueue<string> waitingPlayers = new ConcurrentQueue<string>();
        // In-memory game timer management
        private static ConcurrentDictionary<string, Timer> gameTimers = new ConcurrentDictionary<string, Timer>();
        // Track active connections for health monitoring and cleanup
        private static ConcurrentDictionary<string, DateTime> activeConnections = new ConcurrentDictionary<string, DateTime>();
        // Track message failures for diagnostics
        private static ConcurrentDictionary<string, int> messageFailures = new ConcurrentDictionary<string, int>();

        private const int GAME_LOOP_INTERVAL_MS = 33; // ~30 FPS
        // Maximum retries for sending messages
        private const int MAX_SEND_RETRIES = 3;
        // Optional - reduce update frequency to reduce network load
        private const int UPDATE_THROTTLE_MS = 50; // Consider making this configurable
        private static DateTime lastUpdateTime = DateTime.MinValue; // Note: This is shared across all games, might need per-game throttling if many games run concurrently

        [FunctionName("OnConnected")]
        public static Task OnConnected(
            [SignalRTrigger("pong", "connections", "connected")] InvocationContext invocationContext,
            ILogger log)
        {
            string connectionId = invocationContext.ConnectionId;
            log.LogInformation($"Client connected: {connectionId}");
            activeConnections[connectionId] = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        [FunctionName("OnDisconnected")]
        public static async Task OnDisconnected(
            [SignalRTrigger("pong", "connections", "disconnected")] InvocationContext invocationContext,
            ILogger log)
        {
            string connectionId = invocationContext.ConnectionId;
            log.LogInformation($"Client disconnected: {connectionId}");
            activeConnections.TryRemove(connectionId, out _);
            messageFailures.TryRemove(connectionId, out _);

            var newWaitingQueue = new ConcurrentQueue<string>();
            while (waitingPlayers.TryDequeue(out var playerId))
            {
                if (playerId != connectionId)
                {
                    newWaitingQueue.Enqueue(playerId);
                }
            }
            waitingPlayers = newWaitingQueue;

            var session = await GameStateService.GetSessionAsync(connectionId, log);
            if (session != null)
            {
                string opponentId = session.Player1Id == connectionId ? session.Player2Id : session.Player1Id;
                if (gameTimers.TryRemove(session.Player1Id, out var timer))
                {
                    timer.Dispose();
                    log.LogInformation($"Stopped game timer for session involving {connectionId}");
                    gameTimers.TryRemove(opponentId, out _);
                }
            }
            else
            {
                if (gameTimers.TryRemove(connectionId, out var timer))
                {
                    timer.Dispose();
                    log.LogWarning($"Stopped orphaned game timer for disconnected player {connectionId}");
                }
            }
        }

        [FunctionName("JoinMatchmaking")]
        public static async Task JoinMatchmaking(
            [SignalRTrigger("pong", "messages", "JoinMatchmaking")] InvocationContext invocationContext,
            [SignalR(HubName = "pong")] IAsyncCollector<SignalRMessage> signalRMessages,
            ILogger log)
        {
            string playerId = invocationContext.ConnectionId;
            log.LogInformation($"Player {playerId} requested matchmaking.");

            var existingSession = await GameStateService.GetSessionAsync(playerId, log);
            if (existingSession != null && !existingSession.State.GameOver)
            {
                log.LogWarning($"Player {playerId} already in active session, not joining matchmaking");
                return;
            }

            waitingPlayers.Enqueue(playerId);
            log.LogInformation($"Player {playerId} added to queue. Queue size: {waitingPlayers.Count}");

            if (waitingPlayers.Count >= 2)
            {
                if (waitingPlayers.TryDequeue(out var p1) && waitingPlayers.TryDequeue(out var p2))
                {
                    log.LogInformation($"Attempting to match {p1} and {p2}");
                    if (!activeConnections.ContainsKey(p1))
                    {
                        log.LogWarning($"Player {p1} disconnected before match could start. Re-queuing {p2}.");
                        if (activeConnections.ContainsKey(p2)) waitingPlayers.Enqueue(p2);
                        return;
                    }
                    if (!activeConnections.ContainsKey(p2))
                    {
                        log.LogWarning($"Player {p2} disconnected before match could start. Re-queuing {p1}.");
                        if (activeConnections.ContainsKey(p1)) waitingPlayers.Enqueue(p1);
                        return;
                    }

                    var session = new GameSession { Player1Id = p1, Player2Id = p2 };
                    session.State = new GameState();
                    GameEngine.ResetBall(session.State, new Random().Next(0, 2) == 0 ? 1 : -1);

                    if (await GameStateService.UpdateSessionForBothPlayersAsync(session, log))
                    {
                        log.LogInformation($"Session created for {p1} and {p2}. Starting game loop.");
                        var timer = new Timer(async state => await GameLoopRedisWithRetry(state as GameSession, signalRMessages, log),
                                              session,
                                              0,
                                              GAME_LOOP_INTERVAL_MS);

                        gameTimers[p1] = timer;
                        gameTimers[p2] = timer;

                        await SendSignalRMessageWithRetry(new SignalRMessage
                        {
                            Target = "MatchFound",
                            Arguments = new object[] { new Dictionary<string, object> { ["opponent"] = p2, ["side"] = 1 } },
                            ConnectionId = p1
                        }, signalRMessages, log);

                        await SendSignalRMessageWithRetry(new SignalRMessage
                        {
                            Target = "MatchFound",
                            Arguments = new object[] { new Dictionary<string, object> { ["opponent"] = p1, ["side"] = 2 } },
                            ConnectionId = p2
                        }, signalRMessages, log);
                    }
                    else
                    {
                        log.LogError($"Failed to create/store session for players {p1} and {p2}. Re-queuing if connected.");
                        if (activeConnections.ContainsKey(p1)) waitingPlayers.Enqueue(p1);
                        if (activeConnections.ContainsKey(p2)) waitingPlayers.Enqueue(p2);
                    }
                }
            }
            else
            {
                log.LogInformation($"Player {playerId} is waiting for an opponent.");
            }
        }

        [FunctionName("StartBotMatch")]
        public static async Task StartBotMatch(
            [SignalRTrigger("pong", "messages", "StartBotMatch")] InvocationContext invocationContext,
            [SignalR(HubName = "pong")] IAsyncCollector<SignalRMessage> signalRMessages,
            ILogger log)
        {
            string playerId = invocationContext.ConnectionId;
            string botId = $"bot_{Guid.NewGuid()}";
            log.LogInformation($"Player {playerId} requested bot match.");

            var existingSession = await GameStateService.GetSessionAsync(playerId, log);
            if (existingSession != null && !existingSession.State.GameOver)
            {
                log.LogWarning($"Player {playerId} already in active session, cannot start bot match");
                return;
            }

            var session = new GameSession { Player1Id = playerId, Player2Id = botId };
            session.State = new GameState();
            GameEngine.ResetBall(session.State, 1);

            if (await GameStateService.StoreSessionAsync(playerId, session, log))
            {
                log.LogInformation($"Bot session created for {playerId}. Starting game loop.");
                var timer = new Timer(async state => await GameLoopWithBotWithRetry(state as GameSession, signalRMessages, log),
                                      session,
                                      0,
                                      GAME_LOOP_INTERVAL_MS);
                gameTimers[playerId] = timer;

                await SendSignalRMessageWithRetry(new SignalRMessage
                {
                    Target = "MatchFound",
                    Arguments = new object[] { new Dictionary<string, object> { ["opponent"] = "Bot", ["side"] = 1, ["isBot"] = true } },
                    ConnectionId = playerId
                }, signalRMessages, log);
            }
            else
            {
                log.LogError($"Failed to create/store bot session for player {playerId}");
            }
        }

        private static async Task SendSignalRMessageWithRetry(SignalRMessage message, IAsyncCollector<SignalRMessage> signalRMessages, ILogger log)
        {
            if (message.ConnectionId != null && !activeConnections.ContainsKey(message.ConnectionId))
            {
                log.LogWarning($"Attempted to send message to disconnected client {message.ConnectionId}. Target: {message.Target}");
                return;
            }

            int attempts = 0;
            bool success = false;

            while (!success && attempts < MAX_SEND_RETRIES)
            {
                try
                {
                    attempts++;
                    await signalRMessages.AddAsync(message);
                    success = true;

                    if (message.ConnectionId != null)
                    {
                        messageFailures.TryRemove(message.ConnectionId, out _);
                    }
                }
                catch (Exception ex)
                {
                    if (message.ConnectionId != null)
                    {
                        messageFailures.AddOrUpdate(message.ConnectionId, 1, (_, count) => count + 1);
                    }

                    log.LogWarning($"Attempt {attempts} failed to send message (Target: {message.Target}, ConnId: {message.ConnectionId ?? "N/A"}): {ex.Message}");
                    if (attempts < MAX_SEND_RETRIES)
                    {
                        await Task.Delay(100 * attempts);
                    }
                    else
                    {
                        log.LogError(ex, $"Failed to send message (Target: {message.Target}, ConnId: {message.ConnectionId ?? "N/A"}) after {MAX_SEND_RETRIES} attempts");
                    }
                }
            }
        }

        private static async Task GameLoopWithBotWithRetry(GameSession session, IAsyncCollector<SignalRMessage> signalRMessages, ILogger log)
        {
            if (!activeConnections.ContainsKey(session.Player1Id))
            {
                log.LogWarning($"[BotLoop] Player {session.Player1Id} disconnected. Stopping loop.");
                if (gameTimers.TryRemove(session.Player1Id, out var timer)) timer.Dispose();
                return;
            }

            try
            {
                await GameLoopWithBot(session, signalRMessages, log);
            }
            catch (ObjectDisposedException)
            {
                log.LogInformation($"[BotLoop] Timer disposed for session involving {session.Player1Id}. Loop stopped.");
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"[BotLoop] Error in bot game loop for session {session.Player1Id}");
            }
        }

        private static async Task GameLoopRedisWithRetry(GameSession session, IAsyncCollector<SignalRMessage> signalRMessages, ILogger log)
        {
            if (!activeConnections.ContainsKey(session.Player1Id) || !activeConnections.ContainsKey(session.Player2Id))
            {
                log.LogWarning($"[RedisLoop] One player disconnected from session {session.Player1Id}:{session.Player2Id}. Stopping loop.");
                if (gameTimers.TryRemove(session.Player1Id, out var timer))
                {
                    timer.Dispose();
                    gameTimers.TryRemove(session.Player2Id, out _);
                }
                return;
            }

            try
            {
                await GameLoopRedis(session, signalRMessages, log);
            }
            catch (ObjectDisposedException)
            {
                log.LogInformation($"[RedisLoop] Timer disposed for session {session.Player1Id}:{session.Player2Id}. Loop stopped.");
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"[RedisLoop] Error in Redis game loop for session {session.Player1Id}:{session.Player2Id}");
            }
        }

        private static async Task GameLoopWithBot(GameSession session, IAsyncCollector<SignalRMessage> signalRMessages, ILogger log)
        {
            var currentSession = await GameStateService.GetSessionAsync(session.Player1Id, log);
            if (currentSession == null)
            {
                log.LogWarning($"[BotLoop] Session for {session.Player1Id} is null. Stopping loop.");
                if (gameTimers.TryRemove(session.Player1Id, out var timer)) timer.Dispose();
                return;
            }

            var state = currentSession.State;
            if (state.GameOver)
            {
                log.LogInformation($"[BotLoop] Game over for bot session {session.Player1Id}. Stopping loop.");
                if (gameTimers.TryRemove(session.Player1Id, out var timer)) timer.Dispose();
                return;
            }

            state = GameEngine.UpdateBotPaddle(state);
            state = GameEngine.UpdateGameState(state);

            currentSession.State = state;
            currentSession.LastUpdateTime = DateTime.UtcNow;
            await GameStateService.StoreSessionAsync(session.Player1Id, currentSession, log);

            await SendSignalRMessageWithRetry(new SignalRMessage
            {
                Target = "GameUpdate",
                Arguments = new object[] { state },
                ConnectionId = currentSession.Player1Id
            }, signalRMessages, log);

            if (state.GameOver)
            {
                log.LogInformation($"[BotLoop] Game finished for bot session {session.Player1Id}. Score: {state.Player1Score}-{state.Player2Score}. Stopping loop.");
                if (gameTimers.TryRemove(session.Player1Id, out var timer)) timer.Dispose();
                await SendSignalRMessageWithRetry(new SignalRMessage { Target = "GameOver", Arguments = new object[] { state }, ConnectionId = currentSession.Player1Id }, signalRMessages, log);
            }
        }

        private static async Task GameLoopRedis(GameSession session, IAsyncCollector<SignalRMessage> signalRMessages, ILogger log)
        {
            var currentSession = await GameStateService.GetSessionAsync(session.Player1Id, log);
            if (currentSession == null)
            {
                log.LogWarning($"[RedisLoop] Session for {session.Player1Id} is null. Stopping loop.");
                if (gameTimers.TryRemove(session.Player1Id, out var timer)) { timer.Dispose(); gameTimers.TryRemove(session.Player2Id, out _); }
                return;
            }

            var state = currentSession.State;
            if (state.GameOver)
            {
                log.LogInformation($"[RedisLoop] Game over for session {session.Player1Id}:{session.Player2Id}. Stopping loop.");
                if (gameTimers.TryRemove(session.Player1Id, out var timer)) { timer.Dispose(); gameTimers.TryRemove(session.Player2Id, out _); }
                return;
            }

            state = GameEngine.UpdateGameState(state);

            currentSession.State = state;
            currentSession.LastUpdateTime = DateTime.UtcNow;
            await GameStateService.UpdateSessionForBothPlayersAsync(currentSession, log);

            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug($"[RedisLoop] State: P1Y={state.Player1PaddleY:0}, P2Y={state.Player2PaddleY:0}, Ball=({state.BallX:0},{state.BallY:0}), Score={state.Player1Score}:{state.Player2Score}");
            }

            await SendSignalRMessageWithRetry(new SignalRMessage
            {
                Target = "GameUpdate",
                Arguments = new object[] { state },
                ConnectionId = currentSession.Player1Id
            }, signalRMessages, log);

            await SendSignalRMessageWithRetry(new SignalRMessage
            {
                Target = "GameUpdate",
                Arguments = new object[] { state },
                ConnectionId = currentSession.Player2Id
            }, signalRMessages, log);

            if (state.GameOver)
            {
                log.LogInformation($"[RedisLoop] Game finished for session {session.Player1Id}:{session.Player2Id}. Score: {state.Player1Score}-{state.Player2Score}. Stopping loop.");
                if (gameTimers.TryRemove(session.Player1Id, out var timer)) { timer.Dispose(); gameTimers.TryRemove(session.Player2Id, out _); }
                var gameOverArgs = new object[] { state };
                await SendSignalRMessageWithRetry(new SignalRMessage { Target = "GameOver", Arguments = gameOverArgs, ConnectionId = currentSession.Player1Id }, signalRMessages, log);
                await SendSignalRMessageWithRetry(new SignalRMessage { Target = "GameOver", Arguments = gameOverArgs, ConnectionId = currentSession.Player2Id }, signalRMessages, log);
            }
        }

        [FunctionName("UpdatePaddle")]
        public static async Task UpdatePaddle(
            [SignalRTrigger("pong", "messages", "UpdatePaddle")] InvocationContext invocationContext,
            ILogger log)
        {
            string playerId = invocationContext.ConnectionId;

            try
            {
                var session = await GameStateService.GetSessionAsync(playerId, log);
                if (session == null || session.State.GameOver)
                {
                    if (session == null) log.LogDebug($"Session not found for player {playerId} during paddle update");
                    return;
                }

                int side = session.Player1Id == playerId ? 1 : 2;
                if (invocationContext.Arguments.Length > 0 && invocationContext.Arguments[0] != null)
                {
                    float y;
                    var arg = invocationContext.Arguments[0];

                    bool valid = TryExtractPaddlePosition(arg, out y, log);

                    if (valid)
                    {
                        y = Math.Max(0, Math.Min(GameEngine.CANVAS_HEIGHT - GameEngine.PADDLE_HEIGHT, y));

                        if (side == 1) session.State.Player1PaddleY = y;
                        else session.State.Player2PaddleY = y;

                        if (log.IsEnabled(LogLevel.Trace))
                        {
                            log.LogTrace($"Updated paddle: P{side}={y:0}");
                        }

                        if (session.Player2Id.StartsWith("bot_"))
                        {
                            await GameStateService.StoreSessionAsync(session.Player1Id, session, log);
                        }
                        else
                        {
                            await GameStateService.UpdateSessionForBothPlayersAsync(session, log);
                        }
                    }
                    else
                    {
                        log.LogWarning($"Invalid paddle position data type from {playerId}: {arg.GetType().Name}");
                    }
                }
                else
                {
                    log.LogWarning($"Missing paddle position data from {playerId}");
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"Error processing paddle update from {playerId}");
            }
        }

        private static bool TryExtractPaddlePosition(object arg, out float position, ILogger log)
        {
            position = 0;

            try
            {
                if (arg is float f) { position = f; return true; }
                if (arg is double d) { position = (float)d; return true; }
                if (arg is int i) { position = i; return true; }
                if (arg is long l) { position = l; return true; }

                if (arg is JObject jobj && jobj.TryGetValue("y", StringComparison.OrdinalIgnoreCase, out var yToken))
                {
                    if (yToken.Type == JTokenType.Float) { position = yToken.Value<float>(); return true; }
                    if (yToken.Type == JTokenType.Integer) { position = yToken.Value<int>(); return true; }
                }

                if (arg is string s && float.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out position))
                {
                    return true;
                }
            }
            catch (FormatException)
            {
                log?.LogWarning($"Could not format paddle position from {arg?.GetType().Name ?? "null"} with value '{arg}'");
            }
            catch (InvalidCastException)
            {
                log?.LogWarning($"Could not cast paddle position from {arg?.GetType().Name ?? "null"} with value '{arg}'");
            }
            catch (Exception ex)
            {
                log?.LogError(ex, $"Unexpected error extracting paddle position from {arg?.GetType().Name ?? "null"} with value '{arg}'");
            }

            return false;
        }

        [FunctionName("HealthCheck")]
        public static IActionResult HealthCheck(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "healthcheck")] Microsoft.AspNetCore.Http.HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Health check requested");

            // Basic check for Redis connectivity using the public method
            bool redisConnected = GameStateService.IsRedisConnected(out string redisError);

            var healthData = new
            {
                status = redisConnected ? "Healthy" : "Degraded",
                timestamp = DateTime.UtcNow,
                dependencies = new
                {
                    redisConnected = redisConnected,
                    redisError = redisError ?? "N/A" // Use the error message if provided
                },
                metrics = new
                {
                    activeConnections = activeConnections.Count,
                    waitingPlayers = waitingPlayers.Count,
                    activeGames = gameTimers.Count / 2, // Approximation for multiplayer games
                    messageFailureCounts = messageFailures // Provides counts per connection ID
                }
            };

            return new OkObjectResult(healthData);
        }

        [FunctionName("KeepAlive")]
        public static async Task KeepAlive(
            [SignalRTrigger("pong", "messages", "KeepAlive")] InvocationContext invocationContext,
            [SignalR(HubName = "pong")] IAsyncCollector<SignalRMessage> signalRMessages,
            ILogger log)
        {
            string connectionId = invocationContext.ConnectionId;
            if (activeConnections.ContainsKey(connectionId))
            {
                activeConnections[connectionId] = DateTime.UtcNow;

                await SendSignalRMessageWithRetry(new SignalRMessage
                {
                    Target = "Pong",
                    Arguments = new object[] { DateTime.UtcNow },
                    ConnectionId = connectionId
                }, signalRMessages, log);
            }
            else
            {
                log.LogWarning($"Received KeepAlive from unknown or disconnected connection: {connectionId}");
            }
        }
    }
}