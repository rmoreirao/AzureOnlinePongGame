using System;
using AzureOnlinePongGame.Models;

namespace AzureOnlinePongGame.Game
{
    public static class GameEngine
    {
        // Game Constants
        public const float PADDLE_HEIGHT = 100;
        public const float PADDLE_WIDTH = 16;
        public const float CANVAS_WIDTH = 800;
        public const float CANVAS_HEIGHT = 600;
        public const float BALL_SIZE = 16;
        public const float PADDLE_SPEED = 6; // Speed for player-controlled paddles
        public const float BOT_PADDLE_SPEED_FACTOR = 0.85f; // How fast the bot paddle moves relative to player speed
        public const float BALL_SPEED = 6;
        public const int WIN_SCORE = 5;

        // Updates the game state for one frame/tick (multiplayer)
        public static GameState UpdateGameState(GameState state)
        {
            if (state.GameOver) return state;

            // Ball movement
            state.BallX += state.BallVX;
            state.BallY += state.BallVY;

            // Collisions with top/bottom
            if (state.BallY <= 0)
            {
                state.BallY = 0; // Clamp position
                state.BallVY *= -1;
            }
            else if (state.BallY + BALL_SIZE >= CANVAS_HEIGHT)
            {
                state.BallY = CANVAS_HEIGHT - BALL_SIZE; // Clamp position
                state.BallVY *= -1;
            }

            // Collisions with paddles
            // Player 1 (Left)
            if (state.BallVX < 0 && // Moving left
                state.BallX <= PADDLE_WIDTH && // Within paddle horizontal range (simplified)
                state.BallX > 0 && // Avoid getting stuck behind paddle
                state.BallY + BALL_SIZE > state.Player1PaddleY &&
                state.BallY < state.Player1PaddleY + PADDLE_HEIGHT)
            {
                state.BallVX *= -1;
                state.BallX = PADDLE_WIDTH; // Move ball slightly outside paddle
                // Optional: Add slight vertical angle change based on where it hits the paddle
                // float deltaY = state.BallY - (state.Player1PaddleY + PADDLE_HEIGHT / 2);
                // state.BallVY = deltaY * 0.1f; // Adjust multiplier as needed
            }

            // Player 2 (Right)
            if (state.BallVX > 0 && // Moving right
                state.BallX + BALL_SIZE >= CANVAS_WIDTH - PADDLE_WIDTH && // Within paddle horizontal range
                state.BallX + BALL_SIZE < CANVAS_WIDTH && // Avoid getting stuck behind paddle
                state.BallY + BALL_SIZE > state.Player2PaddleY &&
                state.BallY < state.Player2PaddleY + PADDLE_HEIGHT)
            {
                state.BallVX *= -1;
                state.BallX = CANVAS_WIDTH - PADDLE_WIDTH - BALL_SIZE; // Move ball slightly outside paddle
                // Optional: Add slight vertical angle change
                // float deltaY = state.BallY - (state.Player2PaddleY + PADDLE_HEIGHT / 2);
                // state.BallVY = deltaY * 0.1f;
            }

            // Score
            if (state.BallX < 0) // Player 2 scores
            {
                state.Player2Score++;
                if (state.Player2Score >= WIN_SCORE) state.GameOver = true;
                ResetBall(state, -1); // Ball goes towards player 1
            }
            if (state.BallX + BALL_SIZE > CANVAS_WIDTH) // Player 1 scores
            {
                state.Player1Score++;
                if (state.Player1Score >= WIN_SCORE) state.GameOver = true;
                ResetBall(state, 1); // Ball goes towards player 2
            }

            return state;
        }

        // Updates the bot's paddle position
        public static GameState UpdateBotPaddle(GameState state)
        {
            if (state.GameOver) return state;

            // Simple AI: Move paddle towards the ball's Y position
            float targetY = state.BallY + BALL_SIZE / 2; // Center of the ball
            float currentCenterY = state.Player2PaddleY + PADDLE_HEIGHT / 2;
            float difference = targetY - currentCenterY;

            // Move paddle if the difference is significant enough (prevents jitter)
            if (Math.Abs(difference) > PADDLE_SPEED * BOT_PADDLE_SPEED_FACTOR)
            {
                 state.Player2PaddleY += Math.Sign(difference) * PADDLE_SPEED * BOT_PADDLE_SPEED_FACTOR;
            }

            // Clamp paddle position within bounds
            state.Player2PaddleY = Math.Max(0, Math.Min(CANVAS_HEIGHT - PADDLE_HEIGHT, state.Player2PaddleY));

            return state;
        }


        // Resets the ball to the center after a score
        public static void ResetBall(GameState state, int direction)
        {
            state.BallX = CANVAS_WIDTH / 2 - BALL_SIZE / 2;
            state.BallY = CANVAS_HEIGHT / 2 - BALL_SIZE / 2;
            state.BallVX = BALL_SPEED * direction;
            // Add some randomness to the vertical velocity
            state.BallVY = BALL_SPEED * (new Random().NextDouble() > 0.5 ? 1 : -1) * (float)(0.5 + new Random().NextDouble() * 0.5); // Random factor between 0.5 and 1.0
        }
    }
}
