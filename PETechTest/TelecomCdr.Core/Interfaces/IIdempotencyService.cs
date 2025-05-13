namespace TelecomCdr.Core.Interfaces
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
        Task<CachedHttpResponse?> GetCachedResponseAsync(string idempotencyKey);

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

    public class CachedHttpResponse
    {
        public int StatusCode { get; set; }
        public string? BodyJson { get; set; } // Stores the original *response* body as JSON string
        public string ContentType { get; set; } = "application/json"; // Content type of the original *response*
        public string RequestPayloadHash { get; set; } = string.Empty; // Hash of the original *request* payload
    }
}
