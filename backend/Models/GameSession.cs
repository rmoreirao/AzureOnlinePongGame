using System;
using Newtonsoft.Json;

namespace AzureOnlinePongGame.Models
{
    public class GameSession
    {
        [JsonProperty("player1Id")]
        public string Player1Id { get; set; } = string.Empty;
        [JsonProperty("player2Id")]
        public string Player2Id { get; set; } = string.Empty;
        [JsonProperty("state")]
        public GameState State { get; set; } = new GameState();
        [JsonProperty("lastUpdateTime")]
        public DateTime LastUpdateTime { get; set; } = DateTime.UtcNow;
    }
}
