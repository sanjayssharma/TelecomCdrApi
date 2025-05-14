namespace TelecomCdr.Abstraction.Interfaces.Service
{
    public interface IBlobStorageService
    {
        Task<Uri> UploadFileAsync(string containerName, string blobName, Stream content, string contentType, IDictionary<string, string>? metadata, CancellationToken cancellationToken = default);
        Task<Stream> DownloadFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default);
        Task DeleteFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default); // Optional: for cleanup
    }
}
