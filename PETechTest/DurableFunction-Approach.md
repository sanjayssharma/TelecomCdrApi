# Durable Functions Solution for Large CSV Ingestion

This document outlines an architecture using Azure Durable Functions to process large CSV files, including logic for file chunking, parallel processing, and status tracking, while reusing existing application services.

## Table of Contents

1.  [Overview](#overview)
2.  [Key Components](#key-components)
    * [Durable Functions](#durable-functions-components)
    * [Existing Services Leveraged](#existing-services-leveraged)
3.  [Detailed Workflow](#detailed-workflow)
4.  [Status Tracking](#status-tracking)
5.  [Project Structure Considerations](#project-structure-considerations)
6.  [Dependency Injection for Durable Functions Project](#dependency-injection-for-durable-functions-project)
7.  [Conceptual Code Snippets](#conceptual-code-snippets)
    * [A. `BlobTriggerStarterFunction`](#a-blobtriggerstarterfunction)
    * [B. `CsvProcessingOrchestratorFunction` (Orchestrator)](#b-csvprocessingorchestratorfunction-orchestrator)
    * [C. `GetBlobMetadataActivityFunction`](#c-getblobmetadataactivityfunction)
    * [D. `SplitBlobIntoChunksActivityFunction`](#d-splitblobintochunksactivityfunction)
    * [E. `ProcessChunkActivityFunction`](#e-processchunkactivityfunction)
8.  [Error Handling](#error-handling)
9.  [Pros & Cons of This Approach](#pros--cons-of-this-approach)
10. [Cost Considerations](#cost-considerations)

---

## 1. Overview

Azure Durable Functions provide stateful function execution in a serverless environment, making them well-suited for orchestrating complex, long-running processes like large file ingestion with chunking. This solution aims to:

* Handle very large CSV files (e.g., 10GB+) efficiently.
* Split large files into manageable chunks for parallel processing.
* Reuse existing business logic encapsulated in services like `CsvFileProcessingService`.
* Provide robust status tracking for the overall ingestion process and individual chunks.
* Leverage serverless scaling.

---

## 2. Key Components

### Durable Functions Components

These functions will reside in our `TelecomCdr.DurableFunctions` project (using the .NET Isolated Worker model).

1.  **`BlobTriggerStarterFunction` (Starter Function):**
    * Triggered when a new blob is uploaded to the designated Azure Blob Storage container (e.g., `raw-uploads`).
    * Its primary role is to extract initial information (blob name, container, metadata like the master `UploadCorrelationId`) and start an instance of the `CsvProcessingOrchestratorFunction`.

2.  **`CsvProcessingOrchestratorFunction` (Orchestrator Function):**
    * The core of the workflow. It defines the sequence of operations.
    * Manages the state of the ingestion process.
    * Calls activity functions to perform specific tasks (getting blob size, splitting into chunks, processing chunks).
    * Handles fan-out/fan-in for parallel chunk processing.
    * Aggregates results and updates the final job status.

3.  **`GetBlobMetadataActivityFunction` (Activity Function):**
    * A simple activity called by the orchestrator.
    * Takes blob name and container as input.
    * Uses `IBlobStorageService` to fetch blob properties (like size) and metadata.
    * Returns this information to the orchestrator.

4.  **`SplitBlobIntoChunksActivityFunction` (Activity Function):**
    * Called by the orchestrator if a file is deemed too large for single processing.
    * **This is the most complex new piece of custom logic.**
    * Input: Original blob name, container, target chunk size, master `UploadCorrelationId`.
    * Logic:
        * Downloads the original large blob as a stream (using `IBlobStorageService`).
        * Reads the stream and splits it into smaller, valid CSV segments (chunks). This requires CSV-aware splitting (i.e., splitting on line breaks, and ensuring each chunk has the header row or is processed accordingly).
        * Uploads each chunk as a new blob to a temporary location (e.g., `chunks-container` or a subfolder) using `IBlobStorageService`. Each chunk blob should have metadata linking it to the master job (e.g., `ParentCorrelationId`, `ChunkNumber`).
    * Output: A list of `ChunkInfo` objects (containing chunk blob name, container, new unique `ChunkCorrelationId`, chunk number) for the orchestrator.

5.  **`ProcessChunkActivityFunction` (Activity Function):**
    * Called by the orchestrator for each chunk (or for the whole file if not chunked).
    * Input: `ChunkInfo` (or similar for a whole file, including its `CorrelationId`).
    * Logic:
        * Injects and uses `ICsvFileProcessingService` (our existing stream-based service).
        * Injects and uses `IJobStatusRepository` to update the status of this specific chunk job (e.g., to `Processing`, then `Succeeded`/`Failed`).
        * Calls `_csvFileProcessingService.ProcessAndStoreCdrFileFromBlobAsync()` with the chunk's blob stream.
    * Output: `FileProcessingResult` (counts of successful/failed records for that chunk) back to the orchestrator.

### Existing Services Leveraged

Our existing services will be injected into these Durable Functions (primarily into Activity Functions):

* **`IBlobStorageService`**: Used by `GetBlobMetadataActivityFunction` to get blob properties and by `SplitBlobIntoChunksActivityFunction` to download the large blob and upload chunk blobs.
* **`IJobStatusRepository`**: Used by the Orchestrator to create/update master job status and chunk job statuses. Also used by `ProcessChunkActivityFunction` to update the status of the chunk it's processing. Our existing repository with chunking support (`IncrementProcessedChunkCountAsync`, `UpdateMasterJobStatusBasedOnChunksAsync`) is crucial here.
* **`ICsvFileProcessingService`**: The core data processing logic. Injected into `ProcessChunkActivityFunction` to process the stream from a chunk blob. Its existing stream-based implementation is ideal.
* **`IFailedCdrRecordRepository`**: Used indirectly by `ICsvFileProcessingService` to store failed rows.
* **`ICdrRepository`**: Used indirectly by `ICsvFileProcessingService` to store successful CDRs.
* **`IBackgroundJobClient` (Hangfire - Not directly used by Durable Functions for core processing):** While our current solution uses Hangfire, this Durable Functions approach *replaces* the Hangfire job (`CdrFileProcessingJobs`) and the Azure Function that enqueues it (`EnqueueCdrProcessingJob`) for the large file processing path. The API would still create the initial `JobStatus`, but instead of triggering a Hangfire-enqueuing function, it (or a blob trigger) would start the Durable Function orchestrator.

---

## 3. Detailed Workflow

1.  **File Upload & Initial API Action:**
    * Client `POST`s large CSV to `/api/cdr/upload-large`.
    * `TelecomCdr.Api` uploads the file to a "staging" or "raw-uploads" Azure Blob Storage container.
    * `TelecomCdr.Api` creates an initial `JobStatus` record in our database:
        * `CorrelationId`: A new unique ID for this entire upload (Master Correlation ID).
        * `OriginalFileName`: Name of the uploaded file.
        * `BlobName`, `ContainerName`: Location of the raw uploaded file.
        * `Status`: `ProcessingStatus.Accepted`.
        * `Type`: `JobType.Master` (anticipating it might be chunked) or `JobType.SingleFile` (if a preliminary size check is done here, though less likely).
    * The API response includes the Master `CorrelationId`.

2.  **Durable Workflow Initiation (`BlobTriggerStarterFunction`):**
    * The `BlobTriggerStarterFunction` is triggered by the new blob in the "raw-uploads" container.
    * It extracts the blob name, container, and the `UploadCorrelationId` (Master Correlation ID) from the blob's metadata (ensure the API sets this metadata during upload).
    * It starts a new instance of the `CsvProcessingOrchestratorFunction`, passing the Master `CorrelationId` and blob details.

3.  **Orchestration (`CsvProcessingOrchestratorFunction`):**
    * **Instance Start:** Receives Master `CorrelationId` and blob details.
    * **Update Master Status:** Updates the Master `JobStatus` to `ProcessingStatus.Processing` (or `Chunking` if it proceeds to chunk).
    * **Get Blob Metadata:** Calls `GetBlobMetadataActivityFunction` to get the blob's size.
    * **Chunking Decision:**
        * If `blobSize` > `CHUNK_THRESHOLD_BYTES`:
            * Updates Master `JobStatus` to `ProcessingStatus.Chunking`.
            * Calls `SplitBlobIntoChunksActivityFunction`, passing the original blob details and Master `CorrelationId`. This activity returns a list of `ChunkInfo` objects (each with a new unique `ChunkCorrelationId`, `ChunkBlobName`, etc.).
            * If splitting fails, updates Master `JobStatus` to `Failed` and exits.
            * Updates Master `JobStatus` with `TotalChunks` count and sets status to `ProcessingStatus.ChunksQueued`.
            * Creates a list of tasks to call `ProcessChunkActivityFunction` for each chunk.
        * If `blobSize` <= `CHUNK_THRESHOLD_BYTES`:
            * Treats the file as a single "chunk." The Master `CorrelationId` also serves as the "chunk's" `CorrelationId` for processing.
            * Updates Master `JobStatus` (acting as a single file job) to `ProcessingStatus.QueuedForProcessing`.
            * Creates a single task to call `ProcessChunkActivityFunction` for the whole file.
    * **Fan-Out (Parallel Chunk Processing):**
        * For each chunk (or the single file), the orchestrator:
            * (If chunking was done) Creates a `JobStatus` record for the chunk:
                * `CorrelationId`: `chunkInfo.ChunkCorrelationId`.
                * `ParentCorrelationId`: Master `CorrelationId`.
                * `Type`: `JobType.Chunk`.
                * `Status`: `ProcessingStatus.QueuedForProcessing`.
                * `BlobName`: `chunkInfo.ChunkBlobName`.
            * Calls `ProcessChunkActivityFunction` with the chunk's details (including its `CorrelationId`).
    * **Fan-In (Wait for Completion):**
        * The orchestrator waits for all `ProcessChunkActivityFunction` tasks to complete (`Task.WhenAll(processingTasks)`).
    * **Aggregate Results & Finalize Master Status:**
        * Collects `FileProcessingResult` from each completed activity.
        * The `ProcessChunkActivityFunction` itself would have updated its own chunk's `JobStatus` to `Succeeded`/`Failed` and called `IJobStatusRepository.IncrementProcessedChunkCountAsync` on the master job.
        * The orchestrator calls `IJobStatusRepository.UpdateMasterJobStatusBasedOnChunksAsync(masterCorrelationId)` one final time to ensure the master status is correctly set based on all chunk outcomes (this method in the repository sums up record counts and determines overall `Succeeded`, `PartiallySucceeded`, or `Failed` for the master job).
    * **Cleanup (Optional):** Calls an activity function to delete the original large blob and/or temporary chunk blobs if processing was successful.

4.  **Chunk Processing (`ProcessChunkActivityFunction`):**
    * Receives `chunkCorrelationId` and chunk blob details.
    * Updates its own `JobStatus` (for this chunk) to `ProcessingStatus.Processing` via `IJobStatusRepository`.
    * Uses `IBlobStorageService` to get a stream for the chunk blob.
    * Uses `ICsvFileProcessingService` to process the stream. This service handles row-level errors, saves successful records (`ICdrRepository`), and saves failed rows (`IFailedCdrRecordRepository`). It returns a `FileProcessingResult`.
    * Updates its own chunk `JobStatus` to `Succeeded` or `Failed` based on the `FileProcessingResult`, including `ProcessedRecordsCount` and `FailedRecordsCount` for that chunk.
    * Calls `_jobStatusRepository.IncrementProcessedChunkCountAsync(parentCorrelationId, chunkSucceeded)` to notify the master job about its completion.
    * Returns the `FileProcessingResult` to the orchestrator.

---

## 4. Status Tracking

* **Master `JobStatus` Record:** Tracks the overall progress of the original large file upload. Its `Status` progresses through `Accepted`, `Chunking` (if applicable), `ChunksQueued` (if applicable), and finally `Succeeded`, `PartiallySucceeded`, or `Failed`. It will also store aggregated `TotalChunks`, `ProcessedChunks`, `SuccessfulChunks`, `FailedChunks`, and the grand total of `ProcessedRecordsCount` and `FailedRecordsCount` from all chunks.
* **Chunk `JobStatus` Records:** If chunking occurs, each chunk gets its own `JobStatus` record, linked to the master via `ParentCorrelationId`. These track the individual chunk's progress (`QueuedForProcessing`, `Processing`, `Succeeded`, `Failed`) and its specific record counts.
* The API endpoint `GET /api/jobstatus/{correlationId}` can be used to query the status of either a master job or an individual chunk job. If querying a master job, the response will show its overall status and aggregated counts.

---

## 5. Project Structure Considerations

* **`TelecomCdr.DurableFunctions` Project:**
    * Will host all Durable Functions (Starter, Orchestrator, Activities).
    * Needs project references to:
        * `TelecomCdr.Core` (for interfaces like `IBlobStorageService`, `IJobStatusRepository`, `ICsvFileProcessingService`, and DTOs like `FileProcessingResult`, `ChunkInfo`).
        * `TelecomCdr.Domain` (for entities like `JobStatus`, `CallDetailRecord`, `FailedCdrRecord`).
        * Potentially `TelecomCdr.Infrastructure` if concrete service implementations are directly injected (though injecting interfaces is preferred).
* **`TelecomCdr.Abstraction` Project:**
    * May define new interfaces like `ISplitStrategy` if CSV splitting logic becomes very complex and needs to be abstracted.
* **`TelecomCdr.Infrastructure` Project:**
    * `CsvFileProcessingService` is heavily reused.
    * `MssqlJobStatusRepository` needs to be robust in handling master/chunk status updates.