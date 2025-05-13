using Microsoft.Extensions.Primitives;
using Serilog.Context;

namespace TelecomCdr.API.Middlewares
{
    public class CorrelationIdMiddleware
    {
        private const string CorrelationIdHeaderName = "X-Correlation-ID";
        private readonly RequestDelegate _next;
        private readonly ILogger<CorrelationIdMiddleware> _logger;

        public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var correlationId = GetOrGenerateCorrelationId(context);
            context.Items["CorrelationId"] = correlationId;

            using (LogContext.PushProperty("CorrelationId", correlationId))
            {
                context.Response.OnStarting(() =>
                {
                    if (!context.Response.Headers.ContainsKey(CorrelationIdHeaderName))
                    {
                        context.Response.Headers.Append(CorrelationIdHeaderName, correlationId);
                    }
                    return Task.CompletedTask;
                });

                _logger.LogDebug("Correlation ID {CorrelationId} set for request {Path}", correlationId, context.Request.Path);
                await _next(context);
            }
        }

        private string GetOrGenerateCorrelationId(HttpContext context)
        {
            if (context.Request.Headers.TryGetValue(CorrelationIdHeaderName, out StringValues correlationIdValues) &&
                correlationIdValues.FirstOrDefault() is string headerCorrelationId &&
                !string.IsNullOrWhiteSpace(headerCorrelationId))
            {
                _logger.LogDebug("Using existing Correlation ID from header: {CorrelationId}", headerCorrelationId);
                return headerCorrelationId;
            }

            var newCorrelationId = Guid.NewGuid().ToString();
            _logger.LogDebug("Generated new Correlation ID: {CorrelationId}", newCorrelationId);
            return newCorrelationId;
        }
    }
}
