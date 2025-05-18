using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Moq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Core.Filters;

namespace TelecomCdr.Core.UnitTests.Filters
{
    [TestFixture]
    public class IdempotencyAttributeTests
    {
        private Mock<IIdempotencyService> _mockIdempotencyService;
        private Mock<ILogger<IdempotencyAttribute>> _mockLogger;
        private IdempotencyAttribute _idempotencyAttribute;

        private ActionExecutingContext _actionExecutingContext;
        private ActionExecutionDelegate _nextActionExecutionDelegate;
        private Mock<HttpContext> _mockHttpContext;
        private Mock<HttpRequest> _mockHttpRequest;
        private Mock<HttpResponse> _mockHttpResponse;
        private MemoryStream _requestBodyStream;
        private MemoryStream _responseBodyStream;

        [SetUp]
        public void SetUp()
        {
            _mockIdempotencyService = new Mock<IIdempotencyService>();
            _mockLogger = new Mock<ILogger<IdempotencyAttribute>>();

            // Attribute is instantiated directly with mocks
            _idempotencyAttribute = new IdempotencyAttribute(_mockIdempotencyService.Object, _mockLogger.Object);

            // Setup mock HttpContext and related objects
            _mockHttpContext = new Mock<HttpContext>();
            _mockHttpRequest = new Mock<HttpRequest>();
            _mockHttpResponse = new Mock<HttpResponse>();

            _requestBodyStream = new MemoryStream();
            _responseBodyStream = new MemoryStream();

            _mockHttpRequest.Setup(req => req.HttpContext).Returns(_mockHttpContext.Object);
            _mockHttpRequest.SetupProperty(req => req.Body, _requestBodyStream); // Setup Body to use our MemoryStream
            _mockHttpRequest.Setup(req => req.Headers).Returns(new HeaderDictionary()); // Default empty headers

            _mockHttpResponse.SetupProperty(res => res.Body, _responseBodyStream); // Setup Body to use our MemoryStream
            _mockHttpResponse.SetupProperty(res => res.ContentType, "application/json"); // Default response content type
            _mockHttpResponse.SetupProperty(res => res.StatusCode, StatusCodes.Status200OK); // Default status code

            _mockHttpContext.Setup(hc => hc.Request).Returns(_mockHttpRequest.Object);
            _mockHttpContext.Setup(hc => hc.Response).Returns(_mockHttpResponse.Object);

            var actionContext = new ActionContext(
                _mockHttpContext.Object,
                new RouteData(),
                new ActionDescriptor()
            );

            _actionExecutingContext = new ActionExecutingContext(
                actionContext,
                new List<IFilterMetadata>(),
                new Dictionary<string, object>(),
                new Mock<ControllerBase>().Object
            );

            // Default _next delegate: returns a successful result
            _nextActionExecutionDelegate = () => {
                // Simulate action writing to response
                var writer = new StreamWriter(_mockHttpResponse.Object.Body, Encoding.UTF8, 1024, true);
                writer.Write("{\"message\":\"Action Executed\"}");
                writer.Flush();
                _mockHttpResponse.Object.Body.Seek(0, SeekOrigin.Begin);
                _mockHttpResponse.Object.StatusCode = StatusCodes.Status200OK;

                return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new Mock<ControllerBase>().Object)
                {
                    Result = new OkObjectResult(new { Message = "Action Executed" })
                });
            };
        }

        [TearDown]
        public void TearDown()
        {
            _requestBodyStream?.Dispose();
            _responseBodyStream?.Dispose();
        }

        private void SetupRequest(string method, string idempotencyKey, string bodyContent = "", long? contentLength = null)
        {
            _mockHttpRequest.Setup(req => req.Method).Returns(method);
            _mockHttpRequest.Setup(req => req.Path).Returns(new PathString("/test/path"));

            if (idempotencyKey != null)
            {
                _mockHttpRequest.Object.Headers["Idempotency-Key"] = idempotencyKey;
            }
            else
            {
                _mockHttpRequest.Object.Headers.Remove("Idempotency-Key");
            }

            var bodyBytes = Encoding.UTF8.GetBytes(bodyContent);
            _requestBodyStream.Write(bodyBytes, 0, bodyBytes.Length);
            _requestBodyStream.Seek(0, SeekOrigin.Begin);
            _mockHttpRequest.Setup(req => req.ContentLength).Returns(contentLength ?? bodyBytes.Length);
        }

        private string CalculateTestHash(string content)
        {
            if (string.IsNullOrEmpty(content) && (_mockHttpRequest.Object.ContentLength ?? 0) == 0)
            {
                return "EMPTY_BODY_HASH";
            }
            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        [TestCase("GET")]
        [TestCase("OPTIONS")]
        [TestCase("HEAD")]
        public async Task OnActionExecutionAsync_NonWriteMethod_SkipsIdempotencyAndExecutesNext(string httpMethod)
        {
            // Arrange
            SetupRequest(httpMethod, "any-key");
            bool nextCalled = false;
            _nextActionExecutionDelegate = () => { nextCalled = true; return Task.FromResult(new ActionExecutedContext(_actionExecutingContext, new List<IFilterMetadata>(), null)); };

            // Act
            await _idempotencyAttribute.OnActionExecutionAsync(_actionExecutingContext, _nextActionExecutionDelegate);

            // Assert
            Assert.IsTrue(nextCalled);
            _mockIdempotencyService.Verify(s => s.GetCachedResponseAsync(It.IsAny<string>()), Times.Never);
        }

        [Test]
        public async Task OnActionExecutionAsync_NoIdempotencyKeyHeader_ReturnsBadRequest()
        {
            // Arrange
            SetupRequest("POST", null); // No Idempotency-Key header

            // Act
            await _idempotencyAttribute.OnActionExecutionAsync(_actionExecutingContext, _nextActionExecutionDelegate);

            // Assert
            Assert.IsInstanceOf<BadRequestObjectResult>(_actionExecutingContext.Result);
            Assert.AreEqual("Idempotency-Key header is required.", (_actionExecutingContext.Result as BadRequestObjectResult).Value);
        }

        [Test]
        public async Task OnActionExecutionAsync_EmptyIdempotencyKeyHeader_ReturnsBadRequest()
        {
            // Arrange
            SetupRequest("POST", ""); // Empty Idempotency-Key header

            // Act
            await _idempotencyAttribute.OnActionExecutionAsync(_actionExecutingContext, _nextActionExecutionDelegate);

            // Assert
            Assert.IsInstanceOf<BadRequestObjectResult>(_actionExecutingContext.Result);
            Assert.AreEqual("Idempotency-Key header cannot be empty.", (_actionExecutingContext.Result as BadRequestObjectResult).Value);
        }

        [Test]
        public async Task OnActionExecutionAsync_CachedResponseExists_MatchingHash_ReturnsCachedContentResult()
        {
            // Arrange
            var idempotencyKey = "test-key-cached-match";
            var requestBody = "{\"data\":\"payload\"}";
            var requestHash = CalculateTestHash(requestBody);
            SetupRequest("POST", idempotencyKey, requestBody);

            var cachedEntry = new CachedIdempotencyEntry
            {
                RequestPayloadHash = requestHash,
                StatusCode = StatusCodes.Status201Created,
                BodyJson = "{\"id\":123,\"message\":\"Cached success\"}",
                ContentType = "application/json; charset=utf-8"
            };
            _mockIdempotencyService.Setup(s => s.GetCachedResponseAsync(idempotencyKey)).ReturnsAsync(cachedEntry);

            // Act
            await _idempotencyAttribute.OnActionExecutionAsync(_actionExecutingContext, _nextActionExecutionDelegate);

            // Assert
            Assert.IsInstanceOf<ContentResult>(_actionExecutingContext.Result);
            var contentResult = _actionExecutingContext.Result as ContentResult;
            Assert.AreEqual(cachedEntry.BodyJson, contentResult.Content);
            Assert.AreEqual(cachedEntry.StatusCode, contentResult.StatusCode);
            Assert.AreEqual(cachedEntry.ContentType, contentResult.ContentType);
        }

        [Test]
        public async Task OnActionExecutionAsync_CachedResponseExists_MatchingHash_NoBody_ReturnsStatusCodeResult()
        {
            // Arrange
            var idempotencyKey = "test-key-cached-match-no-body";
            var requestBody = "{\"data\":\"payload_for_no_content_response\"}";
            var requestHash = CalculateTestHash(requestBody);
            SetupRequest("POST", idempotencyKey, requestBody);

            var cachedEntry = new CachedIdempotencyEntry
            {
                RequestPayloadHash = requestHash,
                StatusCode = StatusCodes.Status204NoContent,
                BodyJson = null, // No body for 204
                ContentType = "application/json" // ContentType might still be set
            };
            _mockIdempotencyService.Setup(s => s.GetCachedResponseAsync(idempotencyKey)).ReturnsAsync(cachedEntry);

            // Act
            await _idempotencyAttribute.OnActionExecutionAsync(_actionExecutingContext, _nextActionExecutionDelegate);

            // Assert
            Assert.IsInstanceOf<StatusCodeResult>(_actionExecutingContext.Result);
            var statusCodeResult = _actionExecutingContext.Result as StatusCodeResult;
            Assert.AreEqual(cachedEntry.StatusCode, statusCodeResult.StatusCode);
        }


        [Test]
        public async Task OnActionExecutionAsync_CachedResponseExists_DifferentHash_ReturnsConflict()
        {
            // Arrange
            var idempotencyKey = "test-key-cached-mismatch";
            var requestBody = "{\"data\":\"new payload\"}";
            var currentRequestHash = CalculateTestHash(requestBody);
            SetupRequest("POST", idempotencyKey, requestBody);

            var cachedEntry = new CachedIdempotencyEntry
            {
                RequestPayloadHash = "DIFFERENT_STORED_HASH",
                StatusCode = StatusCodes.Status200OK,
                BodyJson = "{\"message\":\"Old data\"}",
                ContentType = "application/json"
            };
            _mockIdempotencyService.Setup(s => s.GetCachedResponseAsync(idempotencyKey)).ReturnsAsync(cachedEntry);

            // Act
            await _idempotencyAttribute.OnActionExecutionAsync(_actionExecutingContext, _nextActionExecutionDelegate);

            // Assert
            Assert.IsInstanceOf<ConflictObjectResult>(_actionExecutingContext.Result);
            Assert.AreEqual($"The Idempotency-Key '{idempotencyKey}' was used with a different request payload.", (_actionExecutingContext.Result as ConflictObjectResult).Value);
        }

        [Test]
        public async Task OnActionExecutionAsync_NoCachedResponse_ExecutesAction_CachesSuccessfulJsonResponse()
        {
            // Arrange
            var idempotencyKey = "test-key-new-request";
            var requestBody = "{\"data\":\"fresh payload\"}";
            var requestHash = CalculateTestHash(requestBody);
            SetupRequest("POST", idempotencyKey, requestBody);

            _mockIdempotencyService.Setup(s => s.GetCachedResponseAsync(idempotencyKey)).ReturnsAsync((CachedIdempotencyEntry)null);

            var expectedResponseJson = "{\"message\":\"Action Executed\"}"; // From default _next delegate
            var expectedStatusCode = StatusCodes.Status200OK;
            var expectedContentType = "application/json";

            // Modify the default delegate to ensure ContentType is set as expected by the attribute's caching logic
            _nextActionExecutionDelegate = () => {
                _mockHttpResponse.Object.ContentType = expectedContentType;
                var writer = new StreamWriter(_mockHttpResponse.Object.Body, Encoding.UTF8, 1024, true);
                writer.Write(expectedResponseJson);
                writer.Flush();
                _mockHttpResponse.Object.Body.Seek(0, SeekOrigin.Begin);
                _mockHttpResponse.Object.StatusCode = expectedStatusCode;
                return Task.FromResult(new ActionExecutedContext(_actionExecutingContext, new List<IFilterMetadata>(), null)
                { Result = new OkObjectResult(JsonSerializer.Deserialize<object>(expectedResponseJson)) });
            };


            // Act
            await _idempotencyAttribute.OnActionExecutionAsync(_actionExecutingContext, _nextActionExecutionDelegate);

            // Assert
            _mockIdempotencyService.Verify(s => s.CacheResponseAsync(
                idempotencyKey,
                expectedStatusCode,
                It.Is<object>(body => body.ToString() == expectedResponseJson),
                expectedContentType,
                requestHash
            ), Times.Once);
        }

        [Test]
        public async Task OnActionExecutionAsync_NoCachedResponse_ExecutesAction_CachesSuccessfulTextResponse()
        {
            // Arrange
            var idempotencyKey = "test-key-new-text-request";
            var requestBody = "text_payload_for_text_response";
            var requestHash = CalculateTestHash(requestBody);
            SetupRequest("POST", idempotencyKey, requestBody);

            _mockIdempotencyService.Setup(s => s.GetCachedResponseAsync(idempotencyKey)).ReturnsAsync((CachedIdempotencyEntry)null);

            var expectedResponseText = "Action Executed Plain Text";
            var expectedStatusCode = StatusCodes.Status200OK;
            var expectedContentType = "text/plain";

            _nextActionExecutionDelegate = () => {
                _mockHttpResponse.Object.ContentType = expectedContentType;
                var writer = new StreamWriter(_mockHttpResponse.Object.Body, Encoding.UTF8, 1024, true);
                writer.Write(expectedResponseText);
                writer.Flush();
                _mockHttpResponse.Object.Body.Seek(0, SeekOrigin.Begin);
                _mockHttpResponse.Object.StatusCode = expectedStatusCode;
                return Task.FromResult(new ActionExecutedContext(_actionExecutingContext, new List<IFilterMetadata>(), null)
                { Result = new ContentResult { Content = expectedResponseText, ContentType = expectedContentType, StatusCode = expectedStatusCode } }); // Simulating a text result
            };

            // Act
            await _idempotencyAttribute.OnActionExecutionAsync(_actionExecutingContext, _nextActionExecutionDelegate);

            // Assert
            _mockIdempotencyService.Verify(s => s.CacheResponseAsync(
                idempotencyKey,
                expectedStatusCode,
                It.Is<object>(body => body.ToString() == expectedResponseText),
                expectedContentType,
                requestHash
            ), Times.Once);
        }


        [Test]
        public async Task OnActionExecutionAsync_NoCachedResponse_ActionFails_DoesNotCache()
        {
            // Arrange
            var idempotencyKey = "test-key-action-fail";
            var requestBody = "{\"data\":\"payload for failure\"}";
            SetupRequest("POST", idempotencyKey, requestBody);
            var requestHash = CalculateTestHash(requestBody);

            _mockIdempotencyService.Setup(s => s.GetCachedResponseAsync(idempotencyKey)).ReturnsAsync((CachedIdempotencyEntry)null);

            _nextActionExecutionDelegate = () => {
                _mockHttpResponse.Object.StatusCode = StatusCodes.Status500InternalServerError; // Simulate failure
                var writer = new StreamWriter(_mockHttpResponse.Object.Body);
                writer.Write("{\"error\":\"Server Error\"}");
                writer.Flush();
                _mockHttpResponse.Object.Body.Seek(0, SeekOrigin.Begin);
                return Task.FromResult(new ActionExecutedContext(_actionExecutingContext, new List<IFilterMetadata>(), null)
                { Result = new StatusCodeResult(StatusCodes.Status500InternalServerError) });
            };

            // Act
            await _idempotencyAttribute.OnActionExecutionAsync(_actionExecutingContext, _nextActionExecutionDelegate);

            // Assert
            _mockIdempotencyService.Verify(s => s.CacheResponseAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<object>(), It.IsAny<string>(), It.IsAny<string>()
            ), Times.Never);
        }

        [Test]
        public async Task OnActionExecutionAsync_NoCachedResponse_ActionThrowsException_DoesNotCache()
        {
            // Arrange
            var idempotencyKey = "test-key-action-exception";
            var requestBody = "{\"data\":\"payload for exception\"}";
            SetupRequest("POST", idempotencyKey, requestBody);

            _mockIdempotencyService.Setup(s => s.GetCachedResponseAsync(idempotencyKey)).ReturnsAsync((CachedIdempotencyEntry)null);

            _nextActionExecutionDelegate = () => {
                // Simulate exception during action execution
                var executedContext = new ActionExecutedContext(_actionExecutingContext, new List<IFilterMetadata>(), null)
                {
                    Exception = new InvalidOperationException("Action error!"),
                    ExceptionHandled = false // Important for the filter to see the exception
                };
                // Even if an exception occurs, the response status code might be set by an exception handler middleware later,
                // but for the filter, executedContext.Exception is key.
                _mockHttpResponse.Object.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.FromResult(executedContext);
            };


            // Act
            await _idempotencyAttribute.OnActionExecutionAsync(_actionExecutingContext, _nextActionExecutionDelegate);

            // Assert
            _mockIdempotencyService.Verify(s => s.CacheResponseAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<object>(), It.IsAny<string>(), It.IsAny<string>()
            ), Times.Never);
            _mockLogger.Verify(
               x => x.Log(
                   LogLevel.Error,
                   It.IsAny<EventId>(),
                   It.Is<It.IsAnyType>((v, t) => v.ToString().Contains($"Action execution failed for Idempotency-Key: {idempotencyKey}")),
                   It.IsAny<InvalidOperationException>(),
                   It.IsAny<Func<It.IsAnyType, Exception, string>>()),
               Times.Once);
        }

        [Test]
        public async Task CalculateRequestPayloadHashAsync_EmptyBody_ReturnsFixedHash()
        {
            // Arrange
            SetupRequest("POST", "some-key", "", 0); // Empty body, explicit zero length

            // Act
            // We test this indirectly by checking what hash is passed to CacheResponseAsync
            var idempotencyKey = "test-key-empty-body-hash";
            _mockHttpRequest.Object.Headers["Idempotency-Key"] = idempotencyKey; // Ensure key is set for caching path

            _mockIdempotencyService.Setup(s => s.GetCachedResponseAsync(idempotencyKey)).ReturnsAsync((CachedIdempotencyEntry)null);
            _nextActionExecutionDelegate = () => {
                _mockHttpResponse.Object.StatusCode = StatusCodes.Status200OK;
                var writer = new StreamWriter(_mockHttpResponse.Object.Body);
                writer.Write("{\"message\":\"OK\"}");
                writer.Flush();
                _mockHttpResponse.Object.Body.Seek(0, SeekOrigin.Begin);
                return Task.FromResult(new ActionExecutedContext(_actionExecutingContext, new List<IFilterMetadata>(), null)
                { Result = new OkObjectResult("OK") });
            };

            await _idempotencyAttribute.OnActionExecutionAsync(_actionExecutingContext, _nextActionExecutionDelegate);

            // Assert
            _mockIdempotencyService.Verify(s => s.CacheResponseAsync(
                idempotencyKey,
                StatusCodes.Status200OK,
                It.IsAny<object>(),
                It.IsAny<string>(),
                "EMPTY_BODY_HASH"
            ), Times.Once);
        }
    }
}
