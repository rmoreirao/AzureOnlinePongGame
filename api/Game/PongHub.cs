using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;

namespace AzureOnlinePongGame.Game
{
    public static class PongHub
    {
        // In-memory matchmaking and game state (replace with Redis for production)
        private static ConcurrentQueue<string> waitingPlayers = new ConcurrentQueue<string>();
        private static ConcurrentDictionary<string, Timer> gameTimers = new ConcurrentDictionary<string, Timer>();
        private const int GAME_LOOP_INTERVAL_MS = 33; // ~30 FPS
        private const float PADDLE_HEIGHT = 100;
        private const float PADDLE_WIDTH = 16;
        private const float CANVAS_WIDTH = 800;
        private const float CANVAS_HEIGHT = 600;
        private const float BALL_SIZE = 16;
        private const float PADDLE_SPEED = 6;
        private const float BALL_SPEED = 6;
        private const int WIN_SCORE = 5;

        // Redis connection
        private static readonly Lazy<ConnectionMultiplexer> redisConnection = new Lazy<ConnectionMultiplexer>(() =>
            ConnectionMultiplexer.Connect(Environment.GetEnvironmentVariable("RedisConnectionString") ?? "localhost")
        );
        private static IDatabase RedisDb => redisConnection.Value.GetDatabase();

        private static string GetSessionKey(string playerId) => $"pong:session:{playerId}";
        private static string GetSessionKeyBySessionId(string sessionId) => $"pong:sessionid:{sessionId}";

        private static async Task StoreSessionAsync(string playerId, GameSession session)
        {
            var json = JsonConvert.SerializeObject(session);
            await RedisDb.StringSetAsync(GetSessionKey(playerId), json);
            // Also store by sessionId if you want to support multi-player lookup
            await RedisDb.StringSetAsync(GetSessionKeyBySessionId(session.Player1Id + ":" + session.Player2Id), json);
        }
        private static async Task<GameSession?> GetSessionAsync(string playerId)
        {
            var json = await RedisDb.StringGetAsync(GetSessionKey(playerId));
            if (json.IsNullOrEmpty) return null;
            return JsonConvert.DeserializeObject<GameSession>(json!);
        }
        private static async Task UpdateSessionForBothPlayersAsync(GameSession session)
        {
            await StoreSessionAsync(session.Player1Id, session);
            await StoreSessionAsync(session.Player2Id, session);
        }

        private class GameSession
        {
            public string Player1Id { get; set; }
            public string Player2Id { get; set; }
            public GameState State { get; set; } = new GameState();
        }
        private class GameState
        {
            [JsonProperty("player1PaddleY")]
            public float Player1PaddleY { get; set; } = 250;
            [JsonProperty("player2PaddleY")]
            public float Player2PaddleY { get; set; } = 250;
            [JsonProperty("ballX")]
            public float BallX { get; set; } = 400;
            [JsonProperty("ballY")]
            public float BallY { get; set; } = 300;
            [JsonProperty("ballVX")]
            public float BallVX { get; set; } = 6;
            [JsonProperty("ballVY")]
            public float BallVY { get; set; } = 6;
            [JsonProperty("player1Score")]
            public int Player1Score { get; set; } = 0;
            [JsonProperty("player2Score")]
            public int Player2Score { get; set; } = 0;
            [JsonProperty("gameOver")]
            public bool GameOver { get; set; } = false;
        }

        [FunctionName("OnConnected")]
        public static Task OnConnected(
            [SignalRTrigger("pong", "connections", "connected")] InvocationContext invocationContext,
            ILogger log)
        {
            log.LogInformation($"Client connected: {invocationContext.ConnectionId}");
            return Task.CompletedTask;
        }

        [FunctionName("OnDisconnected")]
        public static Task OnDisconnected(
            [SignalRTrigger("pong", "connections", "disconnected")] InvocationContext invocationContext,
            ILogger log)
        {
            log.LogInformation($"Client disconnected: {invocationContext.ConnectionId}");
            return Task.CompletedTask;
        }

