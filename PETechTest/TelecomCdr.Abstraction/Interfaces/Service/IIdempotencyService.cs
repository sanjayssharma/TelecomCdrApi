using TelecomCdr.Abstraction.Models;

namespace TelecomCdr.Abstraction.Interfaces.Service
{
    /// <summary>
    /// Service for handling idempotency checks and caching responses.
    /// </summary>
    public interface IIdempotencyService
    {
        /// <summary>
        /// Retrieves a cached HTTP response based on an idempotency key.
        /// </summary>
        /// <param name="idempotencyKey">The unique key for the request.</param>
        /// <returns>The cached response if found, otherwise null.</returns>
        Task<CachedIdempotencyEntry?> GetCachedResponseAsync(string idempotencyKey);

        /// <summary>
        /// Caches an HTTP response against an idempotency key.
        /// </summary>
        /// <param name="idempotencyKey">The unique key for the request.</param>
        /// <param name="statusCode">The HTTP status code of the response.</param>
        /// <param name="responseBody">The response body object (will be serialized to JSON).</param>
        /// <param name="responseContentType">The content type of the response.</param>
        /// <param name="requestPayloadHash">The hash of the original request payload.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task CacheResponseAsync(string idempotencyKey, int statusCode, object? responseBody, string responseContentType, string requestPayloadHash);
    }
}
