using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace TelecomCdr.Domain
{
    public class FailedCdrRecord
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Auto-incrementing ID
        public long Id { get; set; }

        [Required]
        [MaxLength(100)] // Match the length of UploadCorrelationId in CallDetailRecord/JobStatus
        public string UploadCorrelationId { get; set; } = string.Empty;

        public int? RowNumberInCsv { get; set; } // Optional: if you can track the row number

        [MaxLength(1024)] // Store the problematic row or relevant parts
        public string? RawRowData { get; set; }

        [Required]
        [MaxLength(2000)] // Store the error message
        public string ErrorMessage { get; set; } = string.Empty;

        [Required]
        public DateTime FailedAtUtc { get; set; }
    }
}
