using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System.Text.Json;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Infrastructure.Services;

namespace TelecomCdr.Infrastructure.UnitTests.Services
{
    [TestFixture]
    public class RedisIdempotencyServiceTests
    {
        private Mock<IConnectionMultiplexer> _mockConnectionMultiplexer;
        private Mock<IDatabase> _mockRedisDatabase;
        private Mock<IConfiguration> _mockConfiguration;
        private Mock<ILogger<RedisIdempotencyService>> _mockLogger;
        private RedisIdempotencyService _redisIdempotencyService;

        [SetUp]
        public void SetUp()
        {
            _mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
            _mockRedisDatabase = new Mock<IDatabase>();
            _mockConfiguration = new Mock<IConfiguration>();
            _mockLogger = new Mock<ILogger<RedisIdempotencyService>>();

            // Setup IConnectionMultiplexer to return the mock IDatabase
            _mockConnectionMultiplexer.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                                      .Returns(_mockRedisDatabase.Object);

            // Setup IConfiguration for DefaultExpiryMinutes
            var mockConfigSection = new Mock<IConfigurationSection>();
            mockConfigSection.Setup(s => s.Value).Returns("30"); // Default 30 minutes for tests
            _mockConfiguration.Setup(c => c.GetSection("RedisSettings:DefaultExpiryMinutes"))
                              .Returns(mockConfigSection.Object);
            // Alternative way to mock IConfiguration directly for a specific key:
            _mockConfiguration.Setup(c => c["RedisSettings:DefaultExpiryMinutes"]).Returns("30");


            _redisIdempotencyService = new RedisIdempotencyService(
                _mockConnectionMultiplexer.Object,
                _mockConfiguration.Object,
                _mockLogger.Object
            );
        }

        #region GetCachedResponseAsync Tests

        [Test]
        public async Task GetCachedResponseAsync_NullOrWhitespaceKey_ReturnsNullAndLogsWarning()
        {
            // Act
            var result = await _redisIdempotencyService.GetCachedResponseAsync(" ");

            // Assert
            Assert.IsNull(result);
            _mockRedisDatabase.Verify(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never);
            VerifyLog(LogLevel.Warning, "Idempotency key was null or whitespace.");
        }

        [Test]
        public async Task GetCachedResponseAsync_KeyNotFoundInCache_ReturnsNull()
        {
            // Arrange
            var idempotencyKey = "test-key-not-found";
            var redisKey = $"idempotency:{idempotencyKey}";
            _mockRedisDatabase.Setup(db => db.StringGetAsync(redisKey, CommandFlags.None))
                              .ReturnsAsync(RedisValue.Null);

            // Act
            var result = await _redisIdempotencyService.GetCachedResponseAsync(idempotencyKey);

            // Assert
            Assert.IsNull(result);
            VerifyLog(LogLevel.Information, $"Idempotency key '{redisKey}' not found in cache.");
        }

        [Test]
        public async Task GetCachedResponseAsync_KeyFound_SuccessfulDeserialization_ReturnsEntry()
        {
            // Arrange
            var idempotencyKey = "test-key-found";
            var redisKey = $"idempotency:{idempotencyKey}";
            var cachedEntry = new CachedIdempotencyEntry
            {
                StatusCode = 200,
                BodyJson = "{\"message\":\"success\"}",
                ContentType = "application/json",
                RequestPayloadHash = "hash123"
            };
            var serializedEntry = JsonSerializer.Serialize(cachedEntry);
            _mockRedisDatabase.Setup(db => db.StringGetAsync(redisKey, CommandFlags.None))
                              .ReturnsAsync(new RedisValue(serializedEntry));

            // Act
            var result = await _redisIdempotencyService.GetCachedResponseAsync(idempotencyKey);

            // Assert
            Assert.IsNotNull(result);
            Assert.That(result.StatusCode, Is.EqualTo(cachedEntry.StatusCode));
            Assert.That(result.BodyJson, Is.EqualTo(cachedEntry.BodyJson));
            Assert.That(result.ContentType, Is.EqualTo(cachedEntry.ContentType));
            Assert.That(result.RequestPayloadHash, Is.EqualTo(cachedEntry.RequestPayloadHash));
            VerifyLog(LogLevel.Information, $"Idempotency key '{redisKey}' found in cache.");
        }

