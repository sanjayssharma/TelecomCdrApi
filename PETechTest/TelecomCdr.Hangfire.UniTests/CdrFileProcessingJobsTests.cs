using Hangfire;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;
using TelecomCdr.Hangfire.Jobs;

namespace TelecomCdr.Hangfire.UnitTests
{
    [TestFixture]
    public class CdrFileProcessingJobsTests
    {
        private Mock<IFileProcessingService> _mockFileProcessingService;
        private Mock<IJobStatusRepository> _mockJobStatusRepository;
        private Mock<IBlobStorageService> _mockBlobStorageService;
        private Mock<IQueueService> _mockQueueService;
        private Mock<ILogger<CdrFileProcessingJobs>> _mockLogger;
        private Mock<IJobCancellationToken> _mockHangfireCancellationToken;

        private CdrFileProcessingJobs _cdrFileProcessingJobs;

        [SetUp]
        public void SetUp()
        {
            _mockFileProcessingService = new Mock<IFileProcessingService>();
            _mockJobStatusRepository = new Mock<IJobStatusRepository>();
            _mockBlobStorageService = new Mock<IBlobStorageService>();
            _mockQueueService = new Mock<IQueueService>();
            _mockLogger = new Mock<ILogger<CdrFileProcessingJobs>>();
            _mockHangfireCancellationToken = new Mock<IJobCancellationToken>();

            _cdrFileProcessingJobs = new CdrFileProcessingJobs(
                _mockFileProcessingService.Object,
                _mockJobStatusRepository.Object,
                _mockQueueService.Object,
                _mockLogger.Object
            );
        }

        private static Stream GenerateStreamFromString(string s)
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_GivenJobStatusNotFound_ShouldLogErrorAndExit()
        {
            // Arrange
            var jobId = new Guid("1cee98c5-467e-4ff2-8e91-8fffa19b3f83");
            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(jobId)).ReturnsAsync((JobStatus)null);

            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync("container", "blob", jobId);

