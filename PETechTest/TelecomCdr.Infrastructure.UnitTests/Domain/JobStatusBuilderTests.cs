using TelecomCdr.Domain.Helpers;
using TelecomCdr.Domain;

namespace TelecomCdr.Infrastructure.UnitTests.Domain
{
    [TestFixture]
    public class JobStatusBuilderTests
    {
        private JobStatusBuilder _builder;

        [SetUp]
        public void SetUp()
        {
            _builder = new JobStatusBuilder();
        }

        [Test]
        public void Constructor_InitializesWithDefaultValues()
        {
            // Act
            var jobStatus = _builder.Build();

            // Assert
            Assert.IsNotNull(jobStatus);
            Assert.AreNotEqual(Guid.Empty, jobStatus.CorrelationId);
            Assert.LessOrEqual(jobStatus.CreatedAtUtc, DateTime.UtcNow);
            Assert.GreaterOrEqual(jobStatus.CreatedAtUtc, DateTime.UtcNow.AddSeconds(-5)); // Check within a small window
            Assert.LessOrEqual(jobStatus.CreatedAtUtc, jobStatus.LastUpdatedAtUtc);
            Assert.AreEqual(ProcessingStatus.Accepted, jobStatus.Status);
            Assert.AreEqual(JobType.SingleFile, jobStatus.Type);
        }

        [Test]
        public void WithCorrelationId_SetsCorrelationId()
        {
            // Arrange
            var testId = Guid.NewGuid();

            // Act
            var jobStatus = _builder.WithCorrelationId(testId).Build();

            // Assert
            Assert.AreEqual(testId, jobStatus.CorrelationId);
        }

        [Test]
        public void WithStatus_SetsStatus()
        {
            // Arrange
            var testStatus = ProcessingStatus.Processing;

            // Act
            var jobStatus = _builder.WithStatus(testStatus).Build();

            // Assert
            Assert.AreEqual(testStatus, jobStatus.Status);
        }

        [Test]
        public void WithType_SetsType()
        {
            // Arrange
            var testType = JobType.Master;

            // Act
            var jobStatus = _builder.WithType(testType).Build();

            // Assert
            Assert.AreEqual(testType, jobStatus.Type);
        }

        [Test]
        public void WithMessage_SetsMessage()
        {
            // Arrange
            var testMessage = "Test message";

            // Act
            var jobStatus = _builder.WithMessage(testMessage).Build();

            // Assert
            Assert.AreEqual(testMessage, jobStatus.Message);
        }

        [Test]
        public void WithTotalChunks_SetsTotalChunks()
        {
            // Arrange
            var testTotalChunks = 10;

            // Act
            var jobStatus = _builder.WithTotalChunks(testTotalChunks).Build();

            // Assert
            Assert.AreEqual(testTotalChunks, jobStatus.TotalChunks);
        }

        [Test]
        public void WithProcessedChunks_SetsProcessedChunks()
        {
            // Arrange
            var testProcessedChunks = 5;

            // Act
            var jobStatus = _builder.WithProcessedChunks(testProcessedChunks).Build();

            // Assert
            Assert.AreEqual(testProcessedChunks, jobStatus.ProcessedChunks);
        }

        [Test]
        public void WithSuccessfulChunks_SetsSuccessfulChunks()
        {
            // Arrange
            var testSuccessfulChunks = 4;

            // Act
            var jobStatus = _builder.WithSuccessfulChunks(testSuccessfulChunks).Build();

            // Assert
            Assert.AreEqual(testSuccessfulChunks, jobStatus.SuccessfulChunks);
        }

        [Test]
        public void WithFailedChunks_SetsFailedChunks()
        {
            // Arrange
            var testFailedChunks = 1;

            // Act
            var jobStatus = _builder.WithFailedChunks(testFailedChunks).Build();

            // Assert
            Assert.AreEqual(testFailedChunks, jobStatus.FailedChunks);
        }

