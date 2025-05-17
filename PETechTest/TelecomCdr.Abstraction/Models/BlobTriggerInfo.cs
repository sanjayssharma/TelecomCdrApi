namespace TelecomCdr.Abstraction.Models
{
    public class BlobTriggerInfo
    {
        public string BlobName { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string ContainerName { get; set; } = string.Empty;
        public long BlobSize { get; set; }
        public IReadOnlyDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public Guid UploadCorrelationId { get; set; } = Guid.Empty; // The Correlation ID from the initial API upload
        public Guid? ParentCorrelationId { get; set; } = null; // The Correlation ID of the parent job, if applicable
    }
}
