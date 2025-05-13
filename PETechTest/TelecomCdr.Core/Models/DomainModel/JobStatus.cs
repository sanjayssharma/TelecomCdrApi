using System.ComponentModel.DataAnnotations;

namespace TelecomCdr.Core.Models.DomainModel
{
    public enum ProcessingStatus
    {
        Accepted,           // Initial state after API receives request
        PendingQueue,       // Blob uploaded, waiting for Azure Function to queue (optional intermediate state)
        QueuedForProcessing,// Azure Function successfully queued in Hangfire
        Processing,         // Hangfire job picked up and started
        Succeeded,          // Processing completed successfully
        PartiallySucceeded, // Processing completed with some errors
        Failed              // Processing failed
    }

    public class JobStatus
    {
        [Key] // Primary Key
        public Guid CorrelationId { get; set; }

        [Required]
        public ProcessingStatus Status { get; set; }

        public string? Message { get; set; } // For error details or success information

        public int? ProcessedRecordsCount { get; set; }

        public int? FailedRecordsCount { get; set; }

        [Required]
        public DateTime CreatedAtUtc { get; set; }

        [Required]
        public DateTime LastUpdatedAtUtc { get; set; }

        // Optional: Store the original file name for reference
        public string? OriginalFileName { get; set; }

        // Optional: Store the blob name for direct reference if needed
        public string? BlobName { get; set; }

        public string? ContainerName { get; set; }
    }
}
