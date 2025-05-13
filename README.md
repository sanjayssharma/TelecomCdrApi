# Telecoms Call Detail Record (CDR) HTTP API

## Table of Contents

1.  [Introduction](#introduction)
2.  [Project Structure](#project-structure)
3.  [Technology Choices](#technology-choices)
4.  [Design Patterns and Principles](#design-patterns-and-principles)
5.  [API Endpoints](#api-endpoints)
6.  [Data Handling and Storage](#data-handling-and-storage)
    * [CSV Parsing](#csv-parsing)
    * [Data Validation](#data-validation)
    * [Database Schema](#database-schema)
    * [Large File Ingestion (Blob Storage & Azure Function Triggered Hangfire Jobs)](#large-file-ingestion-blob-storage--azure-function-triggered-hangfire-jobs)
7.  [Background Processing (Hangfire & Azure Functions)](#background-processing-hangfire--azure-functions)
8.  [Asynchronous Job Status Tracking](#asynchronous-job-status-tracking)
9.  [Non-Functional Requirements (NFRs)](#non-functional-requirements-nfrs)
    * [Authentication & Authorization](#authentication--authorization)
    * [Performance](#performance)
    * [Logging and Correlation](#logging-and-correlation)
    * [Idempotency](#idempotency)
    * [Pagination](#pagination)
10. [Unit Testing](#unit-testing)
11. [Local Development Setup (Docker, Azure Storage & Redis)](#local-development-setup-docker-azure-storage--redis)
12. [Assumptions Made](#assumptions-made)
13. [Future Enhancements](#future-enhancements)
14. [Git Etiquette](#git-etiquette)

## 1. Introduction

This document outlines the solution for a Telecoms Call Detail Record (CDR) HTTP API. The API allows for the ingestion of CDR data from CSV files. Smaller files can be processed immediately. Very large files are uploaded to Azure Blob Storage, and their processing is initiated by an Azure Function that enqueues a job in Hangfire. The API also provides endpoints to query CDR data (with pagination) and the status of asynchronous processing jobs.

The solution emphasizes best practices in software development, including SOLID principles, design patterns (CQRS, Repository), comprehensive unit testing, and robust asynchronous processing.

## 2. Project Structure

The solution is organized into the following projects, following a clean architecture approach:

* **`TelecomCdr.Api`**: ASP.NET Core Web API project. Hosts API endpoints (including job status, supporting pagination), Hangfire Server, and Hangfire Dashboard. Handles request validation and initial job status creation. Contains API contracts like `PaginationQuery`.

## 3. Technology Choices

* **.NET 8**: Latest Long-Term Support (LTS) version of .NET.
* **ASP.NET Core**: For building the HTTP API.
* **Entity Framework Core (EF Core)**: As the Object-Relational Mapper (ORM) for MSSQL.
* **MediatR**: For implementing the CQRS pattern.
* **CsvHelper**: For reading and writing CSV files.
* **Hangfire**: For background job processing, using SQL Server for storage.
* **Azure Blob Storage SDK (`Azure.Storage.Blobs`)**: For storing large CSV files.
* **Azure Functions**: For blob-triggered automation to enqueue Hangfire jobs.
* **StackExchange.Redis**: For implementing distributed caching for idempotency.
* **Serilog**: For structured logging.
* **FluentValidation**: For request validation.
* **NUnit**: As the primary testing framework for unit tests.
* **Moq**: As the mocking library for unit tests.
* **Docker & Docker Compose**: For containerizing the API, MSSQL, and Redis for local development.
* **Microsoft SQL Server (MSSQL)**: As the primary data store for CDRs and Job Statuses.

## 4. Design Patterns and Principles

* **SOLID Principles**: Applied throughout the solution.
* **DRY (Don't Repeat Yourself)**: Minimized code duplication.
* **CQRS (Command Query Responsibility Segregation)**: Implemented using MediatR.
* **Mediator Pattern**: Facilitated by MediatR.
* **Repository Pattern**: `ICdrRepository`, `IJobStatusRepository` abstract data access.
* **Strategy Pattern (Conceptual)**: `IBlobStorageService`, `IFileProcessingService`, `IIdempotencyService` interfaces allow for different implementations.
* **Action Filters**: Used for implementing idempotency checks (`IdempotencyAttribute`).
* **Event-Driven Architecture**: Azure Function triggered by blob creation.
* **Pagination**: Implemented for list-based query endpoints.

## 5. API Endpoints

The API exposes the following endpoints:

* **`POST /api/cdr/upload`**:
    * Description: Uploads a CDR CSV file for immediate processing. Suitable for smaller files.
    * Request: `multipart/form-data` with a CSV file.
    * Response: `202 Accepted` with a correlation ID.
* **`POST /api/cdr/upload-large`**:
    * Description: Uploads a CDR CSV file to Azure Blob Storage. Creates an initial job status record. Processing is triggered asynchronously via Azure Function and Hangfire.
    * Request: `multipart/form-data` with a CSV file. `X-Correlation-ID` header recommended.
    * Response: `202 Accepted` with the `CorrelationId` and `BlobName`.
* **`GET /api/jobstatus/{correlationId}`**:
    * Description: Retrieves the current status of an asynchronous file processing job.
    * Response: `200 OK` with job status details or `404 Not Found`.
* **`GET /api/cdr/reference/{reference}`**:
    * Description: Retrieves a specific Call Detail Record by its unique `reference`.
    * Response: `200 OK` with the CDR data or `404 Not Found`.
* **`GET /api/cdr/caller/{callerId}`**:
    * Description: Retrieves Call Detail Records for a given `caller_id` with pagination.
    * Query Parameters: `pageNumber` (int, default 1), `pageSize` (int, default 10, max 100).
    * Response: `200 OK` with a `PagedResponse<CallDetailRecord>` containing the list of CDRs for the page and pagination metadata.
* **`GET /api/cdr/recipient/{recipientId}`**:
    * Description: Retrieves Call Detail Records for a given `recipient` with pagination.
    * Query Parameters: `pageNumber` (int, default 1), `pageSize` (int, default 10, max 100).
    * Response: `200 OK` with a `PagedResponse<CallDetailRecord>`.
* **`GET /api/cdr/correlation/{correlationId}`**:
    * Description: Retrieves Call Detail Records associated with a specific `UploadCorrelationId` with pagination.
    * Query Parameters: `pageNumber` (int, default 1), `pageSize` (int, default 10, max 100).
    * Response: `200 OK` with a `PagedResponse<CallDetailRecord>`.
* **`GET /hangfire`**: (If configured and enabled)
    * Description: Accesses the Hangfire dashboard. Requires appropriate authorization.

## 6. Data Handling and Storage

### CSV Parsing
`CsvHelper` parses uploaded CSV files.

### Data Validation
Basic validation via FluentValidation and during parsing.

### Database Schema
* **`CallDetailRecords` table**: (See previous definition)
* **`JobStatuses` table**: (See previous definition)

### Large File Ingestion (Blob Storage & Azure Function Triggered Hangfire Jobs)
1.  The `POST /api/cdr/upload-large` endpoint in `TelecomCdr.Api` receives the large CSV file.
2.  The API generates/retrieves an `UploadCorrelationId`.
3.  It creates an initial record in the `JobStatuses` database table with this `UploadCorrelationId` and a status like "Accepted" (via `IJobStatusRepository`).
4.  The API uploads the file to Azure Blob Storage (via `IBlobStorageService`), adding `UploadCorrelationId` to the blob's metadata.
5.  An Azure Function (`Cdr.AzureFunctions.EnqueueCdrProcessingJob`) is configured with a Blob Trigger, monitoring the `cdr-uploads` container.
6.  Upon detection of a new blob, the Azure Function is invoked. It reads the blob's name and its `UploadCorrelationId` metadata.
7.  The Azure Function then uses Hangfire's `BackgroundJobClient` to enqueue a new job. This job will call `Cdr.HangfireJobs.CdrFileProcessingJobs.ProcessFileFromBlobAsync`.
8.  The enqueued job details (including blob name, container name, and `UploadCorrelationId`) are stored by Hangfire in its configured MSSQL database.

## 7. Background Processing (Hangfire & Azure Functions)
* **Azure Functions (`Cdr.AzureFunctions`)**:
    * **Blob Trigger**: Listens for new files in the Azure Blob Storage container.
    * **Job Enqueuing**: Responsible for creating and enqueuing Hangfire jobs. It needs Hangfire client libraries and configuration to connect to the Hangfire SQL database.
* **Hangfire Job Definitions (`Cdr.HangfireJobs`)**:
    * `CdrFileProcessingJobs.ProcessFileFromBlobAsync`: This method is executed by Hangfire.
    * It updates the `JobStatus` record (via `IJobStatusRepository`) to "Processing", then "Succeeded" or "Failed".
    * It invokes `IFileProcessingService` (implemented in `Cdr.Infrastructure`) to download the blob, parse CSVs, and store data.
* **Hangfire Server**:
    * Hosted within the `TelecomCdr.Api` application.
    * Polls the Hangfire database for pending jobs and executes them using available worker threads.
* **`IFileProcessingService`**: (Implemented in `Cdr.Infrastructure`)
    * `ProcessAndStoreCdrFileFromBlobAsync`: Contains the logic to download a blob, parse its CSV content, map it to domain entities, and save it to the database.
    * Designed for efficiency: stream blob downloads, use `CsvHelper` for parsing, and perform batch database inserts.

## 8. Asynchronous Job Status Tracking
* **`JobStatus` Entity**: A domain entity (`Cdr.Domain.JobStatus`) with `ProcessingStatus` enum, representing the state of a file processing job.
* **`IJobStatusRepository`**: An interface (`Cdr.Application.Interfaces.IJobStatusRepository`) for creating and updating job status records. Implemented in `Cdr.Infrastructure.Persistence.Repositories.MssqlJobStatusRepository`.
* **Status Flow**:
    1.  **`TelecomCdr.Api` (`/upload-large`)**: Creates `JobStatus` with `Status = ProcessingStatus.Accepted`.
    2.  **`Cdr.HangfireJobs` (`CdrFileProcessingJobs`)**:
        * On job start: Updates `JobStatus` to `Status = ProcessingStatus.Processing`.
        * On success: Updates `JobStatus` to `Status = ProcessingStatus.Succeeded`.
        * On failure: Updates `JobStatus` to `Status = ProcessingStatus.Failed`, populating `Message` with error details.
* **Status Query API**: `GET /api/jobstatus/{correlationId}` in `TelecomCdr.Api` allows clients to poll for the job's status.

## 9. Non-Functional Requirements (NFRs)

### Authentication & Authorization
* **Current State**: Not implemented.
* **Future Considerations**: JWT Bearer tokens, ASP.NET Core Identity, role-based/policy-based authorization.

### Performance
* **Large File Uploads**: Streaming to Azure Blob Storage, background processing with Hangfire, batch database inserts.
* **Database Queries**: Asynchronous operations, proper indexing, pagination for list endpoints.
* **Connection Pooling**: Managed by EF Core and Redis client.
* **Hangfire Worker Count**: Configurable to scale processing capacity.

### Logging and Correlation
* **Structured Logging**: Serilog with `X-Correlation-ID` propagation.
* **`UploadCorrelationId`**: Used across services and stored in relevant entities/metadata.

### Idempotency
* **`Idempotency-Key` Header**: Supported for write operations via `IdempotencyAttribute`.
* **Request Payload Hashing**: Used for validation against cached requests in Redis.
* **Storage**: Redis is used for caching idempotency data.

### Pagination
* **Query Parameters**: List endpoints (`/caller`, `/recipient`, `/correlation`) accept `pageNumber` and `pageSize` query parameters.
* **Response Format**: Responses for paginated endpoints use the `PagedResponse<T>` structure, including data for the current page and metadata (total count, total pages, etc.).
* **Repository Support**: `ICdrRepository` methods implement efficient data fetching using `Skip()` and `Take()`.

## 10. Unit Testing

* **Framework**: NUnit.
* **Mocking**: Moq.
* **Test Projects**: `Cdr.Application.Tests`, `Cdr.Infrastructure.Tests`, `Cdr.HangfireJobs.Tests`, `Cdr.AzureFunctions.Tests`.
* **Coverage Target**: Aiming for >80% code coverage for core logic.

## 11. Local Development Setup (Docker, Azure Storage & Redis)

A `docker-compose.yml` file is provided:
* It spins up:
    1.  `cdr-api`: The .NET API application (hosting Hangfire Server).
    2.  `mssql-server`: A Microsoft SQL Server container (for CDRs, Job Statuses, Hangfire data).
    3.  `redis`: A Redis container (for idempotency caching).
* **Database Initialization**: Via `init-db.sh` and `setup.sql` in the MSSQL Docker image. The `setup.sql` should include table creation scripts for `CallDetailRecords` and `JobStatuses`.
* **Azure Blob Storage**:
    * For testing the `/upload-large` functionality locally, use the Azure Storage Emulator (Azurite). Ensure the emulator is running and accessible, and configure `appsettings.json` to point to it (e.g., `AzureBlobStorage:ConnectionString = "UseDevelopmentStorage=true"`).
* **Azure Functions**:
    * For local development of `Cdr.AzureFunctions`, use the Azure Functions Core Tools. Configure `local.settings.json` with `AZURE_STORAGE_CONNECTION_STRING` (pointing to Azurite) and `HANGFIRE_SQL_CONNECTION_STRING` (pointing to the Dockerized MSSQL).
* **Usage**:
    1.  Ensure Docker, Azurite, and Azure Functions Core Tools are running/available.
    2.  Configure `appsettings.json` (for API) and `local.settings.json` (for Functions).
    3.  Navigate to the `/docker` directory and run `docker-compose up --build`.
    4.  Run the Azure Function locally using `func start`.

## 12. Assumptions Made

* **CSV File Format Consistency**: Header row expected. Columns: `caller_id, recipient, call_date, end_time, duration, cost, reference, currency`.
* **`call_date` and `end_time` in CSV**: Assumed to be `yyyy-MM-dd` and `HH:mm:ss` respectively.
* **Uniqueness of `reference`**: Unique constraint in the `CallDetailRecords` table.
* **Stateless API** (except for idempotency cache).
* **Development Environment**: Docker, .NET SDK, Azure Functions Core Tools available.

## 13. Future Enhancements

* **Advanced Error Handling for CSVs**.
* **Data Archival/Purging Strategy**.
* **Security Hardening**.
* **Scalability**: Horizontal scaling, distributed cache.
* **Monitoring and Alerting**.
* **API Versioning**.
* **Configuration Management**.
* **Integration Tests**.
* **Blob Deletion Strategy**.
* **WebSockets/SignalR** for real-time status updates.
* **More Advanced Filtering/Sorting** for query endpoints (potentially combined with pagination).
