using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using AzureOnlinePongGame.Services;
using Microsoft.AspNetCore.SignalR;
using AzureOnlinePongGame.Models;

namespace AzureOnlinePongGame.Services
{
    public class GameLoopService : BackgroundService
    {
        private readonly GameStateService _gameStateService;
        private readonly IHubContext<PongHub> _hubContext;
        private readonly ILogger<GameLoopService> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(33); // ~30 FPS

        public GameLoopService(GameStateService gameStateService, IHubContext<PongHub> hubContext, ILogger<GameLoopService> logger)
        {
            _gameStateService = gameStateService;
            _hubContext = hubContext;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Game loop background service started.");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var sessions = await _gameStateService.GetAllActiveSessionsAsync();
                    foreach (var session in sessions)
                    {
                        if (session.State.GameOver)
                            continue;

                        // Bot game: update bot paddle
                        bool isBot = session.Player2Id != null && session.Player2Id.StartsWith("bot_");
                        if (isBot)
                        {
                            session.State = GameEngine.UpdateBotPaddle(session.State);
                        }

                        // Update game state
                        session.State = GameEngine.UpdateGameState(session.State);
                        await _gameStateService.UpdateSessionForBothPlayersAsync(session);

                        // Notify both players
                        if (!string.IsNullOrEmpty(session.Player1Id))
                            await _hubContext.Clients.Client(session.Player1Id).SendAsync("GameUpdate", session.State);
                        if (!isBot && !string.IsNullOrEmpty(session.Player2Id))
                            await _hubContext.Clients.Client(session.Player2Id).SendAsync("GameUpdate", session.State);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in game loop execution.");
                }
                await Task.Delay(_interval, stoppingToken);
            }
            _logger.LogInformation("Game loop background service stopped.");
        }
    }
}
