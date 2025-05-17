namespace TelecomCdr.Domain.Helpers
{
    public static class JobStatusHelper
    {
        public static JobStatus WithStatus(this JobStatus jobStatus, ProcessingStatus status)
        {
            jobStatus.Status = status;
            return jobStatus;
        }

        public static JobStatus WithMessage(this JobStatus jobStatus, string message)
        {
            jobStatus.Message = message;
            return jobStatus;
        }

        public static JobStatus WithType(this JobStatus jobStatus, JobType type)
        {
            jobStatus.Type = type;
            return jobStatus;
        }

        public static JobStatus WithParentCorrelationId(this JobStatus jobStatus, Guid? parentCorrelationId)
        {
            jobStatus.ParentCorrelationId = parentCorrelationId;
            return jobStatus;
        }

        public static JobStatus WithTotalChunks(this JobStatus jobStatus, int? totalChunks)
        {
            jobStatus.TotalChunks = totalChunks;
            return jobStatus;
        }

        public static JobStatus WithProcessedChunks(this JobStatus jobStatus, int? processedChunks)
        {
            jobStatus.ProcessedChunks = processedChunks;
            return jobStatus;
        }

        public static JobStatus WithSuccessfulChunks(this JobStatus jobStatus, int? successfulChunks)
        {
            jobStatus.SuccessfulChunks = successfulChunks;
            return jobStatus;
        }

        public static JobStatus WithFailedChunks(this JobStatus jobStatus, int? failedChunks)
        {
            jobStatus.FailedChunks = failedChunks;
            return jobStatus;
        }

        public static JobStatus WithProcessedRecordsCount(this JobStatus jobStatus, long? processedRecordsCount)
        {
            jobStatus.ProcessedRecordsCount = processedRecordsCount;
            return jobStatus;
        }

        public static JobStatus WithFailedRecordsCount(this JobStatus jobStatus, long? failedRecordsCount)
        {
            jobStatus.FailedRecordsCount = failedRecordsCount;
            return jobStatus;
        }

        public static JobStatus WithOriginalFileName(this JobStatus jobStatus, string? originalFileName)
        {
            jobStatus.OriginalFileName = originalFileName;
            return jobStatus;
        }

        public static JobStatus WithContainerName(this JobStatus jobStatus, string? containerName)
        {
            jobStatus.ContainerName = containerName;
            return jobStatus;
        }

        public static JobStatus WithBlobName(this JobStatus jobStatus, string? blobName)
        {
            jobStatus.BlobName = blobName;
            return jobStatus;
        }

        public static JobStatus WithCreatedAtUtc(this JobStatus jobStatus, DateTime createdAtUtc)
        {
            jobStatus.CreatedAtUtc = createdAtUtc;
            return jobStatus;
        }

        public static JobStatus WithLastUpdatedAtUtc(this JobStatus jobStatus, DateTime lastUpdatedAtUtc)
        {
            jobStatus.LastUpdatedAtUtc = lastUpdatedAtUtc;
            return jobStatus;
        }
        public static JobStatus WithCorrelationId(this JobStatus jobStatus, Guid correlationId)
        {
            jobStatus.CorrelationId = correlationId;
            return jobStatus;
        }

        public static JobStatus WithCorrelationId(this JobStatus jobStatus, string correlationId)
        {
            if (Guid.TryParse(correlationId, out var guid))
            {
                jobStatus.CorrelationId = guid;
            }
            else
            {
                throw new ArgumentException("Invalid Guid format", nameof(correlationId));
            }
            return jobStatus;
        }

        public static JobStatus WithCorrelationId(this JobStatus jobStatus, Guid? correlationId)
        {
            if (correlationId.HasValue)
            {
                jobStatus.CorrelationId = correlationId.Value;
            }
            else
            {
                throw new ArgumentNullException(nameof(correlationId), "CorrelationId cannot be null");
            }
            return jobStatus;
        }

        public static bool IsMasterJob(this JobStatus jobStatus)
        {
            return jobStatus.Type == JobType.Master;
        }

        public static bool IsChunkJob(this JobStatus jobStatus)
        {
            return jobStatus.Type == JobType.Chunk;
        }
    }
}
