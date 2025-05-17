namespace TelecomCdr.Domain.Helpers
{
    public class JobStatusBuilder
    {
        private JobStatus _jobStatus;
        public JobStatusBuilder()
        {
            _jobStatus = new JobStatus
            {
                CorrelationId = Guid.NewGuid(),
                CreatedAtUtc = DateTime.UtcNow,
                LastUpdatedAtUtc = DateTime.UtcNow,
                Status = ProcessingStatus.Accepted,
                Type = JobType.SingleFile
            };
        }

        public JobStatusBuilder WithCorrelationId(Guid correlationId)
        {
            _jobStatus.CorrelationId = correlationId;
            return this;
        }

        public JobStatusBuilder WithStatus(ProcessingStatus status)
        {
            _jobStatus.Status = status;
            return this;
        }

        public JobStatusBuilder WithType(JobType type)
        {
            _jobStatus.Type = type;
            return this;
        }

        public JobStatusBuilder WithMessage(string message)
        {
            _jobStatus.Message = message;
            return this;
        }

        public JobStatusBuilder WithTotalChunks(int totalChunks)
        {
            _jobStatus.TotalChunks = totalChunks;
            return this;
        }

        public JobStatusBuilder WithProcessedChunks(int processedChunks)
        {
            _jobStatus.ProcessedChunks = processedChunks;
            return this;
        }

        public JobStatusBuilder WithSuccessfulChunks(int successfulChunks)
        {
            _jobStatus.SuccessfulChunks = successfulChunks;
            return this;
        }

        public JobStatusBuilder WithFailedChunks(int failedChunks)
        {
            _jobStatus.FailedChunks = failedChunks;
            return this;
        }

        public JobStatusBuilder WithProcessedRecordsCount(long processedRecordsCount)
        {
            _jobStatus.ProcessedRecordsCount = processedRecordsCount;
            return this;
        }

        public JobStatusBuilder WithFailedRecordsCount(long failedRecordsCount)
        {
            _jobStatus.FailedRecordsCount = failedRecordsCount;
            return this;
        }

        public JobStatusBuilder WithOriginalFileName(string originalFileName)
        {
            _jobStatus.OriginalFileName = originalFileName;
            return this;
        }

        public JobStatusBuilder WithContainerName(string containerName)
        {
            _jobStatus.ContainerName = containerName;
            return this;
        }

        public JobStatusBuilder WithBlobName(string blobName)
        {
            _jobStatus.BlobName = blobName;
            return this;
        }

        public JobStatusBuilder WithParentCorrelationId(Guid? parentCorrelationId)
        {
            _jobStatus.ParentCorrelationId = parentCorrelationId;
            return this;
        }

        public JobStatusBuilder WithLastUpdatedAtUtc(DateTime lastUpdatedAtUtc)
        {
            _jobStatus.LastUpdatedAtUtc = lastUpdatedAtUtc;
            return this;
        }

        public JobStatus Build()
        {
            return _jobStatus;
        }

        public JobStatusBuilder Reset()
        {
            _jobStatus = new JobStatus
            {
                CorrelationId = Guid.NewGuid(),
                CreatedAtUtc = DateTime.UtcNow,
                LastUpdatedAtUtc = DateTime.UtcNow,
                Status = ProcessingStatus.Accepted,
                Type = JobType.SingleFile
            };
            return this;
        }

        public JobStatusBuilder WithJobStatus(JobStatus jobStatus)
        {
            _jobStatus = jobStatus;
            return this;
        }

        public JobStatusBuilder WithJobStatus(JobStatus jobStatus, bool reset)
        {
            if (reset)
            {
                _jobStatus = new JobStatus
                {
                    CorrelationId = Guid.NewGuid(),
                    CreatedAtUtc = DateTime.UtcNow,
                    LastUpdatedAtUtc = DateTime.UtcNow,
                    Status = ProcessingStatus.Accepted,
                    Type = JobType.SingleFile
                };
            }
            else
            {
                _jobStatus = jobStatus;
            }
            return this;
        }
    }
}
