namespace TelecomCdr.DurableFunctions.Dtos
{
    public class ChunkInfo 
    { 
        public string ChunkBlobName { get; set; } 
        public string ChunkContainerName { get; set; } 
        public Guid ChunkCorrelationId { get; set; } 
        public int ChunkNumber { get; set; } 
    }
}
