using Newtonsoft.Json;

namespace AzureOnlinePongGame.Models
{
    public class GameState
    {
        [JsonProperty("ball")]
        public BallState Ball { get; set; } = new BallState();
        [JsonProperty("leftPaddle")]
        public PaddleState LeftPaddle { get; set; } = new PaddleState { X = 0, Y = 250 };
        [JsonProperty("rightPaddle")]
        public PaddleState RightPaddle { get; set; } = new PaddleState { X = 800 - 16, Y = 250 };
        [JsonProperty("leftScore")]
        public int LeftScore { get; set; } = 0;
        [JsonProperty("rightScore")]
        public int RightScore { get; set; } = 0;
        [JsonProperty("gameOver")]
        public bool GameOver { get; set; } = false;
        [JsonProperty("winner")]
        public int Winner { get; set; } = 0; // 0=no winner, 1=left, 2=right
    }

    public class BallState
    {
        [JsonProperty("x")]
        public float X { get; set; } = 400;
        [JsonProperty("y")]
        public float Y { get; set; } = 300;
        [JsonProperty("velocityX")]
        public float VelocityX { get; set; } = 6;
        [JsonProperty("velocityY")]
        public float VelocityY { get; set; } = 6;
    }

    public class PaddleState
    {
        [JsonProperty("x")]
        public float X { get; set; }
        [JsonProperty("y")]
        public float Y { get; set; }
    }
}
