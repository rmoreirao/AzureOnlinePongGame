using Newtonsoft.Json;

namespace AzureOnlinePongGame.Models
{
    public class GameState
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
        public float BallVX { get; set; } = 6; // Consider making this GameEngine.BALL_SPEED
        [JsonProperty("ballVY")]
        public float BallVY { get; set; } = 6; // Consider making this GameEngine.BALL_SPEED
        [JsonProperty("player1Score")]
        public int Player1Score { get; set; } = 0;
        [JsonProperty("player2Score")]
        public int Player2Score { get; set; } = 0;
        [JsonProperty("gameOver")]
        public bool GameOver { get; set; } = false;
    }
}
