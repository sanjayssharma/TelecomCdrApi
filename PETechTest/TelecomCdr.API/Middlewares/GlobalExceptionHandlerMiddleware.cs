using FluentValidation;
using System.Net;
using System.Text.Json;

namespace TelecomCdr.API.Middlewares
{
    /// <summary>
    /// Middleware for handling exceptions globally and returning a standardized JSON error response.
    /// </summary>
    public class GlobalExceptionHandlerMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;

        public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task Invoke(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception error)
            {
                var response = context.Response;
                response.ContentType = "application/json";
                var correlationId = context.Items["CorrelationId"]?.ToString() ?? "N/A";

                _logger.LogError(error, "An unhandled exception occurred. CorrelationId: {CorrelationId}, Request: {Method} {Path}",
                    correlationId, context.Request.Method, context.Request.Path);

                var responseModel = new { Message = "An unexpected error occurred.", CorrelationId = correlationId, Details = (string)null };

                switch (error)
                {
                    case ValidationException e:
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        responseModel = new { Message = "One or more validation errors occurred.", CorrelationId = correlationId, Details = string.Join("; ", e.Errors.Select(err => err.ErrorMessage)) };
                        break;
                    case ArgumentException e:
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        responseModel = new { Message = e.Message, CorrelationId = correlationId, Details = (string)null };
                        break;
                    case FormatException e: // Catch format exceptions from date/time parsing
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        responseModel = new { Message = $"Error parsing input data: {e.Message}", CorrelationId = correlationId, Details = (string)null };
                        break;
                    case FileNotFoundException e:
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        responseModel = new { Message = e.Message, CorrelationId = correlationId, Details = (string)null };
                        break;
                    case InvalidOperationException e when e.Message.Contains("Blob storage is not configured"):
                        response.StatusCode = (int)HttpStatusCode.NotImplemented;
                        responseModel = new { Message = "This operation requires blob storage, which is not configured.", CorrelationId = correlationId, Details = (string)null };
                        break;
                    default:
                        response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        break;
                }

                var result = JsonSerializer.Serialize(responseModel, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                await response.WriteAsync(result);
            }
        }
    }
}
