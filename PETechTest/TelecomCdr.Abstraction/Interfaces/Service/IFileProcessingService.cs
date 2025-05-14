using TelecomCdr.Abstraction.Models;

namespace TelecomCdr.Abstraction.Interfaces.Service
{
    public interface IFileProcessingService
    {
        // For direct uploads
        Task<FileProcessingResult> ProcessAndStoreCdrFileAsync(Stream fileStream, Guid uploadCorrelationId, CancellationToken cancellationToken = default);

        // For Hangfire jobs reading from blob storage
        Task<FileProcessingResult> ProcessAndStoreCdrFileFromBlobAsync(string containerName, string blobName, Guid originalUploadCorrelationId, CancellationToken cancellationToken = default);
    }
}
