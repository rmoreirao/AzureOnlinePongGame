using System;
using Newtonsoft.Json;

namespace AzureOnlinePongGame.Models
{
    public class GameSession
    {
        public string Player1Id { get; set; } = string.Empty;
        public string Player2Id { get; set; } = string.Empty;
        public GameState State { get; set; } = new GameState();
        public DateTime LastUpdateTime { get; set; } = DateTime.UtcNow;
    }
}
