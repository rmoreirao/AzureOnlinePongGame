using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using AzureOnlinePongGame.Models;
using StackExchange.Redis;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Linq;
using System.Diagnostics;

namespace AzureOnlinePongGame.Game
{
    /// <summary>
    /// Handles the game loop for all active games in a distributed environment
    /// This timer-triggered function provides a higher frequency game loop implementation
    /// </summary>
    public static class GameLoop
    {
        private const string LOCK_PREFIX = "pong:lock:";
        private const string ACTIVE_GAMES_SET = "pong:active_games";
        private const int LOCK_EXPIRY_MS = 2000; // Reduced from 5s to 2s for higher frequency
        private const int MAX_SEND_RETRIES = 3;
        
        // Game loop configuration - can be moved to environment variables
        private static readonly int GAME_LOOP_FREQUENCY_MS = 
            int.TryParse(Environment.GetEnvironmentVariable("GAME_LOOP_FREQUENCY_MS"), out int freq) ? freq : 33; // ~30 FPS default
        
        private static readonly int MAX_GAMES_PER_BATCH = 
            int.TryParse(Environment.GetEnvironmentVariable("MAX_GAMES_PER_BATCH"), out int max) ? max : 20;

        // Use performance counter to track execution time
        private static readonly Stopwatch stopwatch = new Stopwatch();

