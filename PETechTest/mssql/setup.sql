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
