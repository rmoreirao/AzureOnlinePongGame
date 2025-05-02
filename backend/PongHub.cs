using Microsoft.AspNetCore.SignalR;
using AzureOnlinePongGame.Services;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace AzureOnlinePongGame
{
    public class PongHub : Hub
    {
        private readonly GameStateService _gameStateService;
        private readonly ILogger<PongHub> _logger;

        public PongHub(GameStateService gameStateService, ILogger<PongHub> logger)
        {
            _gameStateService = gameStateService;
            _logger = logger;
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
            // Ball always starts towards player
            // (You may want to call GameEngine.ResetBall here if you migrate that logic)
            await _gameStateService.StoreSessionAsync(playerId, session);
            _logger.LogInformation($"Bot session created for {playerId}. Notifying player.");
            await Clients.Caller.SendAsync("MatchFound", new { opponent = "Bot", side = 1, isBot = true });
        }

        public async Task UpdatePaddle(float paddleY)
        {
            var playerId = Context.ConnectionId;
            var session = await _gameStateService.GetSessionAsync(playerId);
            if (session == null || session.State.GameOver)
            {
                _logger.LogDebug(session == null ? $"[UpdatePaddle] Session not found for player {playerId}. Ignoring update." : $"[UpdatePaddle] Game already over for player {playerId}. Ignoring update.");
                return;
            }
            int side = session.Player1Id == playerId ? 1 : 2;
            paddleY = System.Math.Max(0, System.Math.Min(600 - 100, paddleY)); // Clamp to canvas
            if (side == 1) session.State.LeftPaddle.Y = paddleY;
            else session.State.RightPaddle.Y = paddleY;
            await _gameStateService.UpdateSessionForBothPlayersAsync(session);
        }

        public async Task KeepAlive()
        {
            _logger.LogDebug($"Received keepalive from {Context.ConnectionId}");
            await Clients.Caller.SendAsync("Pong", System.DateTime.UtcNow);
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var connectionId = Context.ConnectionId;
            _logger.LogInformation($"Client disconnected: {connectionId}");

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
