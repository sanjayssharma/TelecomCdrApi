# CDR (Call Detail Record) HTTP API

## Table of Contents

1.  [Introduction](#introduction)
2.  [Project Structure](#project-structure)
3.  [Technology Choices](#technology-choices)
4.  [Design Patterns and Principles](#design-patterns-and-principles)
5.  [API Endpoints](#api-endpoints)
6.  [Data Handling and Storage](#data-handling-and-storage)
    * [CSV Parsing, Validation, and Error Handling](#csv-parsing-validation-and-error-handling)
    * [Database Schema](#database-schema)
    * [Large File Ingestion (Blob Storage, Chunking, & Azure Function Triggered Hangfire Jobs)](#large-file-ingestion-blob-storage-chunking--azure-function-triggered-hangfire-jobs)
7.  [Background Processing (Hangfire & Azure Functions)](#background-processing-hangfire--azure-functions)
8.  [Asynchronous Job Status Tracking (Queue-Based)](#asynchronous-job-status-tracking-queue-based)
9.  [Non-Functional Requirements (NFRs)](#non-functional-requirements-nfrs)
    * [Authentication & Authorization](#authentication--authorization)
    * [Performance & Scalability](#performance--scalability)
    * [Logging and Correlation](#logging-and-correlation)
    * [Idempotency](#idempotency)
    * [Pagination](#pagination)
10. [Unit Testing](#unit-testing)
11. [Local Development Setup (Docker, Azure Storage & Redis)](#local-development-setup-docker-azure-storage--redis)
12. [Assumptions Made](#assumptions-made)
13. [Future Enhancements](#future-enhancements)
14. [Git Etiquette](#git-etiquette)

## 1. Introduction

This document outlines the solution for a Telecoms Call Detail Record (CDR) HTTP API. The API allows for the ingestion of CDR data from CSV files. Smaller files can be processed immediately. Very large files (e.g., up to 10GB) are uploaded to Azure Blob Storage. An Azure Function then processes these blobs, potentially splitting very large ones into manageable chunks, and enqueues jobs in Hangfire for each file or chunk. Processed records are stored in a primary database, while records that fail parsing or validation are logged to a separate table for review.

The API provides endpoints to query CDR data (with pagination) and the status of asynchronous processing jobs. Job status updates are handled asynchronously via Azure Storage Queues to ensure resilience and decoupling. The solution emphasizes best practices in software development, including SOLID principles, clean architecture, design patterns (CQRS, Repository, Null Object), stream processing for large data, comprehensive unit testing, and robust error handling.

For **large files (e.g., up to 10GB or more)**, the system employs a direct-to-storage upload pattern:
1.  The client requests an upload URL from the API.
2.  The API provides a secure SAS URI, and an initial job status ("PendingUpload") is created.
3.  The client uploads the file directly to a designated Azure Blob Storage container.
4.  Upon successful upload, an Azure Event Grid event triggers an Azure Function.
5.  This Azure Function updates the blob's metadata (adding a `CorrelationId` that matches the `JobId`) and then calls the orchestrator to initiate backend processing.
6.  The `BlobProcessingOrchestrator` handles fetching blob metadata, splitting the blob into manageable chunks (using a strategy pattern for flexibility, e.g., fixed-size or CSV-aware chunking (ToDo)), processing each chunk (parsing, validation), and updating job status through dedicated activities.

Processed records are stored in a primary database, while records that fail parsing or validation are logged to a separate table for review. The API provides endpoints to query CDR data (with pagination) and the status of asynchronous processing jobs.

The solution emphasizes best practices in software development, including SOLID principles, clean architecture, design patterns (CQRS with MediatR, Repository, Strategy, Options pattern, Null Object), stream processing for large data, comprehensive unit testing, and robust error handling.

*(Note: The previous mechanism for smaller file uploads directly through an API endpoint, or the role of Hangfire, might need to be re-evaluated or clarified based on this new primary flow for large files.)*


## 2. Project Structure

The solution (`PETechTest.sln`) is organized into the following projects:

* **`TelecomCdr.API`**: ASP.NET Core Web API project. Hosts API endpoints (including job status, supporting pagination), Hangfire Server, and Hangfire Dashboard. Handles request validation, initial job status creation, and idempotency checks.
* **`TelecomCdr.Core`**: Contains application logic, CQRS command and query handlers (using MediatR), business rules, and interfaces for infrastructure services (e.g., `ICdrRepository`, `IFileProcessingService`, `IBlobStorageService`, `IJobStatusRepository`, `IFailedCdrRecordRepository`, `IIdempotencyService`, `IQueueService`). Defines DTOs/Models like `PagedResponse<T>` and `JobStatusUpdateMessage`.
* **`TelecomCdr.Abstraction`**: Contains interfaces for better decoupling between projects.
* **`TelecomCdr.Domain`**: Contains core domain entities (e.g., `CallDetailRecord`, `JobStatus` with `ProcessingStatus` and `JobType` enums, `FailedCdrRecord`, `FileProcessingResult`) and domain-specific logic.
* **`TelecomCdr.Infrastructure`**: Implements infrastructure concerns such as data persistence (repositories for MSSQL supporting pagination for CDRs, Job Statuses, and Failed Records), file processing (`CsvFileProcessingService` with streaming and error handling), Azure Blob Storage interaction (`AzureBlobStorageService`), Redis client for idempotency (`RedisIdempotencyService`), and Azure Storage Queue interaction (`AzureStorageQueueService`).
* **`TelecomCdr.Hangfire`**: A class library defining the job methods that Hangfire will execute (e.g., `CdrFileProcessingJobs`). These methods invoke services from the Application/Infrastructure layers and send status update messages to an Azure Storage Queue.
* **`TelecomCdr.AzureFunctions`**: An Azure Functions (.NET Isolated Worker) project.
    * `BlobUploadProcessorFunction`: EventGrid-triggered function that sends an event when a blob upload is completed to the desired container, which updates metadata and initiates the orchestration i.e. Chunking/Storing
    * `EnqueueCdrProcessingJob`: Blob-triggered function that detects new files, implements logic for chunking very large files, creates initial job/chunk statuses, and enqueues processing jobs in Hangfire.
    * `UpdateJobStatusFromQueue`: Queue-triggered function that processes `JobStatusUpdateMessage`s from a queue to update the `JobStatuses` table asynchronously, including aggregating chunk statuses for master jobs.
* **`TelecomCdr.DurableFunctions`**: An Azure Functions (.NET Isolated Worker) project.
    * Contains the `CsvProcessingOrchestratorFunction` and its related activity functions (e.g., `GetBlobMetadataActivityFunction`, `SplitBlobIntoChunksActivityFunction`, `ProcessChunkActivityFunction`, `UpdateJobStatusActivityFunction`).
* **Test Projects**: `TelecomCdr.API.UnitTests`, `TelecomCdr.Core.UnitTests`, `TelecomCdr.Infrastructure.UnitTests`, `TelecomCdr.Hangfire.UniTests`, `TelecomCdr.AzureFunctions.Tests`.

### PETechTest solution 
```
* README.md
* /PETechTest (Solution Root, contains PETechTest.sln, docker-compose.yml)
  * |-- /TelecomCdr.Api                 # ASP.NET Core Web API
  * |-- /TelecomCdr.Abstraction         # Interfaces
  * |-- /TelecomCdr.Core                # Application Logic,  Models
  * |-- /TelecomCdr.Domain              # Domain Entities & Enums
  * |-- /TelecomCdr.Infrastructure      # Data Persistence, Services Implementations
  * |-- /TelecomCdr.Hangfire            # Hangfire Job Definitions
  * |-- /TelecomCdr.AzureFunctions      # Azure Functions (Blob Trigger, Queue Trigger, Event Trigger)
  * |-- /TelecomCdr.DurableFunctions    # Alternate serverless approach
  * |-- /*.*UnitTests|                  # Unit test projects
  * |-- /mssql|   
    * |-- setup.sql # DB creation scripts (CdrStore, HangfireDb, tables)
    * |-- init-db.sh # supporting file for docker to execute setup.sql
  * |-- .env                            # environment variable file
  * |-- docker-compose.yml              # docker compose file for local development
  * |-- large-dataset-generator.py      # python script to generate csv with large volume of data for testing
  * |-- Directory.Packages.props        # file for centeralized package management
  * |-- Leveraging-AzureServices.md     # Alternate Solutions for the problem
  * |-- DurableFucntion-Approach.md     # Details of the DurableFunction solution
  * |-- DirectToStorage_Approach.md     # Details of the Event-Driven solution
* |-- README.md
```
```
appsettings.json         # For TelecomCdr.Api

local.settings.json      # For TelecomCdr.AzureFunctions
```
## 3. Technology Choices

* **.NET 8**: Latest Long-Term Support (LTS) version of .NET.
* **ASP.NET Core**: For building the HTTP API.
* **Entity Framework Core (EF Core)**: ORM for MSSQL.
* **MediatR**: For implementing the CQRS pattern.
* **FluentValidation**: For request validation (validators registered via DI, triggered manually e.g., in MediatR pipeline).
* **CsvHelper**: For reading and writing CSV files (stream-based processing).
* **Hangfire**: For background job processing, using SQL Server for storage.
* **Azure Blob Storage SDK (`Azure.Storage.Blobs`)**: For storing large CSV files.
* **Azure Storage Queues SDK (`Azure.Storage.Queues`)**: For asynchronous job status updates.
* **Azure Event Grid**: For reacting to blob creation events.
* **Azure Functions (.NET Isolated Worker)**: For blob-triggered automation and queue processing.
* **StackExchange.Redis**: For distributed caching for idempotency.
* **Serilog**: For structured logging, with sinks for Console, File, and Application Insights.
* **NUnit & Moq**: For unit testing.
* **Docker & Docker Compose**: For containerizing the API, MSSQL, and Redis for local development.
* **Microsoft SQL Server (MSSQL)**: Primary data store for CDRs, Job Statuses, Failed Records, and Hangfire data.

*(Hangfire's role has been largely superseded by Durable Functions for the main file processing workflow in this new architecture).*


## 4. Design Patterns and Principles

* **SOLID Principles, DRY, Clean Architecture**.
* **CQRS & Mediator Pattern** (MediatR).
* **Repository Pattern** (`ICdrRepository`, `IJobStatusRepository`, `IFailedCdrRecordRepository`).
* **Strategy Pattern** for service interfaces.
* **Null Object Pattern** (e.g., `NullIdempotencyService`).
* **Action Filters** (`IdempotencyAttribute`).
* **Event-Driven Architecture**: Azure Function triggered by blob creation; job status updates via message queues.
* **Stream Processing**: For handling large CSV files efficiently.
* **File Chunking (Conceptually for very large files)**: Logic in Azure Function to split large blobs and enqueue parallel jobs.
* **Direct-to-Storage Upload Pattern**: For large file ingestion.

## 5. API Endpoints

* **`POST /api/cdr/initiate-direct-upload`**: Client requests to upload a large file. API returns a SAS URI for direct Azure Blob Storage upload and a `JobId`.
* **`POST /api/cdr/upload`**: For immediate processing of smaller CSV files.
* **`POST /api/cdr/enqueue`**: Uploads file to Azure Blob Storage, creates initial `JobStatus` (type `Master` or `SingleFile`). Asynchronous processing via Azure Function & Hangfire. Returns `CorrelationId`.
* **`GET /api/jobstatus/{correlationId}`**: Retrieves status of an asynchronous job (master, chunk, or single file).
* **`GET /api/cdr/reference/{reference}`**: Retrieves a specific CDR.
* **`GET /api/cdr/caller/{callerId}`**: Paginated list of CDRs by `caller_id`. (Params: `pageNumber`, `pageSize`)
* **`GET /api/cdr/recipient/{recipientId}`**: Paginated list of CDRs by `recipient`. (Params: `pageNumber`, `pageSize`)
* **`GET /api/cdr/correlation/{correlationId}`**: Paginated list of CDRs by `UploadCorrelationId`. (Params: `pageNumber`, `pageSize`)
* **`GET /hangfire`**: Hangfire dashboard (requires securing in production).

## 6. Data Handling and Storage

### CSV Parsing, Validation, and Error Handling
* **Date Format**: Assumes "dd/MM/yyyy" for dates and "HH:mm:ss" for times in CSV.
* **Streaming**: `CsvFileProcessingService` reads CSVs row-by-row from streams to handle large files with low memory.
* **Row-Level Error Handling**:
    * Each row is parsed and validated within a `try-catch`.
    * Failed rows (due to format, missing data, etc.) are not processed into `CallDetailRecords`.
    * Instead, a `FailedCdrRecord` is created storing the `UploadCorrelationId`, raw row data, error message, and row number.
    * These failed records are batched and saved to the `FailedCdrRecords` table.
* **Batching**: Both successful CDRs and failed records are inserted into the database in batches (e.g., 1000 records) for performance.

### Database Schema
* **`CallDetailRecords` table**: `Id` (PK), `CallerId`, `Recipient`, `CallEndDateTime`, `Duration`, `Cost`, `Reference` (unique), `Currency`, `UploadCorrelationId` (FK to master JobStatus `CorrelationId`).
* **`JobStatuses` table**:
    * `CorrelationId` (PK, unique ID for this job status entry).
    * `Status` (enum: `Accepted`, `Chunking`, `ChunksQueued`, `Processing`, `Succeeded`, `PartiallySucceeded`, `Failed`).
    * `Type` (enum: `Master`, `Chunk`, `SingleFile`).
    * `ParentCorrelationId` (FK to master `JobStatus.CorrelationId` if this is a chunk).
    * `Message`, `OriginalFileName`, `BlobName`, `ContainerName`.
    * `TotalChunks`, `ProcessedChunks`, `SuccessfulChunks`, `FailedChunks` (for master jobs).
    * `ProcessedRecordsCount`, `FailedRecordsCount` (for single files or individual chunks; master job aggregates these).
    * `CreatedAtUtc`, `LastUpdatedAtUtc`.
* **`FailedCdrRecords` table**: `Id` (PK, auto-increment), `UploadCorrelationId` (FK to master JobStatus `CorrelationId`), `RowNumberInCsv`, `RawRowData`, `ErrorMessage`, `FailedAtUtc`.
* **Hangfire Tables**: Created by Hangfire in `HangfireDb`.

### Large File Ingestion (Blob Storage, Chunking, & Azure Function Triggered Hangfire Jobs)
1.  `POST /api/cdr/enqueue` receives file, generates a master `CorrelationId`.
2.  API creates an initial `JobStatus` record (type `Master` or `SingleFile`, status `Accepted`).
3.  File uploaded to Azure Blob Storage with `UploadCorrelationId` in metadata.
4.  **`TelecomCdr.AzureFunctions.EnqueueCdrProcessingJob` (Blob Triggered):**
    * Retrieves the blob and its metadata (`UploadCorrelationId` which is the master job's ID).
    * **File Size Check & Chunking Logic:**
        * If file is below a threshold (e.g., 500MB), it's treated as a `SingleFile` if not already marked. A single Hangfire job is enqueued for this blob, using its master `CorrelationId`.
        * If file is very large:
            * Updates master `JobStatus` to `ProcessingStatus.Chunking`.
            * Splits the large blob into smaller, CSV-aware chunks (e.g., 100-250MB each, ensuring splits occur on line breaks and headers are handled).
            * For each chunk:
                * Uploads chunk to blob storage (e.g., `{originalName}_chunk_{N}.csv`).
                * Creates a new `JobStatus` record for the chunk (type `Chunk`, `ParentCorrelationId` = master job's ID, status `QueuedForProcessing`, unique `CorrelationId` for this chunk).
                * Enqueues a Hangfire job (`CdrFileProcessingJobs.ProcessFileFromBlobAsync`) for this specific chunk, passing the chunk's `CorrelationId`.
            * Updates master `JobStatus` to `ProcessingStatus.ChunksQueued` with `TotalChunks` count.
5.  Hangfire jobs process individual files or chunks.

### Large File Ingestion Flow (Storage Event Triggered and is recommended approach)
1.  Client calls `POST /api/cdr/initiate-direct-upload` with file metadata (name, content type).
2.  API (via `InitiateDirectUploadCommandHandler`):
    * Generates a unique `JobId`.
    * Generates a SAS URI for Azure Blob Storage, targeting a path like `{JobId}/{originalFileName.csv}`.
    * Returns the SAS URI and `JobId` to the client.
3.  Client uses the SAS URI to `PUT` the file directly to Azure Blob Storage.
4.  **Azure Event Grid & Function (`BlobUploadProcessorFunction`):**
    * `BlobCreated` event from Azure Storage triggers the `BlobUploadProcessorFunction`.
    * The function extracts `JobId`, `ContainerName`, `BlobName`, `OriginalFileName` from the event/blob path.
    * **Sets Blob Metadata**: Updates the uploaded blob's metadata to include `CorrelationId: JobId`.
    * Triggers `BlobProcessingOrchestrator` (containing `JobId`, blob details).
5.  **Core Layer (`BlobProcessingOrchestrator`):**
    * Updates `JobStatus` to `Queued` (or similar).
    * Starts processing the file using hangfire. The file processing is initiated using Hangfire. 
    *While both Durable Functions and Hangfire are viable options for background processing, I’ve chosen Hangfire for its simplicity and ease of reuse. Although Durable Functions offer built-in support for managing state and orchestration, Hangfire provides a straightforward and effective solution for our current requirements.*
    **chunking strategy** (e.g., `FixedSizeChunkingStrategy`) to define logical chunks.
    * For each chunk, calls `SplitUploadBlobIntoChunksAsync`.
        * `SplitUploadBlobIntoChunksAsync` reads its assigned part of the blob (using offset/size from `ChunkInfo`), uses `CsvFileProcessingService` for parsing/validation, and saves `CallDetailRecord` / `FailedCdrRecord` batches.
    * Aggregates results from chunk processing.
    * Uses `UpdateJobStatusActivityFunction` to persist progress and final status of the job.


## 7. Background Processing (Hangfire & Azure Functions)

* **`CsvFileProcessingService`**: Core logic for stream-processing a CSV (file or chunk), parsing rows, validating, creating `CallDetailRecord` or `FailedCdrRecord` objects, and batching them for DB insertion. Returns `FileProcessingResult` (counts of successful/failed records for the processed stream).
* **`TelecomCdr.HangfireJobs.CdrFileProcessingJobs.ProcessFileFromBlobAsync`**:
    * Marked with `[AutomaticRetry(Attempts = 0)]` to prevent Hangfire from retrying on unhandled exceptions.
    * Fetches its `JobStatus` record (for the file or chunk it's processing).
    * Updates its `JobStatus` to `ProcessingStatus.Processing` directly via `IJobStatusRepository`.
    * Calls `IFileProcessingService` to process the blob content.
    * **Sends `JobStatusUpdateMessage` to Azure Storage Queue:** Upon completion (success or failure) of processing the file/chunk, it constructs a message containing the `CorrelationId` (of the file/chunk), `ParentCorrelationId` (if a chunk), `JobType`, the `FileProcessingResult`, and the determined final status (`Succeeded`, `Failed`, `PartiallySucceeded`) for that file/chunk. This message is sent to a dedicated queue via `IQueueService`.
    * If sending to the queue fails critically, the Hangfire job itself will fail (and not retry).
* **`TelecomCdr.AzureFunctions.UpdateJobStatusFromQueue` (Queue Triggered):**
    * Listens to the job status update queue.
    * Deserializes `JobStatusUpdateMessage`.
    * Uses `IJobStatusRepository` to update the `JobStatus` record for the specific file/chunk (setting status, message, processed/failed record counts).
    * If the message is for a chunk, it calls `IJobStatusRepository.IncrementProcessedChunkCountAsync`. This method, in turn, checks if all chunks for a master job are complete and, if so, calls `UpdateMasterJobStatusBasedOnChunksAsync` to aggregate results (total processed/failed records from all chunks) and set the final status of the master job.
    * Handles deserialization errors by allowing the message to be retried by the Functions runtime and eventually dead-lettered.

## 8. Asynchronous Job Status Tracking (Queue-Based)

Described in detail in sections 6 and 7. This queue-based approach decouples the final status update from the Hangfire job execution, improving resilience. The `JobStatus` entity tracks the lifecycle from initial acceptance, through chunking (if applicable), to final completion or failure, including aggregated results for chunked master jobs.

## 9. Non-Functional Requirements (NFRs)

### Authentication & Authorization
* Current State: Basic setup. API endpoints are open. Hangfire Dashboard is open.
* Future: Implement JWT/OAuth for API, secure Hangfire Dashboard (e.g., ASP.NET Core Identity policies).

### Performance & Scalability
* **Streaming I/O**: `CsvFileProcessingService` uses stream processing for large files.
* **Batch Database Operations**: Reduces DB roundtrips.
* **Asynchronous Processing**: Hangfire and Azure Functions for background tasks.
* **File Chunking**: For very large files, allows parallel processing of parts of a single file across multiple Hangfire workers.
* **Queue-Based Status Updates**: Decouples status updates, improving throughput and resilience.
* **Database Provisioning**: SQL Server resources (CPU, IOPS, memory) must match the load.
* **Hangfire Worker Scaling**: `TelecomCdr.Api` (or a dedicated Hangfire worker service) can be scaled out.
* **Azure Function Scaling**: Azure Functions scale automatically based on load.

### Logging and Correlation
* **Serilog**: Structured logging to Console, File, and Application Insights.
* **`UploadCorrelationId` / `CorrelationId`**: Propagated through the system for tracing individual uploads and their constituent jobs/chunks.

### Idempotency
* **`Idempotency-Key` Header**: For API write operations.
* **Request Payload Hashing & Redis**: `IdempotencyAttribute` uses Redis to store request hashes and cached responses to prevent duplicate operations.
* **Hangfire Job Idempotency (Consideration)**: While Hangfire retries are disabled for the main processing job, if a job *could* be retried, the underlying data processing (e.g., record insertion) should ideally be idempotent (check if record exists before insert).

### Pagination
* Query endpoints for CDRs (`/caller`, `/recipient`, `/correlation`) support `pageNumber` and `pageSize`.
* `PagedResponse<T>` structure used for responses.

## 10. Unit Testing
* NUnit and Moq for testing Application, Infrastructure, Hangfire Jobs, and Azure Functions layers. Focus on business logic, service interactions, and error handling.

## 11. Local Development Setup (Docker, Azure Storage & Redis)
* **`docker-compose.yml`**: Manages `cdr-api`, `mssql-server`, `redis`.
* **`mssql/setup.sql`**: Initializes `CdrDb`, `HangfireDb`, and creates tables (`CallDetailRecords`, `JobStatuses`, `FailedCdrRecords`).
* **Azure Storage Emulator (Azurite)**: Recommended for local blob and queue development. Configure connection strings in `appsettings.json` (for API) and `local.settings.json` (for Functions) to `UseDevelopmentStorage=true`.
* **Azure Functions Core Tools**: For running `TelecomCdr.AzureFunctions` locally.
* **Environment Variables**: `SA_PASSWORD` for SQL Server, connection strings for Azure services.
* **`local.settings.json` (`TelecomCdr.AzureFunctions`):**
    * `AzureWebJobsStorage`: For Functions runtime (can be Azurite).
    * `HANGFIRE_SQL_CONNECTION_STRING`: For Hangfire client to enqueue jobs.
    * `AZURE_QUEUE_STORAGE_CONNECTION_STRING`: For QueueTrigger and `AzureStorageQueueService`.
    * `AZURE_QUEUE_STORAGE_JOB_STATUS_UPDATE_QUEUE_NAME`: Name of the job status queue.
    * `MAIN_DB_CONNECTION_STRING`: For `IJobStatusRepository` to connect to the app's main DB.
* **`appsettings.json` (`TelecomCdr.Api`):**
    * `ConnectionStrings:CdrConnection`, `ConnectionStrings:HangfireConnection`.
    * `AzureBlobStorage:ConnectionString`, `AzureBlobStorage:ContainerName`.
    * `RedisSettings:ConnectionString`.
    * `AzureQueueStorage:ConnectionString`, `AzureQueueStorage:JobStatusUpdateQueueName`.
    * `ApplicationInsights:ConnectionString`.
    * `Serilog` configuration.

## 12. Assumptions Made
* CSV Date/Time formats are consistent ("dd/MM/yyyy", "HH:mm:ss").
* `Reference` field in CSV is unique per upload context.
* Network connectivity between services (API, Functions, DB, Storage, Redis).

## 13. Future Enhancements
* **API for Failed Records**: `GET /api/failedrecords/{correlationId}`.
* **Retry Mechanism for Corrected Failed Records**.
* **User Interface**: For job monitoring and data management.
* **Advanced Filtering/Sorting** for query APIs.
* **Security Hardening**: Full auth for API & Hangfire Dashboard.
* **Distributed Tracing**: Enhance correlation across microservices/components.
* **Durable Functions for Chunking**: For more robust large file splitting.
* **Schema Validation for CSVs**: More advanced validation beyond basic field checks.


