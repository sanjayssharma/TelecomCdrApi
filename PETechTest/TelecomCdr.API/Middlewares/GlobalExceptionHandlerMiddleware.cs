using System.Net;
using System.Text.Json;
using TelecomCdr.Core.Models.DTO;

namespace TelecomCdr.API.Middlewares
{
    /// <summary>
    /// Middleware for handling exceptions globally and returning a standardized JSON error response.
    /// </summary>
    public class GlobalExceptionHandlerMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;
        private readonly IHostEnvironment _env;

        public GlobalExceptionHandlerMiddleware(
            RequestDelegate next,
            ILogger<GlobalExceptionHandlerMiddleware> logger,
            IHostEnvironment env)
        {
            _next = next;
            _logger = logger;
            _env = env;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred: {ErrorMessage}", ex.Message);

                context.Response.ContentType = "application/json";
                var statusCode = (int)HttpStatusCode.InternalServerError; // Default to 500
                string message = "An unexpected internal server error has occurred.";
                string? details = null;

                if (_env.IsDevelopment())
                {
                    // Include more details in development
                    message = ex.Message;
                    details = ex.StackTrace?.ToString();
                }

                context.Response.StatusCode = statusCode;

                var errorResponse = new ErrorResponseDto(statusCode, message, details);
                var jsonResponse = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                await context.Response.WriteAsync(jsonResponse);
            }
        }
    }
}
