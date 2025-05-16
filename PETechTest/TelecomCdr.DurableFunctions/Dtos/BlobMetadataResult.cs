namespace TelecomCdr.DurableFunctions.Dtos
{
    public class BlobMetadataResult 
    { 
        public long Size { get; set; } 
        public Dictionary<string, string> Metadata { get; set; } 
    }
}
