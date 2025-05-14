using System.Text.Json.Serialization;

namespace TelecomCdr.Core.Models
{
    /// <summary>
    /// Represents a standardized error response from the API.
    /// </summary>
    public class ErrorResponseDto
    {
        /// <summary>
        /// The HTTP status code.
        /// </summary>
        public int StatusCode { get; set; }

        /// <summary>
        /// A human-readable message explaining the error.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Additional details about the error, such as a stack trace.
        /// This should typically only be included in development environments.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Details { get; set; }

        public ErrorResponseDto(int statusCode, string message, string? details = null)
        {
            StatusCode = statusCode;
            Message = message;
            Details = details;
        }
    }
}
