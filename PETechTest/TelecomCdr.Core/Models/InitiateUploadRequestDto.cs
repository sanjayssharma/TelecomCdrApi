using System.ComponentModel.DataAnnotations;

namespace TelecomCdr.Core.Models
{
    /// <summary>
    /// DTO for requesting a direct upload URL.
    /// </summary>
    public class InitiateUploadRequestDto
    {
        /// <summary>
        /// The original name of the file to be uploaded.
        /// </summary>
        [Required]
        [StringLength(255, MinimumLength = 1)]
        public string FileName { get; set; }

        /// <summary>
        /// The MIME content type of the file (e.g., "text/csv", "application/octet-stream").
        /// </summary>
        [Required]
        [StringLength(100, MinimumLength = 3)]
        public string ContentType { get; set; }

        /// <summary>
        /// Optional: The size of the file in bytes. Can be used for pre-allocation or validation.
        /// </summary>
        public long? FileSize { get; set; }
    }
}
