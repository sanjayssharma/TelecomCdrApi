using TelecomCdr.Abstraction.Models;

namespace TelecomCdr.Abstraction.Interfaces.Service
{
    public interface IBlobProcessingOrchestrator
    {
        /// <summary>
        /// Orchestrates the processing of an uploaded blob.
        /// This includes determining if chunking is needed, performing chunking,
        /// updating job statuses, and enqueuing Hangfire jobs.
        /// </summary>
        /// <param name="triggerInfo">Information about the blob that triggered the process.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task OrchestrateBlobProcessingAsync(BlobTriggerInfo triggerInfo, CancellationToken cancellationToken = default);
    }
}
