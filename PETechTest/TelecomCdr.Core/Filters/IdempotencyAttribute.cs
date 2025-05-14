using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using TelecomCdr.Abstraction.Interfaces.Service;

namespace TelecomCdr.Core.Filters
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class IdempotencyAttribute : ActionFilterAttribute
    {
        private readonly IIdempotencyService _idempotencyService;
        private readonly ILogger<IdempotencyAttribute> _logger;

        public IdempotencyAttribute(IIdempotencyService idempotencyService, ILogger<IdempotencyAttribute> logger)
        {
            _idempotencyService = idempotencyService ?? throw new ArgumentNullException(nameof(idempotencyService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var request = context.HttpContext.Request;

            if (request.Method != HttpMethods.Post &&
                request.Method != HttpMethods.Put &&
                request.Method != HttpMethods.Patch &&
                request.Method != HttpMethods.Delete)
            {
                _logger.LogInformation("Idempotency filter skipped for non-write HTTP method: {Method}", request.Method);
                await next();
                return;
            }

            if (!request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKeyHeaderValue))
            {
                _logger.LogWarning("Idempotency-Key header missing. Proceeding without idempotency check.");
                 context.Result = new BadRequestObjectResult("Idempotency-Key header is required.");
                 return;
            }

            string idempotencyKey = idempotencyKeyHeaderValue.ToString();
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                _logger.LogWarning("Idempotency-Key header was present but empty. Proceeding without idempotency check.");
                 context.Result = new BadRequestObjectResult("Idempotency-Key header cannot be empty.");
                 return;
            }

            _logger.LogInformation("Processing request with Idempotency-Key: {IdempotencyKey}", idempotencyKey);

            // Enable buffering to allow reading the request body multiple times
            request.EnableBuffering();

            string currentRequestPayloadHash = await CalculateRequestPayloadHashAsync(request);

            var cachedEntry = await _idempotencyService.GetCachedResponseAsync(idempotencyKey);

            if (cachedEntry != null)
            {
                if (cachedEntry.RequestPayloadHash == currentRequestPayloadHash)
                {
                    _logger.LogInformation("Returning cached response for Idempotency-Key: {IdempotencyKey} with matching payload hash.", idempotencyKey);
                    context.HttpContext.Response.ContentType = cachedEntry.ContentType;
                    if (cachedEntry.BodyJson != null)
                    {
                        context.Result = new ContentResult // Use ContentResult to write raw JSON
                        {
                            Content = cachedEntry.BodyJson,
                            ContentType = cachedEntry.ContentType,
                            StatusCode = cachedEntry.StatusCode
                        };
                    }
                    else
                    {
                        context.Result = new StatusCodeResult(cachedEntry.StatusCode);
                    }
                    return; // Return cached response
                }
                else
                {
                    _logger.LogWarning("Idempotency-Key {IdempotencyKey} reused with a different request payload. Original hash: {OriginalHash}, New hash: {NewHash}. Returning 409 Conflict.",
                        idempotencyKey, cachedEntry.RequestPayloadHash, currentRequestPayloadHash);
                    context.Result = new ConflictObjectResult($"The Idempotency-Key '{idempotencyKey}' was used with a different request payload.");
                    return;
                }
            }

            _logger.LogInformation("No cached response for Idempotency-Key: {IdempotencyKey} and hash {RequestHash}. Proceeding with action execution.", idempotencyKey, currentRequestPayloadHash);

            var originalResponseBodyStream = context.HttpContext.Response.Body;
            using var memoryStream = new MemoryStream();
            context.HttpContext.Response.Body = memoryStream;

            var executedContext = await next(); // Execute the action

            memoryStream.Seek(0, SeekOrigin.Begin);
            var responseBodyText = await new StreamReader(memoryStream).ReadToEndAsync();
            memoryStream.Seek(0, SeekOrigin.Begin);
            await memoryStream.CopyToAsync(originalResponseBodyStream);
            context.HttpContext.Response.Body = originalResponseBodyStream;

            if (executedContext.Exception == null && context.HttpContext.Response.StatusCode >= 200 && context.HttpContext.Response.StatusCode < 300)
            {
                object? responseBodyForCache = null;
                if (!string.IsNullOrEmpty(responseBodyText) && context.HttpContext.Response.ContentType?.ToLower().Contains("application/json") == true)
                {
                    responseBodyForCache = responseBodyText; // Store raw JSON string
                }
                else if (!string.IsNullOrEmpty(responseBodyText))
                {
                    _logger.LogInformation("Response for Idempotency-Key {IdempotencyKey} has ContentType {ContentType} and response body will be cached as plain text.",
                       idempotencyKey, context.HttpContext.Response.ContentType);
                    responseBodyForCache = responseBodyText;
                }

                _logger.LogInformation("Caching response for Idempotency-Key: {IdempotencyKey}, StatusCode: {StatusCode}, RequestHash: {RequestHash}",
                                       idempotencyKey, context.HttpContext.Response.StatusCode, currentRequestPayloadHash);
                await _idempotencyService.CacheResponseAsync(
                    idempotencyKey,
                    context.HttpContext.Response.StatusCode,
                    responseBodyForCache,
                    context.HttpContext.Response.ContentType ?? "application/octet-stream",
                    currentRequestPayloadHash // Store the hash of the request payload
                );
            }
            else if (executedContext.Exception != null)
            {
                _logger.LogError(executedContext.Exception, "Action execution failed for Idempotency-Key: {IdempotencyKey}. Response not cached.", idempotencyKey);
            }
            else
            {
                _logger.LogWarning("Response for Idempotency-Key {IdempotencyKey} was not successful (StatusCode: {StatusCode}). Not caching.",
                                  idempotencyKey, context.HttpContext.Response.StatusCode);
            }
        }

        /// <summary>
        /// Calculates SHA256 hash of the request body.
        /// Ensures the request stream is reset to its original position after reading.
        /// </summary>
        private async Task<string> CalculateRequestPayloadHashAsync(HttpRequest request)
        {
            request.Body.Seek(0, SeekOrigin.Begin); // Ensure stream is at the beginning

            using var sha256 = SHA256.Create();
            byte[] hashBytes;

            // For form data, the body might be complex. Hashing the raw stream is a general approach.
            // If specific form fields need to be part of the hash, more complex logic is needed.
            // For JSON/XML, this approach is fine.
            if (request.ContentLength.HasValue && request.ContentLength > 0)
            {
                hashBytes = await sha256.ComputeHashAsync(request.Body);
            }
            else
            {
                // If there's no body or content length is zero, return a fixed hash or empty string.
                // This handles GET requests if the filter were to apply to them, or POSTs with no body.
                return "EMPTY_BODY_HASH"; // Or string.Empty, or a hash of an empty string.
            }

            request.Body.Seek(0, SeekOrigin.Begin); // Reset stream for downstream consumers (MVC model binding)

            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
