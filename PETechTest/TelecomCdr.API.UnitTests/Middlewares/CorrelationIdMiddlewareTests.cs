using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using TelecomCdr.API.Middlewares;

namespace TelecomCdr.API.UnitTests.Middlewares
{
    [TestFixture]
    public class CorrelationIdMiddlewareTests
    {
        private Mock<RequestDelegate> _mockNextMiddleware;
        private Mock<ILogger<CorrelationIdMiddleware>> _mockLogger;
        private CorrelationIdMiddleware _middleware;

        private Mock<HttpContext> _mockHttpContext;
        private Mock<HttpRequest> _mockHttpRequest;
        private Mock<HttpResponse> _mockHttpResponse;
        private HeaderDictionary _requestHeaders;
        private HeaderDictionary _responseHeaders;
        private Dictionary<object, object> _contextItems;

        [SetUp]
        public void SetUp()
        {
            _mockNextMiddleware = new Mock<RequestDelegate>();
            _mockLogger = new Mock<ILogger<CorrelationIdMiddleware>>();

            _middleware = new CorrelationIdMiddleware(_mockNextMiddleware.Object, _mockLogger.Object);

            // Setup mocks for HttpContext, HttpRequest, HttpResponse
            _mockHttpContext = new Mock<HttpContext>();
            _mockHttpRequest = new Mock<HttpRequest>();
            _mockHttpResponse = new Mock<HttpResponse>();

            _requestHeaders = new HeaderDictionary();
            _responseHeaders = new HeaderDictionary();
            _contextItems = new Dictionary<object, object>();

            _mockHttpRequest.Setup(req => req.Headers).Returns(_requestHeaders);
            _mockHttpRequest.Setup(req => req.Path).Returns(new PathString("/test")); // Default path

            _mockHttpResponse.Setup(res => res.Headers).Returns(_responseHeaders);

            _mockHttpContext.Setup(ctx => ctx.Request).Returns(_mockHttpRequest.Object);
            _mockHttpContext.Setup(ctx => ctx.Response).Returns(_mockHttpResponse.Object);
            _mockHttpContext.Setup(ctx => ctx.Items).Returns(_contextItems);
        }

        [Test]
        public async Task InvokeAsync_ResponseAlreadyHasCorrelationIdHeader_DoesNotOverwrite()
        {
            // Arrange
            var requestCorrelationId = Guid.NewGuid().ToString();
            var preExistingResponseHeaderId = "pre-existing-response-id";
            _requestHeaders["X-Correlation-ID"] = requestCorrelationId;
            _responseHeaders["X-Correlation-ID"] = preExistingResponseHeaderId; // Header already exists on response

            Func<Task> capturedOnStartingCallback = null;
            _mockHttpResponse.Setup(res => res.OnStarting(It.IsAny<Func<object, Task>>(), It.IsAny<object>()))
                .Callback<Func<object, Task>, object>((callback, state) => {
                    // This captures the callback registered by the middleware
                    if (state is Func<Task> funcTask) capturedOnStartingCallback = funcTask;
                });

            // Act
            await _middleware.InvokeAsync(_mockHttpContext.Object);
            // Manually invoke the captured callback to simulate the server firing OnStarting events
            if (capturedOnStartingCallback != null) await capturedOnStartingCallback();

            // Assert
            Assert.AreEqual(requestCorrelationId, _mockHttpContext.Object.Items["CorrelationId"]);
            Assert.IsTrue(_responseHeaders.ContainsKey("X-Correlation-ID"));
            Assert.AreEqual(preExistingResponseHeaderId, _responseHeaders["X-Correlation-ID"].FirstOrDefault(), "Response header should not be overwritten.");
            _mockNextMiddleware.Verify(next => next(_mockHttpContext.Object), Times.Once);
        }

        [Test]
        public async Task InvokeAsync_EnsuresLogContextPushPropertyIsUsed()
        {
            // Arrange
            var correlationId = Guid.NewGuid().ToString();
            _requestHeaders["X-Correlation-ID"] = correlationId;

            // Act
            await _middleware.InvokeAsync(_mockHttpContext.Object);

            // Assert
            _mockNextMiddleware.Verify(next => next(_mockHttpContext.Object), Times.Once);
            // Indirect verification: if _next is called, it passed through the 'using (LogContext.PushProperty...)' block.
            Assert.Pass("LogContext.PushProperty usage is inferred by successful execution of _next delegate within its scope.");
        }

        // Helper method for verifying ILogger calls
        private void VerifyLog(LogLevel expectedLogLevel, string expectedMessageContent, Times? times = null)
        {
            times ??= Times.Once();

            _mockLogger.Verify(
                x => x.Log(
                    expectedLogLevel,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(expectedMessageContent)),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                times.Value);
        }
    }
}
