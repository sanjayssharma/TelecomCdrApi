using TelecomCdr.Domain;

namespace TelecomCdr.Abstraction.Models
{
    public class JobStatusUpdateMessage
    {
        /// <summary>
        /// The Correlation ID of the specific job or chunk whose status is being updated.
        /// </summary>
        public Guid CorrelationId { get; set; } = Guid.Empty;

        /// <summary>
        /// The result of the file/chunk processing.
        /// </summary>
        public FileProcessingResult ProcessingResult { get; set; } = new FileProcessingResult();

        /// <summary>
        /// If this update is for a chunk, this is the Correlation ID of the parent/master job.
        /// Null if this is for a single file or a master job itself.
        /// </summary>
        public Guid? ParentCorrelationId { get; set; }

        /// <summary>
        /// The type of job this status update pertains to.
        /// </summary>
        public JobType JobType { get; set; }

        /// <summary>
        /// The final status determined by the Hangfire job before sending to queue.
        /// </summary>
        public ProcessingStatus DeterminedStatus { get; set; }

        /// <summary>
        /// The detailed message determined by the Hangfire job.
        /// </summary>
        public string DeterminedMessage { get; set; } = string.Empty;
    }
}
