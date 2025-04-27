using System;
using AzureOnlinePongGame.Models;

namespace AzureOnlinePongGame.Services
{
    public static class GameEngine
    {
        public const float PADDLE_HEIGHT = 100;
        public const float PADDLE_WIDTH = 16;
        public const float CANVAS_WIDTH = 800;
        public const float CANVAS_HEIGHT = 600;
        public const float BALL_SIZE = 16;
        public const float PADDLE_SPEED = 6;
        public const float BOT_PADDLE_SPEED_FACTOR = 0.85f;
        public const float BALL_SPEED = 6;
        public const int WIN_SCORE = 5;

        public static GameState UpdateGameState(GameState state)
        {
            // Logic to update the game state
            state.Ball.X += state.Ball.VelocityX;
            state.Ball.Y += state.Ball.VelocityY;

            // Ball collision with top and bottom walls
            if (state.Ball.Y <= 0 || state.Ball.Y >= CANVAS_HEIGHT - BALL_SIZE)
            {
                state.Ball.VelocityY = -state.Ball.VelocityY;
            }

            // Ball collision with paddles
            if (state.Ball.X <= state.LeftPaddle.X + PADDLE_WIDTH &&
                state.Ball.Y + BALL_SIZE >= state.LeftPaddle.Y &&
                state.Ball.Y <= state.LeftPaddle.Y + PADDLE_HEIGHT)
            {
                state.Ball.VelocityX = -state.Ball.VelocityX;
            }

            if (state.Ball.X + BALL_SIZE >= state.RightPaddle.X &&
                state.Ball.Y + BALL_SIZE >= state.RightPaddle.Y &&
                state.Ball.Y <= state.RightPaddle.Y + PADDLE_HEIGHT)
            {
                state.Ball.VelocityX = -state.Ball.VelocityX;
            }

            // Ball out of bounds
            if (state.Ball.X < 0)
            {
                state.RightScore++;
                ResetBall(state, 1);
            }
            else if (state.Ball.X > CANVAS_WIDTH)
            {
                state.LeftScore++;
                ResetBall(state, -1);
            }

            return state;
        }

        public static GameState UpdateBotPaddle(GameState state)
        {
            // Logic to update bot paddle position
            if (state.Ball.Y + BALL_SIZE / 2 > state.RightPaddle.Y + PADDLE_HEIGHT / 2)
            {
                state.RightPaddle.Y += PADDLE_SPEED * BOT_PADDLE_SPEED_FACTOR;
            }
            else if (state.Ball.Y + BALL_SIZE / 2 < state.RightPaddle.Y + PADDLE_HEIGHT / 2)
            {
                state.RightPaddle.Y -= PADDLE_SPEED * BOT_PADDLE_SPEED_FACTOR;
            }

            // Ensure paddle stays within canvas bounds
            state.RightPaddle.Y = Math.Max(0, Math.Min(CANVAS_HEIGHT - PADDLE_HEIGHT, state.RightPaddle.Y));

            return state;
        }

        public static void ResetBall(GameState state, int direction)
        {
            // Logic to reset ball position and velocity
            state.Ball.X = CANVAS_WIDTH / 2 - BALL_SIZE / 2;
            state.Ball.Y = CANVAS_HEIGHT / 2 - BALL_SIZE / 2;
            state.Ball.VelocityX = BALL_SPEED * direction;
            state.Ball.VelocityY = BALL_SPEED * (new Random().Next(0, 2) == 0 ? -1 : 1);
        }
    }
}