using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System.Text.Json;
using TelecomCdr.Abstraction.Interfaces.Service;
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
        private RedisIdempotencyService _service;

        // --- Helper: Configuration Setup ---
        private static IConfiguration SetupMockConfiguration(string? expiryMinutes = "1440")
        {
            var configData = new Dictionary<string, string?>
            {
                { "RedisSettings:IdempotencyKeyExpiryMinutes", expiryMinutes }
            };
            return new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();
        }

        [SetUp]
        public void Setup()
        {
            _mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
            _mockRedisDatabase = new Mock<IDatabase>();
            _mockConfiguration = new Mock<IConfiguration>(); // Will be replaced by SetupMockConfiguration in tests
            _mockLogger = new Mock<ILogger<RedisIdempotencyService>>();

            // Setup the connection multiplexer to return the mocked database
            _mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                                      .Returns(_mockRedisDatabase.Object);
        }

        private void InitializeService(IConfiguration configuration)
        {
            _service = new RedisIdempotencyService(
                _mockConnectionMultiplexer.Object,
                configuration, // Use the specifically configured mock for each test scenario
                _mockLogger.Object
            );
        }

        // --- Tests for GetCachedResponseAsync ---

        [Test]
        public async Task GetCachedResponseAsync_NullOrWhitespaceKey_ReturnsNullAndLogsWarning()
        {
            // Arrange
            InitializeService(SetupMockConfiguration()); // Use default config

            // Act
            var resultWithNull = await _service.GetCachedResponseAsync(null!);
            var resultWithEmpty = await _service.GetCachedResponseAsync("");
            var resultWithWhitespace = await _service.GetCachedResponseAsync("   ");

            // Assert
            Assert.IsNull(resultWithNull);
            Assert.IsNull(resultWithEmpty);
            Assert.IsNull(resultWithWhitespace);

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Idempotency key was null or whitespace.")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Exactly(3)); // Called for null, empty, and whitespace
        }

        [Test]
        public async Task GetCachedResponseAsync_KeyFound_ReturnsDeserializedResponse()
        {
            // Arrange
            var config = SetupMockConfiguration();
            InitializeService(config);
            var idempotencyKey = "test-key-found";
            var expectedResponse = new CachedHttpResponse
            {
                StatusCode = 200,
                BodyJson = "{\"message\":\"success\"}",
                ContentType = "application/json",
                RequestPayloadHash = "hash123"
            };
            var serializedResponse = JsonSerializer.Serialize(expectedResponse);
            _mockRedisDatabase.Setup(db => db.StringGetAsync($"idempotency:{idempotencyKey}", It.IsAny<CommandFlags>()))
                              .ReturnsAsync(new RedisValue(serializedResponse));

            // Act
            var result = await _service.GetCachedResponseAsync(idempotencyKey);

            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(expectedResponse.StatusCode, result!.StatusCode);
            Assert.AreEqual(expectedResponse.BodyJson, result.BodyJson);
            Assert.AreEqual(expectedResponse.ContentType, result.ContentType);
            Assert.AreEqual(expectedResponse.RequestPayloadHash, result.RequestPayloadHash);
        }

        [Test]
        public async Task GetCachedResponseAsync_KeyFoundButDeserializationFails_ReturnsNullAndLogsError()
        {
            // Arrange
            var config = SetupMockConfiguration();
            InitializeService(config);
            var idempotencyKey = "test-key-bad-json";
            var malformedJson = "this is not valid json";
            _mockRedisDatabase.Setup(db => db.StringGetAsync($"idempotency:{idempotencyKey}", It.IsAny<CommandFlags>()))
                              .ReturnsAsync(new RedisValue(malformedJson));

            // Act
            var result = await _service.GetCachedResponseAsync(idempotencyKey);

            // Assert
            Assert.IsNull(result);
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to deserialize cached response")),
                    It.IsAny<JsonException>(), // Expect a JsonException
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Test]
        public async Task GetCachedResponseAsync_KeyNotFound_ReturnsNull()
        {
            // Arrange
            var config = SetupMockConfiguration();
            InitializeService(config);
            var idempotencyKey = "test-key-not-found";
            _mockRedisDatabase.Setup(db => db.StringGetAsync($"idempotency:{idempotencyKey}", It.IsAny<CommandFlags>()))
                              .ReturnsAsync(RedisValue.Null); // Simulate key not found

            // Act
            var result = await _service.GetCachedResponseAsync(idempotencyKey);

            // Assert
            Assert.IsNull(result);
        }

        [Test]
        public async Task GetCachedResponseAsync_RedisThrowsException_ReturnsNullAndLogsError()
        {
            // Arrange
            var config = SetupMockConfiguration();
            InitializeService(config);
            var idempotencyKey = "test-key-redis-error";
            var redisException = new RedisConnectionException(ConnectionFailureType.InternalFailure, "Simulated Redis error");
            _mockRedisDatabase.Setup(db => db.StringGetAsync($"idempotency:{idempotencyKey}", It.IsAny<CommandFlags>()))
                              .ThrowsAsync(redisException);

            // Act
            var result = await _service.GetCachedResponseAsync(idempotencyKey);

            // Assert
            Assert.IsNull(result);
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error retrieving idempotency key")),
                    redisException,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        // --- Tests for CacheResponseAsync ---

        [Test]
        public async Task CacheResponseAsync_NullOrWhitespaceIdempotencyKey_DoesNotCacheAndLogsWarning()
        {
            // Arrange
            var config = SetupMockConfiguration();
            InitializeService(config);

            // Act
            await _service.CacheResponseAsync(null!, 200, null, "application/json", "hash123");
            await _service.CacheResponseAsync("", 200, null, "application/json", "hash123");
            await _service.CacheResponseAsync("   ", 200, null, "application/json", "hash123");

            // Assert
            _mockRedisDatabase.Verify(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()), Times.Never);
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Cannot cache response for null or whitespace idempotency key.")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Exactly(3));
        }

        [Test]
        public async Task CacheResponseAsync_NullOrWhitespaceRequestPayloadHash_DoesNotCacheAndLogsWarning()
        {
            // Arrange
            var config = SetupMockConfiguration();
            InitializeService(config);
            var idempotencyKey = "test-key-no-hash";

            // Act
            await _service.CacheResponseAsync(idempotencyKey, 200, null, "application/json", null!);
            await _service.CacheResponseAsync(idempotencyKey, 200, null, "application/json", "");
            await _service.CacheResponseAsync(idempotencyKey, 200, null, "application/json", "   ");

            // Assert
            _mockRedisDatabase.Verify(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()), Times.Never);
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Request payload hash was null or whitespace for idempotency key")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Exactly(3));
        }

        [Test]
        public async Task CacheResponseAsync_ValidInput_CachesResponseInRedis()
        {
            // Arrange
            var config = SetupMockConfiguration("60"); // 60 minutes expiry
            InitializeService(config);
            var idempotencyKey = "test-key-cache-success";
            var statusCode = 201;
            var responseBody = new { id = 1, name = "test item" };
            var contentType = "application/json";
            var requestHash = "valid-hash-123";
            var expectedExpiry = TimeSpan.FromMinutes(60);

            _mockRedisDatabase.Setup(db => db.StringSetAsync($"idempotency:{idempotencyKey}", It.IsAny<RedisValue>(), expectedExpiry, It.IsAny<When>(), It.IsAny<CommandFlags>()))
                              .ReturnsAsync(true); // Simulate successful set

            // Act
            await _service.CacheResponseAsync(idempotencyKey, statusCode, responseBody, contentType, requestHash);

            // Assert
            _mockRedisDatabase.Verify(
                db => db.StringSetAsync(
                    $"idempotency:{idempotencyKey}",
                    It.IsAny<RedisValue>(),
                    expectedExpiry,
                    It.IsAny<When>(), 
                    It.IsAny<CommandFlags>()), // Default CommandFlags
                Times.Once);
        }

        [Test]
        public async Task CacheResponseAsync_RedisStringSetReturnsFalse_LogsWarning()
        {
            // Arrange
            var config = SetupMockConfiguration();
            InitializeService(config);
            var idempotencyKey = "test-key-set-false";
            _mockRedisDatabase.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                              .ReturnsAsync(false); // Simulate StringSet failing

            // Act
            await _service.CacheResponseAsync(idempotencyKey, 200, null, "application/json", "hash123");

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to cache response for idempotency key")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Test]
        public async Task CacheResponseAsync_RedisThrowsException_LogsError()
        {
            // Arrange
            var config = SetupMockConfiguration();
            InitializeService(config);
            var idempotencyKey = "test-key-cache-redis-error";
            var redisException = new RedisServerException("Simulated Redis server error during set");
            _mockRedisDatabase.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                              .ThrowsAsync(redisException);
            _mockRedisDatabase.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                              .ThrowsAsync(redisException);

            // Act
            await _service.CacheResponseAsync(idempotencyKey, 200, null, "application/json", "hash123");

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error caching response")),
                    redisException,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Test]
        public void Constructor_NullConnectionMultiplexer_ThrowsArgumentNullException()
        {
            // Arrange
            var config = SetupMockConfiguration();
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new RedisIdempotencyService(null!, config, _mockLogger.Object));
        }
    }
}
