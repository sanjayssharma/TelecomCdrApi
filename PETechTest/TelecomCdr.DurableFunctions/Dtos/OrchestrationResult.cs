using TelecomCdr.Domain;

namespace TelecomCdr.DurableFunctions.Dtos
{
    public class OrchestrationResult 
    { 
        public Guid MasterCorrelationId { get; set; } 
        public ProcessingStatus OverallStatus { get; set; } 
        public string FinalMessage { get; set; } 
        public long OriginalBlobSize { get; set; } 
        public long TotalSuccessfulRecords { get; set; } 
        public long TotalFailedRecords { get; set; } 
    }
}