        [Test]
        public void WithProcessedRecordsCount_SetsProcessedRecordsCount()
        {
            // Arrange
            var testProcessedRecordsCount = 1000L;

            // Act
            var jobStatus = _builder.WithProcessedRecordsCount(testProcessedRecordsCount).Build();

            // Assert
            Assert.AreEqual(testProcessedRecordsCount, jobStatus.ProcessedRecordsCount);
        }

        [Test]
        public void WithFailedRecordsCount_SetsFailedRecordsCount()
        {
            // Arrange
            var testFailedRecordsCount = 50L;

            // Act
            var jobStatus = _builder.WithFailedRecordsCount(testFailedRecordsCount).Build();

            // Assert
            Assert.AreEqual(testFailedRecordsCount, jobStatus.FailedRecordsCount);
        }

        [Test]
        public void WithOriginalFileName_SetsOriginalFileName()
        {
            // Arrange
            var testFileName = "test.csv";

            // Act
            var jobStatus = _builder.WithOriginalFileName(testFileName).Build();

            // Assert
            Assert.AreEqual(testFileName, jobStatus.OriginalFileName);
        }

        [Test]
        public void WithContainerName_SetsContainerName()
        {
            // Arrange
            var testContainerName = "test-container";

            // Act
            var jobStatus = _builder.WithContainerName(testContainerName).Build();

            // Assert
            Assert.AreEqual(testContainerName, jobStatus.ContainerName);
        }

        [Test]
        public void WithBlobName_SetsBlobName()
        {
            // Arrange
            var testBlobName = "blob.csv";

            // Act
            var jobStatus = _builder.WithBlobName(testBlobName).Build();

            // Assert
            Assert.AreEqual(testBlobName, jobStatus.BlobName);
        }

        [Test]
        public void WithParentCorrelationId_SetsParentCorrelationId()
        {
            // Arrange
            var testParentId = Guid.NewGuid();

            // Act
            var jobStatus = _builder.WithParentCorrelationId(testParentId).Build();

            // Assert
            Assert.AreEqual(testParentId, jobStatus.ParentCorrelationId);
        }

        [Test]
        public void WithParentCorrelationId_Null_SetsParentCorrelationIdToNull()
        {
            // Act
            var jobStatus = _builder.WithParentCorrelationId(null).Build();

            // Assert
            Assert.IsNull(jobStatus.ParentCorrelationId);
        }


        [Test]
        public void WithLastUpdatedAtUtc_SetsLastUpdatedAtUtc()
        {
            // Arrange
            var testTimestamp = DateTime.UtcNow.AddMinutes(-10);

            // Act
            var jobStatus = _builder.WithLastUpdatedAtUtc(testTimestamp).Build();

            // Assert
            Assert.AreEqual(testTimestamp, jobStatus.LastUpdatedAtUtc);
        }

        [Test]
        public void Build_ReturnsConstructedJobStatus()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var message = "Built object";

            // Act
            var jobStatus = _builder.WithCorrelationId(correlationId)
                                   .WithMessage(message)
                                   .Build();

            // Assert
            Assert.IsNotNull(jobStatus);
            Assert.AreEqual(correlationId, jobStatus.CorrelationId);
            Assert.AreEqual(message, jobStatus.Message);
        }

        [Test]
        public void Reset_ReinitializesJobStatusToDefaults()
        {
            // Arrange
            var initialJobStatus = _builder.WithCorrelationId(Guid.NewGuid())
                                          .WithMessage("Old message")
                                          .Build();
            var initialCorrelationId = initialJobStatus.CorrelationId;

            // Act
            var newBuilderInstance = _builder.Reset();
            var resetJobStatus = newBuilderInstance.Build();

            // Assert
            Assert.AreSame(_builder, newBuilderInstance, "Reset should return the same builder instance.");
            Assert.AreNotEqual(initialCorrelationId, resetJobStatus.CorrelationId, "CorrelationId should be new after reset.");
            Assert.AreNotEqual("Old message", resetJobStatus.Message, "Message should be default (null) after reset.");
            Assert.AreEqual(ProcessingStatus.Accepted, resetJobStatus.Status);
            Assert.AreEqual(JobType.SingleFile, resetJobStatus.Type);
            Assert.LessOrEqual(resetJobStatus.CreatedAtUtc, DateTime.UtcNow);
            Assert.GreaterOrEqual(resetJobStatus.CreatedAtUtc, DateTime.UtcNow.AddSeconds(-5));
        }

