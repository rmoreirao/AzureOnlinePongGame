using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        private const int GAME_LOOP_INTERVAL_MS = 33; // ~30 FPS
        private const int MAX_SEND_RETRIES = 3;

        [FunctionName("OnConnected")]
        public static Task OnConnected(
            [SignalRTrigger("pong", "connections", "connected")] InvocationContext invocationContext,
            ILogger log)
        {
            string connectionId = invocationContext.ConnectionId;
            log.LogInformation($"Client connected: {connectionId}");
            return Task.CompletedTask;
        }

        [FunctionName("OnDisconnected")]
        public static async Task OnDisconnected(
            [SignalRTrigger("pong", "connections", "disconnected")] InvocationContext invocationContext,
            [SignalR(HubName = "pong")] IAsyncCollector<SignalRMessage> signalRMessages,
            ILogger log)
        {
            string connectionId = invocationContext.ConnectionId;
            log.LogInformation($"Client disconnected: {connectionId}");

            await GameStateService.RemoveFromMatchmakingAsync(connectionId, log);

            var session = await GameStateService.GetSessionAsync(connectionId, log);
            if (session != null && !session.State.GameOver)
            {
                log.LogInformation($"Player {connectionId} disconnected during active game session. Ending session.");
                session.State.GameOver = true;
                session.State.Winner = session.Player1Id == connectionId ? 2 : 1;
                session.LastUpdateTime = DateTime.UtcNow;

                string opponentId = session.Player1Id == connectionId ? session.Player2Id : session.Player1Id;
                if (!opponentId.StartsWith("bot_"))
                {
                    await SendSignalRMessageWithRetry(new SignalRMessage
                    {
                        Target = "OpponentDisconnected",
                        Arguments = new object[] { session.State },
                        ConnectionId = opponentId
                    }, signalRMessages, log);
                }

                await GameStateService.UpdateSessionForBothPlayersAsync(session, log);
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
                log.LogWarning($"Player {playerId} already in active session {existingSession.Player1Id}:{existingSession.Player2Id}, not joining matchmaking");
                await SendSignalRMessageWithRetry(new SignalRMessage
                {
                    Target = "AlreadyInGame",
                    Arguments = new object[] { },
                    ConnectionId = playerId
                }, signalRMessages, log);
                return;
            }

            await GameStateService.AddToMatchmakingAsync(playerId, log);
            log.LogInformation($"Player {playerId} added to matchmaking queue.");

            var matchedSession = await GameStateService.TryMatchPlayersAsync(log);

            if (matchedSession != null)
            {
                log.LogInformation($"Match found: {matchedSession.Player1Id} vs {matchedSession.Player2Id}. Initializing session.");

                matchedSession.State = new GameState();
                GameEngine.ResetBall(matchedSession.State, new Random().Next(0, 2) == 0 ? 1 : -1);

                if (await GameStateService.UpdateSessionForBothPlayersAsync(matchedSession, log))
                {
                    log.LogInformation($"Session created and stored for {matchedSession.Player1Id} and {matchedSession.Player2Id}. Notifying players.");

                    await SendSignalRMessageWithRetry(new SignalRMessage
                    {
                        Target = "MatchFound",
                        Arguments = new object[] { new Dictionary<string, object> { ["opponent"] = matchedSession.Player2Id, ["side"] = 1 } },
                        ConnectionId = matchedSession.Player1Id
                    }, signalRMessages, log);

                    await SendSignalRMessageWithRetry(new SignalRMessage
                    {
                        Target = "MatchFound",
                        Arguments = new object[] { new Dictionary<string, object> { ["opponent"] = matchedSession.Player1Id, ["side"] = 2 } },
                        ConnectionId = matchedSession.Player2Id
                    }, signalRMessages, log);
                }
                else
                {
                    log.LogError($"Failed to store initial session for matched players {matchedSession.Player1Id} and {matchedSession.Player2Id}. Matchmaking attempt failed.");
                }
            }
            else
            {
                log.LogInformation($"Player {playerId} is waiting for an opponent.");
                await SendSignalRMessageWithRetry(new SignalRMessage
                {
                    Target = "WaitingForOpponent",
                    Arguments = new object[] { },
                    ConnectionId = playerId
                }, signalRMessages, log);
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
                await SendSignalRMessageWithRetry(new SignalRMessage
                {
                    Target = "AlreadyInGame",
                    Arguments = new object[] { },
                    ConnectionId = playerId
                }, signalRMessages, log);
                return;
            }

            var session = new GameSession { Player1Id = playerId, Player2Id = botId };
            session.State = new GameState();
            GameEngine.ResetBall(session.State, 1);

            if (await GameStateService.StoreSessionAsync(playerId, session, log))
            {
                log.LogInformation($"Bot session created for {playerId}. Notifying player.");

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
                await SendSignalRMessageWithRetry(new SignalRMessage
                {
                    Target = "ErrorStartingGame",
                    Arguments = new object[] { "Failed to initialize bot match." },
                    ConnectionId = playerId
                }, signalRMessages, log);
            }
        }

        private static async Task SendSignalRMessageWithRetry(SignalRMessage message, IAsyncCollector<SignalRMessage> signalRMessages, ILogger log)
        {
            int attempts = 0;
            bool success = false;
            TimeSpan delay = TimeSpan.FromMilliseconds(100);

            while (!success && attempts < MAX_SEND_RETRIES)
            {
                try
                {
                    attempts++;
                    await signalRMessages.AddAsync(message);
                    success = true;
                }
                catch (Exception ex)
                {
                    log.LogWarning($"Attempt {attempts} failed to send message (Target: {message.Target}, ConnId: {message.ConnectionId ?? "N/A"}): {ex.Message}");
                    if (attempts < MAX_SEND_RETRIES)
                    {
                        await Task.Delay(delay);
                        delay *= 2;
                    }
                    else
                    {
                        log.LogError(ex, $"Failed to send message (Target: {message.Target}, ConnId: {message.ConnectionId ?? "N/A"}) after {MAX_SEND_RETRIES} attempts");
                    }
                }
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
                    if (session == null) log.LogDebug($"[UpdatePaddle] Session not found for player {playerId}. Ignoring update.");
                    else log.LogDebug($"[UpdatePaddle] Game already over for player {playerId}. Ignoring update.");
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
                            log.LogTrace($"[UpdatePaddle] P{side} ({playerId}) Y updated to {y:0}");
                        }

                        await GameStateService.UpdateSessionForBothPlayersAsync(session, log);
                    }
                    else
                    {
                        log.LogWarning($"[UpdatePaddle] Invalid paddle position data type from {playerId}: {arg.GetType().Name}, Value: '{arg}'");
                    }
                }
                else
                {
                    log.LogWarning($"[UpdatePaddle] Missing paddle position data from {playerId}");
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"[UpdatePaddle] Error processing paddle update from {playerId}");
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
        public static async Task<IActionResult> HealthCheck(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "healthcheck")] Microsoft.AspNetCore.Http.HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Health check requested");

            bool redisConnected = GameStateService.IsRedisConnected(out string redisError);
            long waitingPlayersCount = -1;
            long activeGamesCount = -1;

            if (redisConnected)
            {
                try
                {
                    waitingPlayersCount = await GameStateService.GetMatchmakingQueueSizeAsync(log);
                    activeGamesCount = await GameStateService.GetActiveGameCountAsync(log);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "HealthCheck failed to get metrics from Redis.");
                    redisConnected = false;
                    redisError = redisError ?? "Failed to get matchmaking metrics.";
                }
            }

            var healthData = new
            {
                status = redisConnected ? "Healthy" : "Degraded",
                timestamp = DateTime.UtcNow,
                dependencies = new
                {
                    redisConnected = redisConnected,
                    redisError = redisError ?? "N/A"
                },
                metrics = new
                {
                    waitingPlayers = waitingPlayersCount,
                    activeGames = activeGamesCount
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
            
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug($"Received keepalive from {connectionId}");
            }

            // Respond with a Pong message containing current timestamp
            await SendSignalRMessageWithRetry(new SignalRMessage
            {
                Target = "Pong",
                Arguments = new object[] { DateTime.UtcNow },
                ConnectionId = connectionId
            }, signalRMessages, log);
        }
    }
}