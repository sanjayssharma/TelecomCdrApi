namespace TelecomCdr.DurableFunctions.Dtos
{
    public class OrchestratorInput { 
        public string BlobName { get; set; } 
        public string ContainerName { get; set; } 
        public Guid MasterCorrelationId { get; set; } 
    }
}
