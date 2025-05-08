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
        
        // Added buffer for collision detection to be more forgiving
        private const float COLLISION_BUFFER = 4.0f; // Increased from 2.0f

        // Move paddle towards target
        private static float MoveTowards(float current, float target, float maxDelta)
        {
            if (Math.Abs(target - current) <= maxDelta) return target;
            return current + Math.Sign(target - current) * maxDelta;
        }

        // Accepts deltaTime and uses targetY for smooth paddle movement
        public static GameState UpdateGameState(GameState state, float deltaTime)
        {
            if (state.GameOver || !state.PlayersReady) return state;

            // Move paddles towards their targets
            state.LeftPaddle.Y = MoveTowards(state.LeftPaddle.Y, state.LeftPaddleTargetY, PADDLE_SPEED * deltaTime * 60);
            state.RightPaddle.Y = MoveTowards(state.RightPaddle.Y, state.RightPaddleTargetY, PADDLE_SPEED * deltaTime * 60);
            state.LeftPaddle.Y = Math.Max(0, Math.Min(CANVAS_HEIGHT - PADDLE_HEIGHT, state.LeftPaddle.Y));
            state.RightPaddle.Y = Math.Max(0, Math.Min(CANVAS_HEIGHT - PADDLE_HEIGHT, state.RightPaddle.Y));

            // Store previous ball position for continuous collision detection
            float prevBallX = state.Ball.X;
            float prevBallY = state.Ball.Y;

            // Ball movement
            state.Ball.X += state.Ball.VelocityX * deltaTime * 60;
            state.Ball.Y += state.Ball.VelocityY * deltaTime * 60;

            // Ball collision with top and bottom walls
            if (state.Ball.Y <= 0 || state.Ball.Y >= CANVAS_HEIGHT - BALL_SIZE)
            {
                state.Ball.VelocityY = -state.Ball.VelocityY;
                state.Ball.Y = Math.Max(0, Math.Min(CANVAS_HEIGHT - BALL_SIZE, state.Ball.Y));
            }

            // Improved collision detection for the left paddle
            bool hitLeftPaddle = CheckContinuousCollision(
                prevBallX, prevBallY, state.Ball.X, state.Ball.Y,
                state.LeftPaddle.X, state.LeftPaddle.Y,
                PADDLE_WIDTH, PADDLE_HEIGHT);

            if (hitLeftPaddle)
            {
                // Calculate angle based on where the ball hit the paddle
                float relativeIntersectY = (state.LeftPaddle.Y + (PADDLE_HEIGHT / 2)) - (state.Ball.Y + (BALL_SIZE / 2));
                float normalizedRelativeIntersectY = relativeIntersectY / (PADDLE_HEIGHT / 2);
                float bounceAngle = normalizedRelativeIntersectY * 0.8f; // Max 0.8 radians (about 45 degrees)
                
                // Calculate new velocities for more realistic physics
                float speed = (float)Math.Sqrt(state.Ball.VelocityX * state.Ball.VelocityX + state.Ball.VelocityY * state.Ball.VelocityY);
                state.Ball.VelocityX = Math.Abs(speed * (float)Math.Cos(bounceAngle));
                state.Ball.VelocityY = -speed * (float)Math.Sin(bounceAngle);
                
                // Prevent sticking by moving ball just outside paddle
                state.Ball.X = state.LeftPaddle.X + PADDLE_WIDTH + 0.1f;
            }

            // Improved collision detection for the right paddle
            bool hitRightPaddle = CheckContinuousCollision(
                prevBallX, prevBallY, state.Ball.X, state.Ball.Y,
                state.RightPaddle.X, // Corrected: Use the actual X position of the right paddle
                state.RightPaddle.Y,
                PADDLE_WIDTH, PADDLE_HEIGHT);

            if (hitRightPaddle)
            {
                // Calculate angle based on where the ball hit the paddle
                float relativeIntersectY = (state.RightPaddle.Y + (PADDLE_HEIGHT / 2)) - (state.Ball.Y + (BALL_SIZE / 2));
                float normalizedRelativeIntersectY = relativeIntersectY / (PADDLE_HEIGHT / 2);
                float bounceAngle = normalizedRelativeIntersectY * 0.8f; // Max 0.8 radians (about 45 degrees)
                
                // Calculate new velocities for more realistic physics
                float speed = (float)Math.Sqrt(state.Ball.VelocityX * state.Ball.VelocityX + state.Ball.VelocityY * state.Ball.VelocityY);
                state.Ball.VelocityX = -Math.Abs(speed * (float)Math.Cos(bounceAngle));
                state.Ball.VelocityY = -speed * (float)Math.Sin(bounceAngle);
                
                // Prevent sticking by moving ball just outside paddle
                state.Ball.X = state.RightPaddle.X - BALL_SIZE - 0.1f;
            }

            // Ball out of bounds
            if (state.Ball.X < 0)
            {
                state.RightScore++;
                ResetBall(state, 1);
                
                // Check for game over
                if (state.RightScore >= WIN_SCORE)
                {
                    state.GameOver = true;
                    state.Winner = 2; // Right player wins
                }
            }
            else if (state.Ball.X > CANVAS_WIDTH)
            {
                state.LeftScore++;
                ResetBall(state, -1);
                
                // Check for game over
                if (state.LeftScore >= WIN_SCORE)
                {
                    state.GameOver = true;
                    state.Winner = 1; // Left player wins
                }
            }

            state.SequenceNumber++;
            return state;
        }
        
        // Continuous collision detection to check if the ball crossed through the paddle
        private static bool CheckContinuousCollision(
            float prevBallX, float prevBallY, float currBallX, float currBallY,
            float paddleX, float paddleY, float paddleWidth, float paddleHeight)
        {
            // Add a small buffer to make collision detection more forgiving
            paddleY -= COLLISION_BUFFER;
            paddleHeight += COLLISION_BUFFER * 2;
            
            // Add horizontal buffer for edge cases
            paddleX -= COLLISION_BUFFER / 2;
            paddleWidth += COLLISION_BUFFER;
            
            // Vertical collision check
            if (currBallY + BALL_SIZE < paddleY || currBallY > paddleY + paddleHeight)
            {
                return false;
            }
            
            // Check if ball crossed the paddle on X-axis
            if ((prevBallX + BALL_SIZE <= paddleX && currBallX + BALL_SIZE > paddleX) ||
                (prevBallX >= paddleX + paddleWidth && currBallX < paddleX + paddleWidth))
            {
                return true;
            }
            
            // Check if ball is inside the paddle
            if (currBallX + BALL_SIZE >= paddleX && currBallX <= paddleX + paddleWidth)
            {
                return true;
            }
            
            return false;
        }

        // UpdateBotPaddle sets the targetY for the bot
        public static GameState UpdateBotPaddle(GameState state)
        {
            if (state.GameOver || !state.PlayersReady) return state;
            
            // Add slight prediction to make the bot more challenging
            float predictedY = state.Ball.Y;
            
            // If ball is moving toward the bot, predict where it will be
            if (state.Ball.VelocityX > 0)
            {
                // Simple prediction based on distance and velocity
                float distanceToBot = state.RightPaddle.X - state.Ball.X;
                float timeToReach = distanceToBot / Math.Abs(state.Ball.VelocityX);
                predictedY = state.Ball.Y + (state.Ball.VelocityY * timeToReach);
                
                // Keep prediction within bounds
                predictedY = Math.Max(0, Math.Min(CANVAS_HEIGHT - BALL_SIZE, predictedY));
            }
            
            // Target the center of the ball with the center of the paddle
            float targetY = predictedY - (PADDLE_HEIGHT / 2) + (BALL_SIZE / 2);
            targetY = Math.Max(0, Math.Min(CANVAS_HEIGHT - PADDLE_HEIGHT, targetY));
            
            // Adjust bot difficulty by limiting speed
            state.RightPaddleTargetY = MoveTowards(state.RightPaddle.Y, targetY, 
                PADDLE_SPEED * BOT_PADDLE_SPEED_FACTOR);
                
            return state;
        }

        public static void ResetBall(GameState state, int direction)
        {
            // Add slight randomness to ball velocity for variety
            Random random = new Random();
            float angle = (float)((random.NextDouble() * Math.PI / 4) - Math.PI / 8); // -22.5° to +22.5°
            
            // Set ball position to center
            state.Ball.X = CANVAS_WIDTH / 2 - BALL_SIZE / 2;
            state.Ball.Y = CANVAS_HEIGHT / 2 - BALL_SIZE / 2;
            
            // Set velocity based on angle and direction
            state.Ball.VelocityX = BALL_SPEED * direction * (float)Math.Cos(angle);
            state.Ball.VelocityY = BALL_SPEED * (float)Math.Sin(angle);
        }
    }
}