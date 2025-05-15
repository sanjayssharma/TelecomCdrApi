-- Wait for SQL Server to be ready (optional, but can help in some environments)
-- This is a simple delay; more robust checks might involve polling.
WAITFOR DELAY '00:00:10'; -- Wait for 10 seconds

-- Create the main application database (CdrStore) if it doesn't exist
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = N'CdrStore')
BEGIN
    CREATE DATABASE CdrStore;
    PRINT 'Database CdrStore created.';
END
ELSE
BEGIN
    PRINT 'Database CdrStore already exists.';
END
GO

-- Create the Hangfire database (HangfireDb) if it doesn't exist
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = N'HangfireDb')
BEGIN
    CREATE DATABASE HangfireDb;
    PRINT 'Database HangfireDb created.';
END
ELSE
BEGIN
    PRINT 'Database HangfireDb already exists.';
END
GO

PRINT 'Database setup script completed.';


-- =====================================================================================
-- call_detail_records Table for storing cdr records
-- =====================================================================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'call_detail_records')
BEGIN
	CREATE TABLE call_detail_records (
    call_reference CHAR(34) PRIMARY KEY NOT NULL, -- Unique identifier for the call
    caller_id VARCHAR(20),                        -- Phone number of the caller, can be NULL
    recipient VARCHAR(20) NOT NULL,               -- Phone number of the recipient
    call_end_datetime DATETIME2(3) NOT NULL,           -- Combined date and time of when the call ended
    duration_seconds INT NOT NULL CHECK (duration_seconds >= 0), -- Duration in seconds
    cost DECIMAL(12, 3) NOT NULL CHECK (cost >= 0.000), -- Cost of the call, up to 3 decimal places
    currency CHAR(3) NOT NULL,                      -- ISO alpha-3 currency code
	correlation_id UNIQUEIDENTIFIER NOT NULL -- GUID of the injestion batch
);

	CREATE NONCLUSTERED INDEX idx_cdr_correlation_id
	ON call_detail_records (correlation_id);

	CREATE NONCLUSTERED INDEX idx_cdr_caller_id
	ON call_detail_records (caller_id);

	CREATE NONCLUSTERED INDEX idx_cdr_recipient
	ON call_detail_records (recipient);

	-- Optimizes queries that filter by a specific caller within a date range.
	CREATE NONCLUSTERED INDEX idx_cdr_caller_datetime
	ON call_detail_records (caller_id, call_end_datetime);

	-- Optimizes queries that filter by a specific recipient within a date range.
	CREATE NONCLUSTERED INDEX idx_cdr_recipient_datetime
	ON call_detail_records (recipient, call_end_datetime);

	CREATE NONCLUSTERED INDEX idx_cdr_cost
	ON call_detail_records (cost);

	CREATE NONCLUSTERED INDEX idx_cdr_currency
	ON call_detail_records (currency);
	
END;
GO

-- =====================================================================================
-- call_detail_record_failures Table for logging failed cdr records
-- =====================================================================================

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'call_detail_record_failures')
BEGIN
	CREATE TABLE call_detail_record_failures (
	Id BIGINT identity(1,1) PRIMARY KEY,
    UploadCorrelationId UNIQUEIDENTIFIER , -- Unique identifier correlating to the upload operation
    RowNumberInCsv int,                        -- row number in the actual csv
    RawRowData VARCHAR(1024),               -- csv comma delimited row data
	ErrorMessage VARCHAR(2000),
    FailedAtUtc DATETIME NOT NULL,           -- failure date and time of when the call ended
);

CREATE NONCLUSTERED INDEX idx_upload_correlation_id
	ON call_detail_record_failures (UploadCorrelationId);
	
END;
GO


-- =====================================================================================
-- BaseData Table for JobType Enum
-- =====================================================================================
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='JobTypes' and xtype='U')
BEGIN
	CREATE TABLE [dbo].[JobTypes] (
    [Id]   INT NOT NULL,
    [Name] NVARCHAR(50) NOT NULL,
    CONSTRAINT [PK_JobTypes] PRIMARY KEY CLUSTERED ([Id] ASC)
	);
	
	-- Populate JobTypes Table
	INSERT INTO [dbo].[JobTypes] ([Id], [Name]) VALUES
	(0, 'SingleFile'),
	(1, 'Master'),
	(2, 'Chunk');
