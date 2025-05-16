using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

public class BlobTriggerStarterFunction
{
    private readonly ILogger<BlobTriggerStarterFunction> _logger;

    public BlobTriggerStarterFunction(ILogger<BlobTriggerStarterFunction> logger)
    {
        _logger = logger;
    }

    [Function(nameof(BlobTriggerStarterFunction))]
    public async Task Run(
        [BlobTrigger("raw-uploads/{blobName}", Connection = "AZURE_STORAGE_CONNECTION_STRING")] byte[] myBlob, // content can be byte[] or Stream
        string blobName,
        [DurableClient] DurableTaskClient durableTaskClient, // Inject DurableTaskClient
        FunctionContext functionContext)
    {
        _logger.LogInformation("Blob trigger function processed blob: {BlobName}, Size: {Size} bytes.", blobName, myBlob?.Length ?? 0);

        // Extract masterCorrelationId from blob metadata
        string masterCorrelationId = "UNKNOWN";
        if (functionContext.BindingContext.BindingData.TryGetValue("metadata", out var metadataObj) &&
            metadataObj is IReadOnlyDictionary<string, string> metadataDict)
        {
            metadataDict.TryGetValue("UploadCorrelationId", out masterCorrelationId);
        }

        if (string.IsNullOrEmpty(masterCorrelationId) || masterCorrelationId == "UNKNOWN")
        {
            _logger.LogError("UploadCorrelationId not found in metadata for blob {BlobName}. Cannot start orchestrator.", blobName);
            return; // Or handle error appropriately
        }

        // Input for the orchestrator
        var orchestratorInput = new { BlobName = blobName, ContainerName = "raw-uploads", MasterCorrelationId = masterCorrelationId };

        // Start the orchestrator
        string instanceId = await durableTaskClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(CsvProcessingOrchestratorFunction), // Name of the orchestrator function
            orchestratorInput);

        _logger.LogInformation("Started orchestration with ID = '{InstanceId}' for blob {BlobName} and MasterCorrelationId {MasterCorrelationId}.",
            instanceId, blobName, masterCorrelationId);
    }
}