        [FunctionName("JoinMatchmaking")]
        public static async Task JoinMatchmaking(
            [SignalRTrigger("pong", "messages", "JoinMatchmaking")] InvocationContext invocationContext,
            [SignalR(HubName = "pong")] IAsyncCollector<SignalRMessage> signalRMessages,
            ILogger log)
        {
            string playerId = invocationContext.ConnectionId;
            log.LogInformation($"Player {playerId} requested matchmaking.");
            waitingPlayers.Enqueue(playerId);
            if (waitingPlayers.Count >= 2)
            {
                if (waitingPlayers.TryDequeue(out var p1) && waitingPlayers.TryDequeue(out var p2))
                {
                    var session = new GameSession { Player1Id = p1, Player2Id = p2 };
                    await UpdateSessionForBothPlayersAsync(session);
                    // Start server-side game loop
                    var timer = new Timer(async _ => await GameLoopRedis(session, signalRMessages), null, 0, GAME_LOOP_INTERVAL_MS);
                    gameTimers[p1] = timer;
                    gameTimers[p2] = timer;
                    // Notify both players
                    await signalRMessages.AddAsync(new SignalRMessage
                    {
                        Target = "MatchFound",
                        Arguments = new object[] { new { opponent = p2, side = 1 } },
                        ConnectionId = p1
                    });
                    await signalRMessages.AddAsync(new SignalRMessage
                    {
                        Target = "MatchFound",
                        Arguments = new object[] { new { opponent = p1, side = 2 } },
                        ConnectionId = p2
                    });
                }
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
            var session = new GameSession { Player1Id = playerId, Player2Id = botId };
            await UpdateSessionForBothPlayersAsync(session);
            // Start server-side game loop with bot
            var timer = new Timer(async _ => await GameLoopWithBot(session, signalRMessages), null, 0, GAME_LOOP_INTERVAL_MS);
            gameTimers[playerId] = timer;
            gameTimers[botId] = timer;
            // Notify player
            await signalRMessages.AddAsync(new SignalRMessage
            {
                Target = "MatchFound",
                Arguments = new object[] { new { opponent = "Bot", side = 1, isBot = true } },
                ConnectionId = playerId
            });
        }

        private static async Task GameLoopWithBot(GameSession session, IAsyncCollector<SignalRMessage> signalRMessages)
        {
            var session1 = await GetSessionAsync(session.Player1Id);
            if (session1 == null) return;
            var state = session1.State;
            if (state.GameOver) return;
            // Bot AI: simple follow the ball
            state.Player2PaddleY += Math.Sign(state.BallY + BALL_SIZE / 2 - (state.Player2PaddleY + PADDLE_HEIGHT / 2)) * PADDLE_SPEED * 0.85f;
            state.Player2PaddleY = Math.Max(0, Math.Min(CANVAS_HEIGHT - PADDLE_HEIGHT, state.Player2PaddleY));
            // Ball movement and collisions (same as normal loop)
            state.BallX += state.BallVX;
            state.BallY += state.BallVY;
            if (state.BallY <= 0 || state.BallY + BALL_SIZE >= CANVAS_HEIGHT)
                state.BallVY *= -1;
            if (state.BallX <= 32 && state.BallY + BALL_SIZE > state.Player1PaddleY && state.BallY < state.Player1PaddleY + PADDLE_HEIGHT)
            {
                state.BallVX *= -1;
                state.BallX = 32;
            }
            if (state.BallX + BALL_SIZE >= CANVAS_WIDTH - 32 && state.BallY + BALL_SIZE > state.Player2PaddleY && state.BallY < state.Player2PaddleY + PADDLE_HEIGHT)
            {
                state.BallVX *= -1;
                state.BallX = CANVAS_WIDTH - 32 - BALL_SIZE;
            }
            if (state.BallX < 0)
            {
                state.Player2Score++;
                if (state.Player2Score >= WIN_SCORE) state.GameOver = true;
                ResetBall(state, -1);
            }
            if (state.BallX > CANVAS_WIDTH)
            {
                state.Player1Score++;
                if (state.Player1Score >= WIN_SCORE) state.GameOver = true;
                ResetBall(state, 1);
            }
            session1.State = state;
            await UpdateSessionForBothPlayersAsync(session1);
            await signalRMessages.AddAsync(new SignalRMessage
            {
                Target = "GameUpdate",
                Arguments = new object[] { state },
                ConnectionId = session1.Player1Id
            });
        }

        private static async Task GameLoopRedis(GameSession session, IAsyncCollector<SignalRMessage> signalRMessages)
        {
            // Always get the latest session from Redis
            var session1 = await GetSessionAsync(session.Player1Id);
            if (session1 == null) return;
            var state = session1.State;
            if (state.GameOver) return;
            // Ball movement
            state.BallX += state.BallVX;
            state.BallY += state.BallVY;
            // Collisions with top/bottom
            if (state.BallY <= 0 || state.BallY + BALL_SIZE >= CANVAS_HEIGHT)
                state.BallVY *= -1;
            // Collisions with paddles
            if (state.BallX <= 32 && state.BallY + BALL_SIZE > state.Player1PaddleY && state.BallY < state.Player1PaddleY + PADDLE_HEIGHT)
            {
                state.BallVX *= -1;
                state.BallX = 32;
            }
            if (state.BallX + BALL_SIZE >= CANVAS_WIDTH - 32 && state.BallY + BALL_SIZE > state.Player2PaddleY && state.BallY < state.Player2PaddleY + PADDLE_HEIGHT)
            {
                state.BallVX *= -1;
                state.BallX = CANVAS_WIDTH - 32 - BALL_SIZE;
            }
            // Score
            if (state.BallX < 0)
            {
                state.Player2Score++;
                if (state.Player2Score >= WIN_SCORE) state.GameOver = true;
                ResetBall(state, -1);
            }
            if (state.BallX > CANVAS_WIDTH)
            {
                state.Player1Score++;
                if (state.Player1Score >= WIN_SCORE) state.GameOver = true;
                ResetBall(state, 1);
            }
            // Save updated state to Redis
            session1.State = state;
            await UpdateSessionForBothPlayersAsync(session1);
            // Broadcast state
            await signalRMessages.AddAsync(new SignalRMessage
            {
                Target = "GameUpdate",
                Arguments = new object[] { state },
                ConnectionId = session1.Player1Id
            });
            await signalRMessages.AddAsync(new SignalRMessage
            {
                Target = "GameUpdate",
                Arguments = new object[] { state },
                ConnectionId = session1.Player2Id
            });
        }

        private static void ResetBall(GameState state, int direction)
        {
            state.BallX = CANVAS_WIDTH / 2 - BALL_SIZE / 2;
            state.BallY = CANVAS_HEIGHT / 2 - BALL_SIZE / 2;
            state.BallVX = BALL_SPEED * direction;
            state.BallVY = BALL_SPEED * (new Random().Next(0, 2) == 0 ? 1 : -1);
        }

        [FunctionName("UpdatePaddle")]
        public static async Task UpdatePaddle(
            [SignalRTrigger("pong", "messages", "UpdatePaddle")] InvocationContext invocationContext,
            [SignalR(HubName = "pong")] IAsyncCollector<SignalRMessage> signalRMessages,
            ILogger log)
        {
            string playerId = invocationContext.ConnectionId;
            var session = await GetSessionAsync(playerId);
            if (session == null) return;
            int side = session.Player1Id == playerId ? 1 : 2;
            if (invocationContext.Arguments.Length > 0 && invocationContext.Arguments[0] != null)
            {
                var arg = invocationContext.Arguments[0];
                float y = 0;
                bool valid = false;
                if (arg is long l)
                {
                    y = l;
                    valid = true;
                }
                else if (arg is double d)
                {
                    y = (float)d;
                    valid = true;
                }
                else if (arg is float f)
                {
                    y = f;
                    valid = true;
                }
                else if (arg is Newtonsoft.Json.Linq.JObject jobj && jobj["y"] != null && jobj["y"].Type != Newtonsoft.Json.Linq.JTokenType.Null)
                {
                    y = jobj["y"].ToObject<float>();
                    valid = true;
                }
                else if (arg != null)
                {
                    try
                    {
                        y = Convert.ToSingle(arg);
                        valid = true;
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, $"Unexpected error converting paddle position from player {playerId}. Argument type: {arg.GetType().FullName}, Value: {arg}");
                        valid = false;
                    }
                }
                log.LogInformation($"Received paddle update from {playerId}: {y} (valid: {valid})");
                if (valid)
                {
                    if (side == 1) session.State.Player1PaddleY = y;
                    else session.State.Player2PaddleY = y;
                    log.LogInformation($"Updated game state: player1PaddleY={session.State.Player1PaddleY}, player2PaddleY={session.State.Player2PaddleY}");
                    await UpdateSessionForBothPlayersAsync(session);
                }
            }
        }
    }
}