END
ELSE
BEGIN
    PRINT 'Table JobTypes already exists.';
END
GO

-- =====================================================================================
-- BaseData Table for ProcessingStatus Enum
-- =====================================================================================

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ProcessingStatuses' and xtype='U')
BEGIN
	CREATE TABLE [dbo].[ProcessingStatuses] (
    [Id]   INT NOT NULL,
    [Name] NVARCHAR(50) NOT NULL,
    CONSTRAINT [PK_ProcessingStatuses] PRIMARY KEY CLUSTERED ([Id] ASC)
);
	
	-- Populate ProcessingStatuses Table
	INSERT INTO [dbo].[ProcessingStatuses] ([Id], [Name]) VALUES
	(0, 'Accepted'),
	(1, 'PendingQueue'),
	(2, 'Chunking'),
	(3, 'ChunksQueued'),
	(4, 'QueuedForProcessing'),
	(5, 'Processing'),
	(6, 'Succeeded'),
	(7, 'PartiallySucceeded'),
	(8, 'Failed');
END
ELSE
BEGIN
    PRINT 'Table ProcessingStatuses already exists.';
END
GO

-- =====================================================================================
-- JobStatuses Table for tracking upload jobs
-- =====================================================================================
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='job_statuses' and xtype='U')
BEGIN

	CREATE TABLE [dbo].[job_statuses] (
    [CorrelationId]        UNIQUEIDENTIFIER NOT NULL,  -- Primary Key, typically a GUID string
    [ParentCorrelationId]  UNIQUEIDENTIFIER NULL,      -- Foreign key to another JobStatus (master job)
    [JobTypeId]            INT NOT NULL,            -- Foreign Key to JobTypes.Id
    [ProcessingStatusId]   INT NOT NULL,            -- Foreign Key to ProcessingStatuses.Id
    [OriginalFileName]     NVARCHAR(255) NULL,
    [BlobName]             NVARCHAR(255) NULL,
    [ContainerName]        NVARCHAR(100) NULL,
    [TotalChunks]          INT NULL,
    [ProcessedChunks]      INT NULL,
    [SuccessfulChunks]     INT NULL,
    [FailedChunks]         INT NULL,
    [ProcessedRecordsCount] BIGINT NULL,
    [FailedRecordsCount]    BIGINT NULL,
    [CreatedAtUtc]          DATETIME2 NOT NULL,
    [LastUpdatedAtUtc]      DATETIME2 NOT NULL,
    [Message]         		NVARCHAR(2000) NULL,
    CONSTRAINT [PK_JobStatuses] PRIMARY KEY CLUSTERED ([CorrelationId] ASC),
    CONSTRAINT [FK_JobStatuses_JobTypes] FOREIGN KEY ([JobTypeId]) REFERENCES [dbo].[JobTypes]([Id]),
    CONSTRAINT [FK_JobStatuses_ProcessingStatuses] FOREIGN KEY ([ProcessingStatusId]) REFERENCES [dbo].[ProcessingStatuses]([Id])
	);

-- These help speed up queries on these columns.

	CREATE NONCLUSTERED INDEX [IX_JobStatus_ParentCorrelationId]
	ON [dbo].[job_statuses]([ParentCorrelationId] ASC)
	WHERE [ParentCorrelationId] IS NOT NULL; -- Index only non-null values if desired, or remove WHERE for all

	CREATE NONCLUSTERED INDEX [IX_JobStatus_ProcessingStatusId] -- Renamed from IX_JobStatus_Status
	ON [dbo].[job_statuses]([ProcessingStatusId] ASC);

	CREATE NONCLUSTERED INDEX [IX_JobStatus_JobTypeId] -- Renamed from IX_JobStatus_Type
	ON [dbo].[job_statuses]([JobTypeId] ASC);

	CREATE NONCLUSTERED INDEX [IX_JobStatus_CreatedAt]
	ON [dbo].[job_statuses]([CreatedAtUtc] ASC);

END
ELSE
BEGIN
    PRINT 'Table job_statuses already exists.';
END
GO