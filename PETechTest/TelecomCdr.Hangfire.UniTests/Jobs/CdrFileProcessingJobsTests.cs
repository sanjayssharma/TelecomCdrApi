using Microsoft.Extensions.Logging;
using Moq;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;
using TelecomCdr.Hangfire.Jobs;

namespace TelecomCdr.Hangfire.UnitTests.Jobs
{
    [TestFixture]
    public class CdrFileProcessingJobsTests
    {
        private Mock<IFileProcessingService> _mockFileProcessingService;
        private Mock<IJobStatusRepository> _mockJobStatusRepository;
        private Mock<IQueueService> _mockQueueService;
        private Mock<ILogger<CdrFileProcessingJobs>> _mockLogger;
        private CdrFileProcessingJobs _cdrFileProcessingJobs;

        private Guid _testJobCorrelationId;
        private string _testContainerName;
        private string _testBlobName;

        [SetUp]
        public void SetUp()
        {
            _mockFileProcessingService = new Mock<IFileProcessingService>();
            _mockJobStatusRepository = new Mock<IJobStatusRepository>();
            _mockQueueService = new Mock<IQueueService>();
            _mockLogger = new Mock<ILogger<CdrFileProcessingJobs>>();

            _cdrFileProcessingJobs = new CdrFileProcessingJobs(
                _mockFileProcessingService.Object,
                _mockJobStatusRepository.Object,
                _mockQueueService.Object,
                _mockLogger.Object
            );

            _testJobCorrelationId = Guid.NewGuid();
            _testContainerName = "test-container";
            _testBlobName = "test-blob.csv";
        }

        private JobStatus CreateTestJobStatus(Guid correlationId, ProcessingStatus status = ProcessingStatus.QueuedForProcessing, JobType type = JobType.SingleFile, Guid? parentId = null)
        {
            var js = new JobStatus{

                CorrelationId = correlationId, 
                BlobName = "test.csv", 
                ContainerName = "test-container", 
                Status = status, 
                Type = type, 
                Message = "Initial message" 
            };

            if (parentId.HasValue)
            {
                js.SetParentCorrelationId(parentId);
            }

            return js;
        }

        [Test]
        public void ProcessFileFromBlobAsync_NullBlobName_ThrowsArgumentNullException()
        {
            // Assert
            var ex = Assert.ThrowsAsync<ArgumentNullException>(() =>
                _cdrFileProcessingJobs.ProcessFileFromBlobAsync(_testContainerName, null, _testJobCorrelationId));
            Assert.That(ex.ParamName, Is.EqualTo("blobName"));
            VerifyLog(LogLevel.Error, "Blob name cannot be null or whitespace");
        }

        [Test]
        public void ProcessFileFromBlobAsync_EmptyBlobName_ThrowsArgumentNullException()
        {
            // Assert
            var ex = Assert.ThrowsAsync<ArgumentNullException>(() =>
                _cdrFileProcessingJobs.ProcessFileFromBlobAsync(_testContainerName, " ", _testJobCorrelationId));
            Assert.That(ex.ParamName, Is.EqualTo("blobName"));
            VerifyLog(LogLevel.Error, "Blob name cannot be null or whitespace");
        }

        [Test]
        public void ProcessFileFromBlobAsync_NullContainerName_ThrowsArgumentNullException()
        {
            // Assert
            var ex = Assert.ThrowsAsync<ArgumentNullException>(() =>
                _cdrFileProcessingJobs.ProcessFileFromBlobAsync(null, _testBlobName, _testJobCorrelationId));
            Assert.That(ex.ParamName, Is.EqualTo("containerName"));
            VerifyLog(LogLevel.Error, $"Container name cannot be null or whitespace for Hangfire job processing blob {_testBlobName}");
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_JobStatusNotFound_LogsErrorAndReturns()
        {
            // Arrange
            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(_testJobCorrelationId))
                                      .ReturnsAsync((JobStatus)null);

            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId);