        /// <summary>
        /// Timer triggered function that processes all active game sessions
        /// Runs at approximately 30fps (33ms intervals) for smoother gameplay
        /// </summary>
        [FunctionName("ProcessActiveGames")]
        public static async Task ProcessActiveGames(
            // Using a TimeSpan string in the correct format for ~30fps
            [TimerTrigger("00:00:00.033")] TimerInfo timer, 
            [SignalR(HubName = "pong")] IAsyncCollector<SignalRMessage> signalRMessages,
            ILogger log)
        {
            stopwatch.Restart();
            
            try
            {
                IDatabase redisDb = null;
                try
                {
                    // Get the Redis database connection
                    var redis = ConnectionMultiplexer.Connect(
                        Environment.GetEnvironmentVariable("RedisConnectionString") ?? "localhost");
                    redisDb = redis.GetDatabase();
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Failed to connect to Redis in ProcessActiveGames");
                    return;
                }

                // Get all active game session IDs from Redis
                var activeGameSessionIds = await redisDb.SetMembersAsync(ACTIVE_GAMES_SET);
                
                if (activeGameSessionIds.Length == 0)
                {
                    log.LogDebug("No active games found to process");
                    return;
                }

                // Only log when there are actually games to process to avoid spam
                if (activeGameSessionIds.Length > 0 && log.IsEnabled(LogLevel.Information))
                {
                    log.LogInformation($"Processing {activeGameSessionIds.Length} active game sessions");
                }

                // Track processed games for metrics
                int processedGames = 0;
                int botGamesProcessed = 0;
                int playerGamesProcessed = 0;
                int errorsEncountered = 0;

                // Limit the number of games processed per invocation to avoid overruns
                int gamesToProcess = Math.Min(activeGameSessionIds.Length, MAX_GAMES_PER_BATCH);
                
                // Process each game session
                for (int i = 0; i < gamesToProcess; i++)
                {
                    var sessionIdRedisValue = activeGameSessionIds[i];
                    string sessionId = sessionIdRedisValue.ToString();
                    
                    // Try to acquire a distributed lock for this session to prevent multiple instances from processing the same game
                    string lockKey = $"{LOCK_PREFIX}{sessionId}";
                    bool gotLock = await redisDb.StringSetAsync(lockKey, Environment.MachineName, 
                        TimeSpan.FromMilliseconds(LOCK_EXPIRY_MS), When.NotExists);
                    
                    if (!gotLock)
                    {
                        // Another instance is already processing this game
                        continue;
                    }

                    try
                    {
                        // Get the game session
                        string sessionKey = $"pong:sessionid:{sessionId}";
                        var sessionJson = await redisDb.StringGetAsync(sessionKey);
                        
                        if (sessionJson.IsNullOrEmpty)
                        {
                            // Session no longer exists but is still in active set, remove it
                            await redisDb.SetRemoveAsync(ACTIVE_GAMES_SET, sessionId);
                            continue;
                        }

                        var session = JsonConvert.DeserializeObject<GameSession>(sessionJson);
                        
                        // Skip already completed games
                        if (session.State.GameOver)
                        {
                            await redisDb.SetRemoveAsync(ACTIVE_GAMES_SET, sessionId);
                            continue;
                        }

                        // Record time before update for metrics
                        session.LastUpdateTime = DateTime.UtcNow;
                        
                        // Check if this is a bot game
                        bool isBot = session.Player2Id?.StartsWith("bot_") ?? false;
                        
                        if (isBot)
                        {
                            // For bot games, update the bot paddle position
                            session.State = GameEngine.UpdateBotPaddle(session.State);
                            botGamesProcessed++;
                        }
                        else
                        {
                            playerGamesProcessed++;
                        }
                        
                        // Update game state for all games
                        session.State = GameEngine.UpdateGameState(session.State);
                        
                        // Save updated state back to Redis
                        await GameStateService.UpdateSessionForBothPlayersAsync(session, log);
                        
                        // Send updates to connected clients
                        await SendGameUpdates(session, signalRMessages, log);
                        
                        // Increment processed counter
                        processedGames++;
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, $"Error processing game session {sessionId}");
                        errorsEncountered++;
                    }
                    finally
                    {
                        // Always release the lock when done
                        await redisDb.KeyDeleteAsync(lockKey);
                    }
                }

                stopwatch.Stop();
                
                // Only log summary when there were games processed or errors
                if (processedGames > 0 || errorsEncountered > 0)
                {
                    log.LogInformation($"Game loop completed in {stopwatch.ElapsedMilliseconds}ms: " +
                        $"Processed {processedGames} of {activeGameSessionIds.Length} games " +
                        $"({playerGamesProcessed} player vs player, {botGamesProcessed} bot games). " +
                        $"Errors: {errorsEncountered}");
                    
                    // Warning if we're taking too long compared to our target frequency
                    if (stopwatch.ElapsedMilliseconds > GAME_LOOP_FREQUENCY_MS * 0.8)
                    {
                        log.LogWarning($"Game loop execution time ({stopwatch.ElapsedMilliseconds}ms) " +
                            $"is approaching trigger frequency ({GAME_LOOP_FREQUENCY_MS}ms). " +
                            $"Consider optimizing or scaling out.");
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error in ProcessActiveGames function");
            }
        }

        /// <summary>
        /// Sends game state updates to all players in a session
        /// </summary>
        private static async Task SendGameUpdates(GameSession session, 
            IAsyncCollector<SignalRMessage> signalRMessages, ILogger log)
        {
            // Only send updates to human players
            List<string> targets = new List<string>();
            if (!string.IsNullOrEmpty(session.Player1Id) && !session.Player1Id.StartsWith("bot_"))
                targets.Add(session.Player1Id);
            
            if (!string.IsNullOrEmpty(session.Player2Id) && !session.Player2Id.StartsWith("bot_"))
                targets.Add(session.Player2Id);

            if (targets.Count == 0) return; // No human players to update

            foreach (var target in targets)
            {
                await SendSignalRMessageWithRetry(new SignalRMessage
                {
                    Target = session.State.GameOver ? "GameOver" : "GameUpdate",
                    Arguments = new object[] { session.State },
                    ConnectionId = target
                }, signalRMessages, log);
            }
        }

        /// <summary>
        /// Send SignalR messages with retry logic for reliability
        /// </summary>
        private static async Task SendSignalRMessageWithRetry(SignalRMessage message, 
            IAsyncCollector<SignalRMessage> signalRMessages, ILogger log)
        {
            int attempts = 0;
            bool success = false;
            TimeSpan delay = TimeSpan.FromMilliseconds(100);

            while (!success && attempts < MAX_SEND_RETRIES)
            {
                try
                {
                    attempts++;
                    await signalRMessages.AddAsync(message);
                    success = true;
                }
                catch (Exception ex)
                {
                    log.LogWarning($"Attempt {attempts} failed to send message (Target: {message.Target}, ConnId: {message.ConnectionId ?? "N/A"}): {ex.Message}");
                    if (attempts < MAX_SEND_RETRIES)
                    {
                        await Task.Delay(delay);
                        delay *= 2; // Exponential backoff
                    }
                    else
                    {
                        log.LogError(ex, $"Failed to send message (Target: {message.Target}, ConnId: {message.ConnectionId ?? "N/A"}) after {MAX_SEND_RETRIES} attempts");
                    }
                }
            }
        }
    }
}