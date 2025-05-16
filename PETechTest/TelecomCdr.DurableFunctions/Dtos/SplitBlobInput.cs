namespace TelecomCdr.DurableFunctions.Dtos
{
    public class SplitBlobInput 
    { 
        public string OriginalContainer { get; set; } 
        public string OriginalBlobName { get; set; } 
        public Guid MasterCorrelationId { get; set; } 
    }
}