        [Test]
        public async Task GetCachedResponseAsync_KeyFound_DeserializationFails_ReturnsNullAndLogsError()
        {
            // Arrange
            var idempotencyKey = "test-key-deserialize-fail";
            var redisKey = $"idempotency:{idempotencyKey}";
            var invalidJson = "this is not valid json";
            _mockRedisDatabase.Setup(db => db.StringGetAsync(redisKey, CommandFlags.None))
                              .ReturnsAsync(new RedisValue(invalidJson));

            // Act
            var result = await _redisIdempotencyService.GetCachedResponseAsync(idempotencyKey);

            // Assert
            Assert.IsNull(result);
            VerifyLog(LogLevel.Error, $"Failed to deserialize cached response for idempotency key '{redisKey}'.");
        }

        [Test]
        public async Task GetCachedResponseAsync_RedisServerException_ReturnsNullAndLogsError()
        {
            // Arrange
            var idempotencyKey = "test-key-redis-server-error";
            var redisKey = $"idempotency:{idempotencyKey}";
            _mockRedisDatabase.Setup(db => db.StringGetAsync(redisKey, CommandFlags.None))
                              .ThrowsAsync(new RedisServerException("Server is on fire"));

            // Act
            var result = await _redisIdempotencyService.GetCachedResponseAsync(idempotencyKey);

            // Assert
            Assert.IsNull(result);
            VerifyLog(LogLevel.Error, $"Redis server error while retrieving idempotency key '{redisKey}'.");
        }

        [Test]
        public async Task GetCachedResponseAsync_RedisTimeoutException_ReturnsNullAndLogsError()
        {
            // Arrange
            var idempotencyKey = "test-key-redis-timeout";
            var redisKey = $"idempotency:{idempotencyKey}";
            _mockRedisDatabase.Setup(db => db.StringGetAsync(redisKey, CommandFlags.None))
                              .ThrowsAsync(new RedisTimeoutException("Connection timed out", CommandStatus.Unknown));

            // Act
            var result = await _redisIdempotencyService.GetCachedResponseAsync(idempotencyKey);

            // Assert
            Assert.IsNull(result);
            VerifyLog(LogLevel.Error, $"Redis timeout while retrieving idempotency key '{redisKey}'.");
        }

        [Test]
        public async Task GetCachedResponseAsync_GeneralException_ReturnsNullAndLogsError()
        {
            // Arrange
            var idempotencyKey = "test-key-general-exception";
            var redisKey = $"idempotency:{idempotencyKey}";
            _mockRedisDatabase.Setup(db => db.StringGetAsync(redisKey, CommandFlags.None))
                              .ThrowsAsync(new Exception("Something went wrong"));

            // Act
            var result = await _redisIdempotencyService.GetCachedResponseAsync(idempotencyKey);

            // Assert
            Assert.IsNull(result);
            VerifyLog(LogLevel.Error, $"Error retrieving idempotency key '{redisKey}' from Redis.");
        }


        #endregion

        #region CacheResponseAsync Tests

        [Test]
        public async Task CacheResponseAsync_NullOrWhitespaceKey_DoesNotCacheAndLogsWarning()
        {
            // Act
            await _redisIdempotencyService.CacheResponseAsync(" ", 200, null, "app/json", "hash");

            // Assert
            _mockRedisDatabase.Verify(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()), Times.Never);
            VerifyLog(LogLevel.Warning, "Cannot cache response for null or whitespace idempotency key.");
        }

