using TelecomCdr.Abstraction.Models;

namespace TelecomCdr.Abstraction.Interfaces.Service
{
    /// <summary>
    /// Interface for sending messages to a queue.
    /// </summary>
    public interface IQueueService
    {
        /// <summary>
        /// Sends a job status update message to the configured queue.
        /// </summary>
        /// <param name="message">The message to send.</param>
        Task SendJobStatusUpdateAsync(JobStatusUpdateMessage message);
    }
}
