using Newtonsoft.Json;
using MessagePack;

namespace AzureOnlinePongGame.Models
{
    [MessagePackObject]
    public class GameState
    {
        [Key("ball")]
        [JsonProperty("ball")]
        public BallState Ball { get; set; } = new BallState();
        [Key("leftPaddle")]
        [JsonProperty("leftPaddle")]
        public PaddleState LeftPaddle { get; set; } = new PaddleState { X = 0, Y = 250 };
        [Key("rightPaddle")]
        [JsonProperty("rightPaddle")]
        public PaddleState RightPaddle { get; set; } = new PaddleState { X = 800 - 16, Y = 250 };
        [Key("leftScore")]
        [JsonProperty("leftScore")]
        public int LeftScore { get; set; } = 0;
        [Key("rightScore")]
        [JsonProperty("rightScore")]
        public int RightScore { get; set; } = 0;
        [Key("gameOver")]
        [JsonProperty("gameOver")]
        public bool GameOver { get; set; } = false;
        [Key("winner")]
        [JsonProperty("winner")]
        public int Winner { get; set; } = 0; // 0=no winner, 1=left, 2=right
    }

    [MessagePackObject]
    public class BallState
    {
        [Key("x")]
        [JsonProperty("x")]
        public float X { get; set; } = 400;
        [Key("y")]
        [JsonProperty("y")]
        public float Y { get; set; } = 300;
        [Key("velocityX")]
        [JsonProperty("velocityX")]
        public float VelocityX { get; set; } = 6;
        [Key("velocityY")]
        [JsonProperty("velocityY")]
        public float VelocityY { get; set; } = 6;
    }

    [MessagePackObject]
    public class PaddleState
    {
        [Key("x")]
        [JsonProperty("x")]
        public float X { get; set; }
        [Key("y")]
        [JsonProperty("y")]
        public float Y { get; set; }
    }
}
