using Hangfire;
using MediatR;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelecomCdr.Core.Interfaces;
using TelecomCdr.Core.Models.DomainModel;
using Microsoft.AspNetCore.Http;

namespace TelecomCdr.Core.Features.CdrProcessing.Commands
{
    public class EnqueueCdrFileProcessingCommandHandler : IRequestHandler<EnqueueCdrFileProcessingCommand, string>
    {
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly IBlobStorageService _blobStorageService;
        private readonly IJobStatusRepository _jobStatusRepository;
        private readonly IConfiguration _configuration;
        private readonly ILogger<EnqueueCdrFileProcessingCommandHandler> _logger;

        public EnqueueCdrFileProcessingCommandHandler(
            IBackgroundJobClient backgroundJobClient,
            IBlobStorageService blobStorageService,
            IConfiguration configuration,
            IJobStatusRepository jobStatusRepository,
            ILogger<EnqueueCdrFileProcessingCommandHandler> logger)
        {
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
            _blobStorageService = blobStorageService ?? throw new ArgumentNullException(nameof(blobStorageService));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _jobStatusRepository = jobStatusRepository ?? throw new ArgumentNullException(nameof(jobStatusRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<string> Handle(EnqueueCdrFileProcessingCommand request, CancellationToken cancellationToken)
        {
            var containerName = _configuration.GetValue<string>("AzureBlobStorage:ContainerName");
            if (string.IsNullOrEmpty(containerName))
            {
                _logger.LogError("Azure Blob Storage container name is not configured.");
                throw new InvalidOperationException("Azure Blob Storage container name is not configured.");
            }

            var blobName = $"{Guid.NewGuid()}-{Path.GetFileName(request.File.FileName)}";
            var initialJobStatus = new JobStatus
            {
                CorrelationId = request.CorrelationId,
                Status = ProcessingStatus.Accepted,
                Message = "File upload accepted by API, pending blob storage and queueing.",
                OriginalFileName = request.File.FileName,
                CreatedAtUtc = DateTime.UtcNow,
                LastUpdatedAtUtc = DateTime.UtcNow,
                ContainerName = containerName,
                BlobName = blobName
            };

            try
            {
                await _jobStatusRepository.CreateAsync(initialJobStatus);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create initial job status for Correlation ID '{CorrelationId}'. Aborting upload.", request.CorrelationId);
                throw;
            }

            _logger.LogInformation("Uploading file {FileName} to Azure Blob Storage as {BlobName} in container {ContainerName}. CorrelationId: {CorrelationId}",
                request.File.FileName, blobName, containerName, request.CorrelationId);

            Uri blobUri;
            try
            {
                var metadata = new Dictionary<string, string> { { "UploadCorrelationId", request.CorrelationId.ToString() } };

                using var fileStream = request.File.OpenReadStream();
                blobUri = await _blobStorageService.UploadFileAsync(containerName, blobName, fileStream, request.File.ContentType, metadata, cancellationToken);
                _logger.LogInformation("File {FileName} uploaded to {BlobUri}. CorrelationId: {CorrelationId}", request.File.FileName, blobUri, request.CorrelationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload file {FileName} to Azure Blob Storage. CorrelationId: {CorrelationId}", request.File.FileName, request.CorrelationId);
                throw; // Re-throw to be caught by error handling middleware
            }

            _logger.LogInformation("Enqueueing CDR file from blob {BlobName} for background processing. Original CorrelationId: {CorrelationId}",
                blobName, request.CorrelationId);

            // Pass the original request's CorrelationId to be stored with the CDRs
            var jobId = _backgroundJobClient.Enqueue<IFileProcessingService>(
                service => service.ProcessAndStoreCdrFileFromBlobAsync(containerName, blobName, request.CorrelationId, CancellationToken.None));

            _logger.LogInformation("CDR file from blob {BlobName} enqueued with JobId {JobId}. Original CorrelationId: {CorrelationId}",
                blobName, jobId, request.CorrelationId);

            return jobId;
        }
    }
}
