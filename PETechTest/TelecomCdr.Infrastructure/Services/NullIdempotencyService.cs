using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;

namespace TelecomCdr.Infrastructure.Services
{
    /// <summary>
    /// A null object implementation of the idempotency service.
    /// Does not perform any caching or checking, effectively disabling idempotency.
    /// </summary>
    public class NullIdempotencyService : IIdempotencyService
    {
        private readonly ILogger<NullIdempotencyService> _logger;

        public NullIdempotencyService(ILogger<NullIdempotencyService> logger)
        {
            _logger = logger;
            _logger.LogWarning("Using NullIdempotencyService. Idempotency checks are disabled.");
        }

        /// <summary>
        /// Always returns null, indicating no cached response was found.
        /// </summary>
        /// <param name="idempotencyKey">The idempotency key (ignored).</param>
        /// <returns>A task resolving to null.</returns>
        public Task<CachedIdempotencyEntry?> GetCachedResponseAsync(string idempotencyKey)
        {
            // Log the attempt for debugging purposes if needed, but generally keep it quiet.
            // _logger.LogDebug("NullIdempotencyService: GetCachedResponseAsync called for key {IdempotencyKey}, returning null.", idempotencyKey);

            // Return null immediately, wrapped in a completed task.
            return Task.FromResult<CachedIdempotencyEntry?>(null);
        }

        /// <summary>
        /// Performs no operation. Does not cache the response.
        /// </summary>
        /// <param name="idempotencyKey">The idempotency key (ignored).</param>
        /// <param name="statusCode">The status code (ignored).</param>
        /// <param name="responseBody">The response body (ignored).</param>
        /// <param name="responseContentType">The response content type (ignored).</param>
        /// <param name="requestPayloadHash">The request payload hash (ignored).</param>
        /// <returns>A completed task.</returns>
        public Task CacheResponseAsync(string idempotencyKey, int statusCode, object? responseBody, string responseContentType, string requestPayloadHash)
        {
            // Log the attempt for debugging purposes if needed.
            // _logger.LogDebug("NullIdempotencyService: CacheResponseAsync called for key {IdempotencyKey}. No action taken.", idempotencyKey);

            // Return a completed task as no asynchronous work is done.
            return Task.CompletedTask;
        }
    }
}