        [Test]
        public void WithJobStatus_ReplacesInternalJobStatus()
        {
            // Arrange
            var newJobStatusInstance = new JobStatus
            {
                CorrelationId = Guid.NewGuid(),
                Message = "Externally created",
                Status = ProcessingStatus.Failed
            };

            // Act
            var jobStatusFromBuilder = _builder.WithJobStatus(newJobStatusInstance).Build();

            // Assert
            Assert.AreSame(newJobStatusInstance, jobStatusFromBuilder, "Build should return the instance passed to WithJobStatus.");
            Assert.AreEqual("Externally created", jobStatusFromBuilder.Message);
            Assert.AreEqual(ProcessingStatus.Failed, jobStatusFromBuilder.Status);
        }

        [Test]
        public void WithJobStatus_WithResetTrue_ReinitializesToDefaults()
        {
            // Arrange
            var originalJobStatus = _builder.WithMessage("Original").Build();
            var originalCorrelationId = originalJobStatus.CorrelationId;

            var externalJobStatus = new JobStatus { Message = "This should be ignored" };

            // Act
            var jobStatusFromBuilder = _builder.WithJobStatus(externalJobStatus, true).Build();

            // Assert
            Assert.AreNotEqual(originalCorrelationId, jobStatusFromBuilder.CorrelationId);
            Assert.AreNotEqual(externalJobStatus.Message, jobStatusFromBuilder.Message);
            Assert.AreEqual(ProcessingStatus.Accepted, jobStatusFromBuilder.Status);
            Assert.AreEqual(JobType.SingleFile, jobStatusFromBuilder.Type);
        }

        [Test]
        public void WithJobStatus_WithResetFalse_UsesProvidedInstance()
        {
            // Arrange
            _builder.WithMessage("Original").Build(); // Initial state

            var externalJobStatus = new JobStatus
            {
                CorrelationId = Guid.NewGuid(),
                Message = "External Instance",
                Status = ProcessingStatus.Processing
            };

            // Act
            var jobStatusFromBuilder = _builder.WithJobStatus(externalJobStatus, false).Build();

            // Assert
            Assert.AreSame(externalJobStatus, jobStatusFromBuilder);
            Assert.AreEqual("External Instance", jobStatusFromBuilder.Message);
            Assert.AreEqual(ProcessingStatus.Processing, jobStatusFromBuilder.Status);
        }

        [Test]
        public void FluentChaining_WorksCorrectly()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var parentId = Guid.NewGuid();
            var message = "Chained setup";
            var status = ProcessingStatus.Chunking;
            var type = JobType.Chunk;
            var totalChunks = 5;

            // Act
            var jobStatus = _builder
                .WithCorrelationId(correlationId)
                .WithMessage(message)
                .WithStatus(status)
                .WithType(type)
                .WithParentCorrelationId(parentId)
                .WithTotalChunks(totalChunks)
                .Build();

            // Assert
            Assert.AreEqual(correlationId, jobStatus.CorrelationId);
            Assert.AreEqual(message, jobStatus.Message);
            Assert.AreEqual(status, jobStatus.Status);
            Assert.AreEqual(type, jobStatus.Type);
            Assert.AreEqual(parentId, jobStatus.ParentCorrelationId);
            Assert.AreEqual(totalChunks, jobStatus.TotalChunks);
        }
    }
}
