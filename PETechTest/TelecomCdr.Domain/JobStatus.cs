using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TelecomCdr.Domain
{
    public enum ProcessingStatus
    {
        Accepted,           // Initial state after API receives request
        PendingQueue,       // Blob uploaded, waiting for Azure Function to queue
        Chunking,           // Large file is being split into chunks
        ChunksQueued,       // All chunks for a large file have been enqueued
        QueuedForProcessing,// Job (or chunk) enqueued in Hangfire
        Processing,         // Hangfire job (or chunk) picked up and started
        Succeeded,          // Processing completed successfully (for whole file or all chunks)
        PartiallySucceeded, // Processing completed with some errors (for whole file or if some chunks failed)
        Failed              // Processing failed (for whole file or if critical chunk error)
    }

    public enum JobType // To distinguish master jobs from chunks
    {
        SingleFile, // For files not large enough to be chunked
        Master,
        Chunk,
    }

    [Table("job_statuses")]
    public class JobStatus
    {
        [Key]
        public Guid CorrelationId { get; set; } // Unique ID for this job/chunk status

        [Required]
        [Column("ProcessingStatus")]
        public ProcessingStatus Status { get; set; }

        [Required]
        public JobType Type { get; set; } = JobType.SingleFile; // Default to single file

        // For chunk jobs, this links back to the master job's CorrelationId.
        // For master jobs or single file jobs, this can be null.
        [MaxLength(100)]
        public Guid? ParentCorrelationId { get; set; }

        public string? Message { get; set; }

        public int? TotalChunks { get; set; }        // For master jobs, the total number of chunks expected.
        public int? ProcessedChunks { get; set; }    // For master jobs, the number of chunks that have completed (succeeded or failed).
        public int? SuccessfulChunks { get; set; }   // For master jobs, count of chunks that succeeded.
        public int? FailedChunks { get; set; }       // For master jobs, count of chunks that failed.

        public long? ProcessedRecordsCount { get; set; } // For single files or individual chunks
        public long? FailedRecordsCount { get; set; }    // For single files or individual chunks

        [Required]
        public DateTime CreatedAtUtc { get; set; }

        [Required]
        public DateTime LastUpdatedAtUtc { get; set; }

        public string? OriginalFileName { get; set; }
        public string? BlobName { get; set; } // For single files or individual chunks
        public string? ContainerName { get; set; }

        public void SetParentCorrelationId(Guid? parentCorrelationId)
        {
            ParentCorrelationId = parentCorrelationId;
        }
    }
}