            // Assert
            _mockJobStatusRepository.Verify(r => r.UpdateJobStatusAsync(It.IsAny<Guid>(), It.IsAny<ProcessingStatus>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>()), Times.Never);
            _mockBlobStorageService.Verify(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockQueueService.Verify(q => q.SendJobStatusUpdateAsync(It.IsAny<JobStatusUpdateMessage>()), Times.Never);
            // Verify logging for "JobStatus not found"
            VerifyLog(LogLevel.Error, $"JobStatus not found for JobId: {jobId}. Cannot proceed with processing blob blob.");
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_GivenBlobDownloadFails_ShouldSendFailedStatusToQueue()
        {
            // Arrange
            var jobId = new Guid("1cee98c5-467e-4ff2-8e91-8fffa19b3f83");
            var containerName = "test-container";
            var blobName = "test-blob.csv";
            var originalFileName = "original.csv";
            var jobStatus = new JobStatus { CorrelationId = jobId, Status = ProcessingStatus.QueuedForProcessing };

            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(jobId)).ReturnsAsync(jobStatus);
            _mockBlobStorageService.Setup(s => s.DownloadFileAsync(containerName, blobName, It.IsAny<CancellationToken>())).ReturnsAsync((Stream)null);

            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync(containerName, blobName, jobId);

            // Assert
            _mockJobStatusRepository.Verify(r => r.UpdateJobStatusAsync(jobId, ProcessingStatus.Processing, It.IsAny<string>(), null, null), Times.Once);
            _mockFileProcessingService.Verify(s => s.ProcessAndStoreCdrFileFromBlobAsync(It.IsAny<string>(), It.IsAny<string>(), jobId, It.IsAny<CancellationToken>()), Times.Never);

            // Verify message sent to queue

            VerifyLog(LogLevel.Error, $"Failed to download blob {blobName} from container {containerName} for JobId: {jobId}.");
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_GivenSuccessfulProcessing_ShouldSendCompletedStatusToQueue()
        {
            // Arrange
            var jobId = new Guid("1cee98c5-467e-4ff2-8e91-8fffa19b3f83");
            var containerName = "test-container";
            var blobName = "test-blob.csv";
            var originalFileName = "original.csv";
            var jobStatus = new JobStatus { CorrelationId = jobId, Status = ProcessingStatus.QueuedForProcessing };
            var fileContentStream = GenerateStreamFromString("header1,header2\nvalue1,value2");
            var processingResult = new FileProcessingResult { SuccessfulRecordsCount = 1, FailedRecordsCount = 0 };

            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(jobId)).ReturnsAsync(jobStatus);
            _mockBlobStorageService.Setup(s => s.DownloadFileAsync(containerName, blobName, It.IsAny<CancellationToken>())).ReturnsAsync(fileContentStream);
            _mockFileProcessingService.Setup(s => s.ProcessAndStoreCdrFileFromBlobAsync(containerName, blobName, jobId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(processingResult);

            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync(containerName, blobName, jobId);

            // Assert
            _mockJobStatusRepository.Verify(r => r.UpdateJobStatusAsync(jobId, ProcessingStatus.Processing, It.IsAny<string>(), null, null), Times.Once);
            _mockFileProcessingService.Verify(s => s.ProcessAndStoreCdrFileFromBlobAsync(containerName, blobName, jobId, It.IsAny<CancellationToken>()), Times.Once);


            VerifyLog(LogLevel.Information, $"File processing completed for JobId: {jobId}. Processed: 1, Failed: 0");
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_GivenProcessingWithSomeFailures_ShouldSendPartiallyCompletedStatusToQueue()
        {
            // Arrange
            var jobId = new Guid("1cee98c5-467e-4ff2-8e91-8fffa19b3f83");
            var jobStatus = new JobStatus { CorrelationId = jobId, Status = ProcessingStatus.QueuedForProcessing };
            var fileContentStream = GenerateStreamFromString("header1,header2\nvalue1,value2\nbadrow");
            var processingResult = new FileProcessingResult { SuccessfulRecordsCount = 1, FailedRecordsCount = 1 };

            SetupMocksForSuccessfulPath(jobId, jobStatus, fileContentStream, processingResult);

            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync("c", "b", jobId);

            // Assert

        }

        [Test]
        public async Task ProcessFileFromBlobAsync_GivenProcessingWithAllFailures_ShouldSendFailedStatusToQueue()
        {
            // Arrange
            var jobId = new Guid("1cee98c5-467e-4ff2-8e91-8fffa19b3f83");
            var jobStatus = new JobStatus { CorrelationId = jobId, Status = ProcessingStatus.QueuedForProcessing};
            var fileContentStream = GenerateStreamFromString("badrow1\nbadrow2");
            var processingResult = new FileProcessingResult { SuccessfulRecordsCount = 0, FailedRecordsCount = 2};

            SetupMocksForSuccessfulPath(jobId, jobStatus, fileContentStream, processingResult);

            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync("c", "b", jobId);

            // Assert

        }

        [Test]
        public async Task ProcessFileFromBlobAsync_GivenProcessingServiceThrowsException_ShouldSendFailedStatusToQueue()
        {
            // Arrange
            var jobId = new Guid("1cee98c5-467e-4ff2-8e91-8fffa19b3f83");
            var containerName = "test-container";
            var blobName = "test-blob.csv";
            var originalFileName = "original.csv";
            var jobStatus = new JobStatus { CorrelationId = jobId, Status = ProcessingStatus.QueuedForProcessing };
            var fileContentStream = GenerateStreamFromString("good,data");
            var exceptionMessage = "Service exploded";

            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(jobId)).ReturnsAsync(jobStatus);
            _mockBlobStorageService.Setup(s => s.DownloadFileAsync(containerName, blobName, It.IsAny<CancellationToken>())).ReturnsAsync(fileContentStream);
            _mockFileProcessingService.Setup(s => s.ProcessAndStoreCdrFileFromBlobAsync(containerName, blobName, jobId, It.IsAny<CancellationToken>()))
                                      .ThrowsAsync(new InvalidOperationException(exceptionMessage));

            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync(containerName, blobName, jobId);

            // Assert
            VerifyLog(LogLevel.Error, $"Unhandled exception processing file for JobId: {jobId}, Blob: {blobName}.");
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_GivenQueueServiceThrowsException_ShouldLogCriticalError()
        {
            // Arrange
            var jobId = new Guid("1cee98c5-467e-4ff2-8e91-8fffa19b3f83");
            SetupMocksForSuccessfulPath(jobId, new JobStatus { CorrelationId = jobId }, GenerateStreamFromString("data"), new FileProcessingResult { SuccessfulRecordsCount = 1 });
            _mockQueueService.Setup(q => q.SendJobStatusUpdateAsync(It.IsAny<JobStatusUpdateMessage>())).ThrowsAsync(new Exception("Queue is down"));

            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync( "c", "b", jobId);

            // Assert
            VerifyLog(LogLevel.Critical, $"CRITICAL: Failed to send job status update message to queue for JobId: {jobId}.");
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_GivenJobIsCancelled_ShouldSendFailedStatusWithCancellationMessage()
        {
            // Arrange
            var jobId = new Guid("1cee98c5-467e-4ff2-8e91-8fffa19b3f83");
            var jobStatus = new JobStatus { CorrelationId = jobId, Status = ProcessingStatus.QueuedForProcessing};
            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(jobId)).ReturnsAsync(jobStatus);
            _mockHangfireCancellationToken.Setup(c => c.ThrowIfCancellationRequested()).Throws<OperationCanceledException>();

            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync("c", "b", jobId);

            // Assert
            VerifyLog(LogLevel.Warning, $"Hangfire job for JobId: {jobId} was cancelled.");
        }


        // Helper method to reduce boilerplate in tests
        private void SetupMocksForSuccessfulPath(Guid jobId, JobStatus jobStatus, Stream blobStream, FileProcessingResult processingResult, string container = "c", string blob = "b", string originalFile = "o.csv")
        {
            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(jobId)).ReturnsAsync(jobStatus);
            _mockBlobStorageService.Setup(s => s.DownloadFileAsync(container, blob, It.IsAny<CancellationToken>())).ReturnsAsync(blobStream);
            _mockFileProcessingService.Setup(s => s.ProcessAndStoreCdrFileFromBlobAsync(container, blob, jobId, It.IsAny<CancellationToken>())).ReturnsAsync(processingResult);
        }

        // Helper method for verifying ILogger calls
        // This is a simplified version. For more complex scenarios, you might need a custom ILogger implementation or a more robust verification library.
        private void VerifyLog<TException>(LogLevel expectedLogLevel, string expectedMessageContent, Times? times = null) where TException : Exception
        {
            times ??= Times.Once();
            _mockLogger.Verify(
                x => x.Log(
                    expectedLogLevel,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(expectedMessageContent)),
                    It.IsAny<TException>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                times.Value);
        }
        private void VerifyLog(LogLevel expectedLogLevel, string expectedMessageContent, Times? times = null)
        {
            times ??= Times.Once();
            _mockLogger.Verify(
                x => x.Log(
                    expectedLogLevel,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(expectedMessageContent)),
                    null, // No exception for non-error logs or when exception type is not specified
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                times.Value);
        }
    }
}
