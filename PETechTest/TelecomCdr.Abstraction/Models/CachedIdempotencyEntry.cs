namespace TelecomCdr.Abstraction.Models
{
    public class CachedIdempotencyEntry
    {
        /// <summary>
        /// Status code of the response
        /// </summary>
        public int StatusCode { get; set; }

        /// <summary>
        /// // Stores the original *response* body as JSON string
        /// </summary>
        public string? BodyJson { get; set; }

        /// <summary>
        /// Content type of the original *response*
        /// </summary>
        public string ContentType { get; set; } = "application/json";

        /// <summary>
        /// Hash of the original *request* payload
        /// </summary>
        public string RequestPayloadHash { get; set; } = string.Empty;
    }
}