        [Test]
        public async Task CacheResponseAsync_NullOrWhitespaceRequestHash_DoesNotCacheAndLogsWarning()
        {
            // Act
            await _redisIdempotencyService.CacheResponseAsync("key", 200, null, "app/json", " ");

            // Assert
            _mockRedisDatabase.Verify(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()), Times.Never);
            VerifyLog(LogLevel.Warning, "Request payload hash was null or whitespace for idempotency key key. Not caching.");
        }

        [Test]
        public async Task CacheResponseAsync_ValidInput_CachesResponseSuccessfully()
        {
            // Arrange
            var idempotencyKey = "cache-key-success";
            var redisKey = $"idempotency:{idempotencyKey}";
            var statusCode = 200;
            var responseBody = new { message = "cached data" };
            var responseContentType = "application/json";
            var requestPayloadHash = "payloadHash123";
            var defaultExpiry = TimeSpan.FromMinutes(30);

            var expectedEntry = new CachedIdempotencyEntry
            {
                StatusCode = statusCode,
                BodyJson = JsonSerializer.Serialize(responseBody),
                ContentType = responseContentType,
                RequestPayloadHash = requestPayloadHash
            };
            var expectedSerializedValue = JsonSerializer.Serialize(expectedEntry);

            _mockRedisDatabase.Setup(db => db.StringSetAsync(redisKey, It.IsAny<RedisValue>(), defaultExpiry, When.Always, CommandFlags.None))
                              .ReturnsAsync(true);

            // Act
            await _redisIdempotencyService.CacheResponseAsync(idempotencyKey, statusCode, responseBody, responseContentType, requestPayloadHash);

            // Assert
            _mockRedisDatabase.Verify(db => db.StringSetAsync(redisKey, (RedisValue)expectedSerializedValue, defaultExpiry, When.Always, CommandFlags.None), Times.Once);
            VerifyLog(LogLevel.Information, $"Successfully cached response for idempotency key '{redisKey}' with request hash '{requestPayloadHash}'.");
        }

        [Test]
        public async Task CacheResponseAsync_ValidInput_NullResponseBody_CachesSuccessfully()
        {
            // Arrange
            var idempotencyKey = "cache-key-null-body";
            var redisKey = $"idempotency:{idempotencyKey}";
            var statusCode = 204; // No Content
            string responseContentType = "application/json";
            var requestPayloadHash = "payloadHash456";
            var defaultExpiry = TimeSpan.FromMinutes(30);

            var expectedEntry = new CachedIdempotencyEntry
            {
                StatusCode = statusCode,
                BodyJson = null, // Explicitly null
                ContentType = responseContentType,
                RequestPayloadHash = requestPayloadHash
            };
            var expectedSerializedValue = JsonSerializer.Serialize(expectedEntry);

            _mockRedisDatabase.Setup(db => db.StringSetAsync(redisKey, It.IsAny<RedisValue>(), defaultExpiry, When.Always, CommandFlags.None))
                              .ReturnsAsync(true);

            // Act
            await _redisIdempotencyService.CacheResponseAsync(idempotencyKey, statusCode, null, responseContentType, requestPayloadHash);

            // Assert
            _mockRedisDatabase.Verify(db => db.StringSetAsync(redisKey, (RedisValue)expectedSerializedValue, defaultExpiry, When.Always, CommandFlags.None), Times.Once);
        }


        [Test]
        public async Task CacheResponseAsync_RedisStringSetFails_LogsWarning()
        {
            // Arrange
            var idempotencyKey = "cache-key-fail-set";
            var redisKey = $"idempotency:{idempotencyKey}";
            _mockRedisDatabase.Setup(db => db.StringSetAsync(redisKey, It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                              .ReturnsAsync(false); // Simulate failure

            // Act
            await _redisIdempotencyService.CacheResponseAsync(idempotencyKey, 200, "body", "text/plain", "hash");

            // Assert
            VerifyLog(LogLevel.Warning, $"Failed to cache response for idempotency key '{redisKey}'.");
        }

        [Test]
        public async Task CacheResponseAsync_GeneralExceptionDuringCaching_LogsError()
        {
            // Arrange
            var idempotencyKey = "cache-key-exception";
            var redisKey = $"idempotency:{idempotencyKey}";
            _mockRedisDatabase.Setup(db => db.StringSetAsync(redisKey, It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                              .ThrowsAsync(new Exception("Something went wrong during cache"));

            // Act
            await _redisIdempotencyService.CacheResponseAsync(idempotencyKey, 200, "body", "text/plain", "hash");

            // Assert
            VerifyLog(LogLevel.Error, $"Error caching response for idempotency key '{redisKey}' in Redis.");
        }

        #endregion

        // Helper method for verifying ILogger calls
        private void VerifyLog(LogLevel expectedLogLevel, string expectedMessageContent, Times? times = null)
        {
            times ??= Times.Once(); // Default to Times.Once() if not specified

            _mockLogger.Verify(
                x => x.Log(
                    expectedLogLevel,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(expectedMessageContent)),
                    It.IsAny<Exception>(), // Allow any exception, or null if no exception is expected for this log level
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                times.Value);
        }
    }
}
