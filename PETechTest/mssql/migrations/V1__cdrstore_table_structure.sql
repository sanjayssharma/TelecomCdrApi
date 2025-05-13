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