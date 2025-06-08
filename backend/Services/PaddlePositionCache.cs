using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using AzureOnlinePongGame.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AzureOnlinePongGame.Services
{
    public class PaddlePositionCache
    {
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<PaddlePositionCache> _logger;
        private readonly TimeSpan _paddleCacheExpiry = TimeSpan.FromMinutes(5); // Cache expiry for paddle positions
        
        // Prefix for cache keys
        private const string PLAYER_PADDLE_KEY_PREFIX = "paddle:";
        
        public PaddlePositionCache(IMemoryCache memoryCache, ILogger<PaddlePositionCache> logger)
        {
            _memoryCache = memoryCache;
            _logger = logger;
        }
        
        private string GetPaddleKey(string playerId)
        {
            return $"{PLAYER_PADDLE_KEY_PREFIX}{playerId}";
        }
        
        public void StorePaddlePosition(string playerId, float targetY)
        {
            try
            {
                string paddleKey = GetPaddleKey(playerId);
                // Store with a sliding expiration to automatically clean up stale entries
                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetSlidingExpiration(_paddleCacheExpiry);
                
                _memoryCache.Set(paddleKey, targetY, cacheOptions);
                _logger.LogDebug($"Stored paddle position for player {playerId}: {targetY}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error storing paddle position for player {playerId}.");
            }
        }
        
        public (float? Player1Input, float? Player2Input) GetPlayerInputs(string? player1Id, string? player2Id)
        {
            float? p1Input = null;
            float? p2Input = null;
            
            try
            {
                if (!string.IsNullOrEmpty(player1Id))
                {
                    var paddleKey1 = GetPaddleKey(player1Id);
                    if (_memoryCache.TryGetValue(paddleKey1, out float p1TargetY))
                    {
                        p1Input = p1TargetY;
                    }
                }
                
                if (!string.IsNullOrEmpty(player2Id) && !player2Id.StartsWith("bot_"))
                {
                    var paddleKey2 = GetPaddleKey(player2Id);
                    if (_memoryCache.TryGetValue(paddleKey2, out float p2TargetY))
                    {
                        p2Input = p2TargetY;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving paddle positions for players {player1Id} and {player2Id}.");
            }
            
            return (p1Input, p2Input);
        }
        
        public void RemovePaddlePosition(string playerId)
        {
            try
            {
                string paddleKey = GetPaddleKey(playerId);
                _memoryCache.Remove(paddleKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error removing paddle position for player {playerId}.");
            }
        }
    }
}
