using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;

namespace TelecomCdr.Infrastructure.Services
{
    public class AzureStorageQueueService : IQueueService
    {
        private readonly QueueClient _queueClient;
        private readonly ILogger<AzureStorageQueueService> _logger;
        private readonly string _queueName;

        public AzureStorageQueueService(IConfiguration configuration, ILogger<AzureStorageQueueService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            string? connectionString = configuration["AzureQueueStorage:ConnectionString"];
            _queueName = configuration["AzureQueueStorage:JobStatusUpdateQueueName"];

            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogError("Azure Queue Storage connection string ('AzureQueueStorage:ConnectionString') is not configured.");
                throw new InvalidOperationException("Azure Queue Storage connection string is missing.");
            }
            if (string.IsNullOrEmpty(_queueName))
            {
                _logger.LogError("Azure Queue Storage queue name ('AzureQueueStorage:JobStatusUpdateQueueName') is not configured.");
                throw new InvalidOperationException("Azure Queue Storage queue name for job status updates is missing.");
            }

            try
            {
                // Get a reference to a QueueClient
                _queueClient = new QueueClient(connectionString, _queueName);
                // Create the queue if it does not exist
                _queueClient.CreateIfNotExists(); // Idempotent
                _logger.LogInformation("Azure Storage Queue client initialized for queue '{QueueName}'.", _queueName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Azure Storage Queue client for queue '{QueueName}'. Check connection string and permissions.", _queueName);
                throw;
            }
        }

        public async Task SendJobStatusUpdateAsync(JobStatusUpdateMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            try
            {
                string serializedMessage = JsonSerializer.Serialize(message);
                await _queueClient.SendMessageAsync(serializedMessage);

                _logger.LogInformation("Successfully sent job status update message to queue '{QueueName}' for CorrelationId '{CorrelationId}'.", _queueName, message.CorrelationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send job status update message to queue '{QueueName}' for CorrelationId '{CorrelationId}'.", _queueName, message.CorrelationId);
                // Depending on requirements, we might implement a retry policy here or a dead-letter mechanism for this send operation.
                throw; // Rethrow to indicate failure to the caller (e.g., the Hangfire job)
            }
        }
    }
}