            // Assert
            VerifyLog(LogLevel.Error, $"JobStatus not found for JobCorrelationId: {_testJobCorrelationId}. Cannot process blob {_testBlobName}.");
            _mockJobStatusRepository.Verify(r => r.UpdateJobStatusAsync(It.IsAny<JobStatus>()), Times.Never);
            _mockFileProcessingService.Verify(s => s.ProcessAndStoreCdrFileFromBlobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_InitialStatusUpdateFails_ContinuesProcessingAndLogsError()
        {
            // Arrange
            var initialJobStatus = CreateTestJobStatus(_testJobCorrelationId);
            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(_testJobCorrelationId))
                                      .ReturnsAsync(initialJobStatus);
            _mockJobStatusRepository.Setup(r => r.UpdateJobStatusAsync(It.Is<JobStatus>(js => js.CorrelationId == _testJobCorrelationId && js.Status == ProcessingStatus.Processing)))
                                      .ThrowsAsync(new Exception("DB connection failed during initial update"));

            _mockFileProcessingService.Setup(s => s.ProcessAndStoreCdrFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId, It.IsAny<CancellationToken>()))
                                      .ReturnsAsync(new FileProcessingResult { SuccessfulRecordsCount = 10 }); // Assume processing would succeed

            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId);

            // Assert
            VerifyLog(LogLevel.Error, $"Failed to update job status to 'Processing' for Correlation ID '{_testJobCorrelationId}'. Processing will continue but status might be stale.");
            _mockFileProcessingService.Verify(s => s.ProcessAndStoreCdrFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId, It.IsAny<CancellationToken>()), Times.Once);
            // Verify final status update attempts in finally block
            _mockJobStatusRepository.Verify(r => r.UpdateJobStatusAsync(It.Is<JobStatus>(js => js.CorrelationId == _testJobCorrelationId && js.Status == ProcessingStatus.Succeeded)), Times.Exactly(2));
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_SuccessfulProcessing_UpdatesStatusAndIncrementsChunk()
        {
            // Arrange
            var parentJobId = Guid.NewGuid();
            var initialJobStatus = CreateTestJobStatus(_testJobCorrelationId, ProcessingStatus.QueuedForProcessing, JobType.Chunk, parentJobId);
            var processingResult = new FileProcessingResult { SuccessfulRecordsCount = 100, FailedRecordsCount = 0 };

            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(_testJobCorrelationId)).ReturnsAsync(initialJobStatus);
            _mockJobStatusRepository.Setup(r => r.UpdateJobStatusAsync(It.IsAny<JobStatus>())).Returns(Task.CompletedTask); // For all updates
            _mockFileProcessingService.Setup(s => s.ProcessAndStoreCdrFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId, It.IsAny<CancellationToken>()))
                                      .ReturnsAsync(processingResult);
            _mockJobStatusRepository.Setup(r => r.IncrementProcessedChunkCountAsync(parentJobId, true)).Returns(Task.CompletedTask);


            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId);

            // Assert
            // Final update to Succeeded
            _mockJobStatusRepository.Verify(r => r.UpdateJobStatusAsync(It.Is<JobStatus>(js =>
                js.CorrelationId == _testJobCorrelationId &&
                js.Status == ProcessingStatus.Succeeded &&
                js.ProcessedRecordsCount == 100 &&
                js.FailedRecordsCount == 0
            )), Times.Exactly(2));
            _mockJobStatusRepository.Verify(r => r.IncrementProcessedChunkCountAsync(parentJobId, true), Times.Once);
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_ProcessingServiceReturnsCriticalError_UpdatesStatusToFailed()
        {
            // Arrange
            var initialJobStatus = CreateTestJobStatus(_testJobCorrelationId);
            var processingResult = new FileProcessingResult { SuccessfulRecordsCount = 0, FailedRecordsCount = -1 }; // Critical error
            processingResult.ErrorMessages.Add("File not found by service");

            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(_testJobCorrelationId)).ReturnsAsync(initialJobStatus);
            _mockJobStatusRepository.Setup(r => r.UpdateJobStatusAsync(It.IsAny<JobStatus>())).Returns(Task.CompletedTask);
            _mockFileProcessingService.Setup(s => s.ProcessAndStoreCdrFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId, It.IsAny<CancellationToken>()))
                                      .ReturnsAsync(processingResult);
            _mockJobStatusRepository.Setup(r => r.IncrementProcessedChunkCountAsync(initialJobStatus.ParentCorrelationId, false)).Returns(Task.CompletedTask);


            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId);

            // Assert
            _mockJobStatusRepository.Verify(r => r.UpdateJobStatusAsync(It.Is<JobStatus>(js =>
                js.CorrelationId == _testJobCorrelationId &&
                js.Status == ProcessingStatus.Failed && // Because DetermineStatus() on result with -1 failed records will be Failed
                js.Message.Contains("File not found by service")
            )), Times.Exactly(2));
            _mockJobStatusRepository.Verify(r => r.IncrementProcessedChunkCountAsync(initialJobStatus.ParentCorrelationId, false), Times.Once);
            VerifyLog(LogLevel.Error, $"Critical file-level error for {_testBlobName}, CorrelationId {_testJobCorrelationId}");
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_ProcessingServiceReturnsPartialSuccess_UpdatesStatusToPartiallySucceeded()
        {
            // Arrange
            var parentJobId = Guid.NewGuid();
            var initialJobStatus = CreateTestJobStatus(_testJobCorrelationId, ProcessingStatus.QueuedForProcessing, JobType.Chunk, parentJobId);
            var processingResult = new FileProcessingResult { SuccessfulRecordsCount = 50, FailedRecordsCount = 10 };
            processingResult.ErrorMessages.Add("Some rows failed");

            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(_testJobCorrelationId)).ReturnsAsync(initialJobStatus);
            _mockJobStatusRepository.Setup(r => r.UpdateJobStatusAsync(It.IsAny<JobStatus>())).Returns(Task.CompletedTask);
            _mockFileProcessingService.Setup(s => s.ProcessAndStoreCdrFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId, It.IsAny<CancellationToken>()))
                                      .ReturnsAsync(processingResult);
            _mockJobStatusRepository.Setup(r => r.IncrementProcessedChunkCountAsync(parentJobId, false)).Returns(Task.CompletedTask);


            // Act
            await _cdrFileProcessingJobs.ProcessFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId);

            // Assert
            _mockJobStatusRepository.Verify(r => r.UpdateJobStatusAsync(It.Is<JobStatus>(js =>
                js.CorrelationId == _testJobCorrelationId &&
                js.Status == ProcessingStatus.PartiallySucceeded && // DetermineStatus() will set this
                js.ProcessedRecordsCount == 50 &&
                js.FailedRecordsCount == 10 &&
                js.Message.Contains("Some rows failed")
            )), Times.Exactly(2));
            _mockJobStatusRepository.Verify(r => r.IncrementProcessedChunkCountAsync(parentJobId, false), Times.Once);
        }


        [Test]
        public async Task ProcessFileFromBlobAsync_ProcessingServiceThrowsException_UpdatesStatusToFailedAndRethrowsFromFinally()
        {
            // Arrange
            var initialJobStatus = CreateTestJobStatus(_testJobCorrelationId);
            var exceptionMessage = "Core processing service failure!";

            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(_testJobCorrelationId)).ReturnsAsync(initialJobStatus);
            _mockJobStatusRepository.Setup(r => r.UpdateJobStatusAsync(It.IsAny<JobStatus>())).Returns(Task.CompletedTask)
                .Callback<JobStatus>(js => {
                    // This callback helps verify the status update inside the finally block if the queue service call fails
                    if (js.Status == ProcessingStatus.Failed && js.Message.Contains(exceptionMessage))
                    {
                        // This is the expected final update
                    }
                });
            _mockFileProcessingService.Setup(s => s.ProcessAndStoreCdrFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId, It.IsAny<CancellationToken>()))
                                      .ThrowsAsync(new InvalidOperationException(exceptionMessage));
            // Simulate failure in queue service to trigger rethrow from finally
            _mockJobStatusRepository.Setup(r => r.IncrementProcessedChunkCountAsync(initialJobStatus.ParentCorrelationId, false))
                .ThrowsAsync(new Exception("Queue service is down"));


            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(() =>
                _cdrFileProcessingJobs.ProcessFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId)
            );
            Assert.That(ex.Message, Is.EqualTo("Queue service is down")); // Exception from finally block

            // Verify the log for the initial processing error
            VerifyLogError<InvalidOperationException>(
                logMsg => logMsg.Contains($"Hangfire job CRITICAL FAILURE: Error processing blob '{_testBlobName}' with Correlation ID '{_testJobCorrelationId}'"),
                innerEx => innerEx.Message == exceptionMessage
            );

            // Verify the final status update to Failed was attempted
            _mockJobStatusRepository.Verify(r => r.UpdateJobStatusAsync(It.Is<JobStatus>(js =>
               js.CorrelationId == _testJobCorrelationId &&
               js.Status == ProcessingStatus.Failed &&
               js.Message.Contains(exceptionMessage) // Check if the original exception message is part of the status message
           )), Times.Exactly(2));

            // Verify the critical log for queue failure
            VerifyLog(LogLevel.Critical, $"CRITICAL FAILURE: Failed to send job status update to queue for JobCorrelationId: {_testJobCorrelationId}");
        }

        [Test]
        public async Task ProcessFileFromBlobAsync_FinalStatusUpdateFails_RethrowsAndLogsCritical()
        {
            // Arrange
            var initialJobStatus = CreateTestJobStatus(_testJobCorrelationId);
            var processingResult = new FileProcessingResult { SuccessfulRecordsCount = 10 }; // Success from file processing
            var statusUpdateExceptionMessage = "Final DB update failed";

            _mockJobStatusRepository.Setup(r => r.GetJobStatusByCorrelationIdAsync(_testJobCorrelationId)).ReturnsAsync(initialJobStatus);
            _mockJobStatusRepository.SetupSequence(r => r.UpdateJobStatusAsync(It.IsAny<JobStatus>()))
                .Returns(Task.CompletedTask) // For "Processing" status
                .ThrowsAsync(new Exception(statusUpdateExceptionMessage)); // For final status update in finally

            _mockFileProcessingService.Setup(s => s.ProcessAndStoreCdrFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId, It.IsAny<CancellationToken>()))
                                      .ReturnsAsync(processingResult);
            // IncrementProcessedChunkCountAsync might also fail or not be called if UpdateJobStatusAsync fails first in finally
            _mockJobStatusRepository.Setup(r => r.IncrementProcessedChunkCountAsync(initialJobStatus.ParentCorrelationId, It.IsAny<bool>()))
                                      .Returns(Task.CompletedTask);


            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(() =>
                _cdrFileProcessingJobs.ProcessFileFromBlobAsync(_testContainerName, _testBlobName, _testJobCorrelationId)
            );
            Assert.That(ex.Message, Is.EqualTo(statusUpdateExceptionMessage)); // Exception from UpdateJobStatusAsync in finally

            VerifyLog(LogLevel.Critical, $"CRITICAL FAILURE: Failed to send job status update to queue for JobCorrelationId: {_testJobCorrelationId}");
        }


        // Helper for logs without specific exception property checks
        private void VerifyLog(LogLevel expectedLogLevel, string expectedMessageContent, Times? times = null)
        {
            times ??= Times.Once();
            _mockLogger.Verify(
                x => x.Log(
                    expectedLogLevel,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((s, type) => s.ToString().Contains(expectedMessageContent)),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                times.Value);
        }

        // Helper for LogError calls where we want to verify exception properties
        private void VerifyLogError<TException>(
            Func<string, bool> messageStatePredicate,
            Func<TException, bool> exceptionPredicate,
            Times? times = null) where TException : Exception
        {
            times ??= Times.Once();
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((s, type) => messageStatePredicate(s.ToString())),
                    It.Is<TException>(ex => exceptionPredicate(ex)),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                times.Value);
        }
    }
}
