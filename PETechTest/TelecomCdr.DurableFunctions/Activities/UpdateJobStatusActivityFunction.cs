using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.DurableFunctions.Dtos;

namespace TelecomCdr.DurableFunctions.Activities
{
    public class UpdateJobStatusActivityFunction
    {
        [Function(nameof(UpdateJobStatusActivityFunction))]
        public async Task Run([ActivityTrigger] JobStatusActivityInput statusInput, ILogger<UpdateJobStatusActivityFunction> logger, IJobStatusRepository jobStatusRepository)
        {
            if (statusInput == null)
            {
                logger.LogError("JobStatusActivityInput is null. Cannot update job status.");
                return;
            }

            logger.LogInformation("Updating jobstatus for job with CorrelationId {CorrelationId} to {JobStatus}.", statusInput.CorrelationId, statusInput.Status);

            // Update the job status in the repository
            var jobStatus = await jobStatusRepository.GetJobStatusByCorrelationIdAsync(statusInput.CorrelationId);

            if (jobStatus == null)
            {
                logger.LogError("Job status not found for CorrelationId {CorrelationId}. Cannot update status.", statusInput.CorrelationId);
                return;
            }

            jobStatus.Status = statusInput.Status;
            jobStatus.Message = statusInput.Message;
            jobStatus.Type = statusInput.Type;
            jobStatus.LastUpdatedAtUtc = DateTime.UtcNow;

            // Chunk-specific properties
            jobStatus.TotalChunks = statusInput.TotalChunks;
            jobStatus.ProcessedChunks = statusInput.ProcessedChunks;
            jobStatus.SuccessfulChunks = statusInput.SuccessfulChunks;
            jobStatus.FailedChunks = statusInput.FailedChunks;

            jobStatus.ProcessedRecordsCount = statusInput.ProcessedRecordsCount;
            jobStatus.FailedRecordsCount = statusInput.FailedRecordsCount;

            if (string.IsNullOrEmpty(jobStatus.OriginalFileName) && !string.IsNullOrEmpty(statusInput.OriginalFileName))
            { 
                jobStatus.OriginalFileName = statusInput.OriginalFileName;
            }

            if (string.IsNullOrEmpty(jobStatus.ContainerName) && !string.IsNullOrEmpty(statusInput.ContainerName))
            {
                jobStatus.ContainerName = statusInput.ContainerName;
            }

            if (string.IsNullOrEmpty(jobStatus.BlobName) && !string.IsNullOrEmpty(statusInput.BlobName))
            {
                jobStatus.BlobName = statusInput.BlobName;
            }

            await jobStatusRepository.UpdateJobStatusAsync(jobStatus);
         
            logger.LogInformation("Job status updated successfully for CorrelationId {CorrelationId}.", statusInput.CorrelationId);
        }
    }
}
