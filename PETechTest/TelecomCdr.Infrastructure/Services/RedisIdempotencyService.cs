using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;
using TelecomCdr.Abstraction.Interfaces.Service;

namespace TelecomCdr.Infrastructure.Services
{
    /// <summary>
    /// Implements IIdempotencyService using Redis as the backing store.
    /// Stores a hash of the request payload for validation.
    /// </summary>
    public class RedisIdempotencyService : IIdempotencyService
    {
        private readonly IDatabase _redisDatabase;
        private readonly ILogger<RedisIdempotencyService> _logger;
        private readonly TimeSpan _defaultExpiry;

        public RedisIdempotencyService(
            IConnectionMultiplexer connectionMultiplexer,
            IConfiguration configuration,
            ILogger<RedisIdempotencyService> logger)
        {
            if (connectionMultiplexer == null)
            {
                throw new ArgumentNullException(nameof(connectionMultiplexer));
            }

            _redisDatabase = connectionMultiplexer.GetDatabase();
            _logger = logger;

            if (!int.TryParse(configuration["RedisSettings:DefaultExpiryMinutes"], out int expiryMinutes))
            {
                expiryMinutes = 1440; // Default to 24 hours
            }
            _defaultExpiry = TimeSpan.FromMinutes(expiryMinutes);
        }

        public async Task<CachedHttpResponse?> GetCachedResponseAsync(string idempotencyKey)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                _logger.LogWarning("Idempotency key was null or whitespace.");
                return null;
            }

            string redisKey = $"idempotency:{idempotencyKey}";
            try
            {
                var storedValue = await _redisDatabase.StringGetAsync(redisKey);
                if (storedValue.HasValue)
                {
                    _logger.LogInformation("Idempotency key '{RedisKey}' found in cache.", redisKey);
                    return JsonSerializer.Deserialize<CachedHttpResponse>(storedValue.ToString());
                }
                _logger.LogInformation("Idempotency key '{RedisKey}' not found in cache.", redisKey);
                return null;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Failed to deserialize cached response for idempotency key '{RedisKey}'.", redisKey);
                return null;
            }
            catch (RedisServerException ex)
            {
                _logger.LogError(ex, "Redis server error while retrieving idempotency key '{RedisKey}'.", redisKey);
                return null;
            }
            catch (RedisTimeoutException ex)
            {
                _logger.LogError(ex, "Redis timeout while retrieving idempotency key '{RedisKey}'.", redisKey);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving idempotency key '{RedisKey}' from Redis.", redisKey);
                return null;
            }
        }

        public async Task CacheResponseAsync(string idempotencyKey, int statusCode, object? responseBody, string responseContentType, string requestPayloadHash)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                _logger.LogWarning("Cannot cache response for null or whitespace idempotency key.");
                return;
            }

            if (string.IsNullOrWhiteSpace(requestPayloadHash))
            {
                _logger.LogWarning("Request payload hash was null or whitespace for idempotency key {IdempotencyKey}. Not caching.", idempotencyKey);
                // Depending on policy, we might still cache but without payload hash validation, or reject.
                return;
            }

            string redisKey = $"idempotency:{idempotencyKey}";
            try
            {
                var responseToCache = new CachedHttpResponse
                {
                    StatusCode = statusCode,
                    BodyJson = responseBody != null ? JsonSerializer.Serialize(responseBody) : null,
                    ContentType = responseContentType,
                    RequestPayloadHash = requestPayloadHash
                };

                string serializedResponse = JsonSerializer.Serialize(responseToCache);
                bool success = await _redisDatabase.StringSetAsync(redisKey, serializedResponse, _defaultExpiry, When.Always, CommandFlags.None);

                if (success)
                {
                    _logger.LogInformation("Successfully cached response for idempotency key '{RedisKey}' with request hash '{RequestHash}'.", redisKey, requestPayloadHash);
                }
                else
                {
                    _logger.LogWarning("Failed to cache response for idempotency key '{RedisKey}'.", redisKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error caching response for idempotency key '{RedisKey}' in Redis.", redisKey);
            }
        }
    }
}
