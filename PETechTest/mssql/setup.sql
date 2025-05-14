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


-- ---------------- Create call_detail_records table ---------------------------------------------
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

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'call_detail_record_failures')
BEGIN
	CREATE TABLE call_detail_record_failures (
	Id BIGINT identity(1,1) PRIMARY KEY,
    UploadCorrelationId CHAR(36) , -- Unique identifier correlating to the upload operation
    RowNumberInCsv int,                        -- row number in the actual csv
    RawRowData VARCHAR(1024),               -- csv comma delimited row data
	ErrorMessage VARCHAR(2000),
    FailedAtUtc DATETIME NOT NULL,           -- failure date and time of when the call ended
);

CREATE NONCLUSTERED INDEX idx_upload_correlation_id
	ON call_detail_record_failures (UploadCorrelationId);
	
END;
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'cdr_ingestions')
BEGIN
	CREATE TABLE cdr_ingestions (
    Id int PRIMARY KEY NOT NULL,  -- This could be used in the call_detail_records as a foreign key, but keeping it as is for now.
	triggered_by varchar(50) NOT NULL,
    start_date VARCHAR(20) NOT NULL, 
    completed_date VARCHAR(20) NOT NULL,
    status varchar(10),
    [error_message] varchar(500),
	correlation_id UNIQUEIDENTIFIER NOT NULL,
);
	
END;
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='JobStatuses' and xtype='U')
BEGIN
    CREATE TABLE JobStatuses (
        CorrelationId NVARCHAR(100) NOT NULL PRIMARY KEY, 
        Status NVARCHAR(50) NOT NULL,                    
        Message NVARCHAR(2000) NULL,                     
        ProcessedRecordsCount INT NULL,
        FailedRecordsCount INT NULL,
        OriginalFileName NVARCHAR(255) NULL,            
        BlobName NVARCHAR(255) NULL,                    
        ContainerName NVARCHAR(100) NULL,              
        CreatedAtUtc DATETIME2 NOT NULL,
        LastUpdatedAtUtc DATETIME2 NOT NULL
    );

    PRINT 'Table JobStatuses created.';

    CREATE INDEX IX_JobStatuses_LastUpdatedAtUtc ON JobStatuses (LastUpdatedAtUtc);
    PRINT 'Index IX_JobStatuses_LastUpdatedAtUtc created on JobStatuses table.';

END
ELSE
BEGIN
    PRINT 'Table JobStatuses already exists.';
END
GO