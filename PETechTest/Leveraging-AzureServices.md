# Azure Service Alternatives for Large CSV Ingestion

Some other robust Azure services that can handle large-scale data ingestion more efficiently than a general-purpose background job system like Hangfire when dealing with multi-GB files and millions of records.

## Table of Contents

1.  [Azure Data Factory (ADF) + Azure Batch](#1-azure-data-factory-adf--azure-batch)
    * [Workflow](#workflow-adf--batch)
    * [Status Tracking with ADF + Batch](#status-tracking-with-adf--batch)
    * [Pros](#pros-adf--batch)
    * [Cons](#cons-adf--batch)
2.  [Durable Functions](#2-durable-functions)
    * [Workflow](#workflow-durable-functions)
    * [Status Tracking with Durable Functions](#status-tracking-with-durable-functions)
    * [Pros](#pros-durable-functions)
    * [Cons](#cons-durable-functions)
3.  [High-Level Cost Considerations (for 100GB/month, millions of records)](#3-high-level-cost-considerations-for-100gbmonth-millions-of-records)
    * [Common Cost Components (Both Solutions)](#common-cost-components-both-solutions)
    * [Specific Costs for ADF + Azure Batch](#specific-costs-for-adf--azure-batch)
    * [Specific Costs for Durable Functions](#specific-costs-for-durable-functions)
    * [Estimating and Optimization](#estimating-and-optimization)
4.  [Recommendation](#recommendation)

---

## 1. Azure Data Factory (ADF) + Azure Batch

This is a powerful combination for orchestrating and executing large-scale data processing tasks.

* **Azure Data Factory (ADF):** A cloud-based ETL and data integration service that allows us to create, schedule, and manage data pipelines.
* **Azure Batch:** Enables us to run large-scale parallel and high-performance computing (HPC) batch jobs efficiently. We can run custom executables (like a .NET console app derived from our existing processing logic) on a managed pool of virtual machines.

### Workflow (ADF + Batch)

1.  **API Upload & Initial Status:**
    * Our `POST /api/cdr/upload-large` endpoint receives the file.
    * It uploads the file to a designated Azure Blob Storage container (e.g., `raw-uploads`).
    * Crucially, it creates an initial record in the `JobStatuses` table with the `CorrelationId`, `OriginalFileName`, and status `Accepted` or `PendingOrchestration`.
    * The API then triggers an Azure Data Factory pipeline, passing the `CorrelationId`, blob name, and container name as parameters. This can be done via the ADF SDK, REST API, or even a Logic App listening to the API response.

2.  **ADF Pipeline Orchestration:**
    * **Trigger:** The ADF pipeline can also be triggered directly by blob creation events using an Event Grid trigger for a fully event-driven approach.
    * **File Validation & Pre-processing (Optional):** ADF can perform initial validation or move the file.
    * **Chunking (Handled by Azure Batch Task or ADF):**
        * **Option A (Batch handles chunking):** Our custom application run by Azure Batch can be designed to read the large blob in chunks (e.g., by byte range if the file format allows, or by splitting it logically if it's a simple line-based format like CSV).
        * **Option B (ADF pre-chunks):** ADF could potentially use activities to split the large blob into smaller chunk blobs first.
    * **Azure Batch Activity:** ADF invokes an Azure Batch job.
        * **Batch Pool:** A pool of VMs (Windows or Linux) is provisioned (can be auto-scaling).
        * **Batch Job & Tasks:** A job is created, and one or more tasks are submitted.
            * If not chunked by ADF, a single task might process the whole file (relying on Our C# app's streaming).
            * If chunked, multiple tasks can run in parallel, each processing a chunk.
            * **Application Package:** Our `CsvFileProcessingService` logic (and its dependencies) would be packaged into a .NET console application, uploaded to Azure Batch as an application package. This console app would take parameters like blob name, container, `CorrelationId`, and chunk definition (if applicable). It would perform the CSV parsing, validation, writing successful records to `CallDetailRecords`, and failed records to `FailedCdrRecords`.
    * **Status Updates from Batch Task:** The console application running in Azure Batch should:
        * Log extensively.
        * Upon completion (success, partial success, or failure of its assigned file/chunk), it needs to communicate its status. This can be done by:
            * Writing status and record counts to a specific blob that ADF can read.
            * Calling an Azure Function (with an HTTP trigger) to update our `JobStatuses` table directly (passing `CorrelationId`, counts, and status).
            * Writing to an Azure Storage Queue (similar to our current setup, but initiated by the Batch task).
    * **ADF Post-processing:** After the Batch activity, ADF can handle cleanup, notifications, or further data movements. It can also update a master `JobStatus` record based on the outcome of the Batch job.

### Status Tracking with ADF + Batch

* The API creates the initial `JobStatus` record.
* The `CorrelationId` is passed through the entire process.
* The .NET application running in Azure Batch is responsible for the detailed processing status of its file/chunk. It updates our `JobStatuses` table (directly or indirectly via another service like a Function or Queue) with `Processing`, `Succeeded`, `Failed`, `PartiallySucceeded`, and the record counts.
* ADF provides its own run monitoring, which can be used for operational insights but might need translation for user-facing status.
* Our `GET /api/jobstatus/{correlationId}` endpoint continues to query the `JobStatuses` table.

### Pros (ADF + Batch)

* Highly scalable and designed for large batch workloads.
* Azure Batch provides robust VM management, auto-scaling, and task scheduling.
* ADF offers powerful orchestration, scheduling, monitoring, and integration capabilities.
* Can reuse much of our existing C# data processing logic in the Batch application.

### Cons (ADF + Batch)

* More Azure services to manage.
* Can be more complex to set up the initial Batch pools, application packages, and ADF pipelines.
* Status update from the Batch task back to our application's `JobStatuses` table requires a well-defined mechanism.

---

## 2. Durable Functions

This is a good serverless option for orchestrating stateful workflows, including fanning out for parallel processing of chunks.

### Workflow (Durable Functions)

1.  **API Upload & Initial Status:** Same as above – API uploads blob and creates initial `JobStatus`.
2.  **Starter Function & Orchestrator:**
    * Blob creation triggers a "starter" Azure Function.
    * This starter function initiates a Durable Function orchestrator, passing the `CorrelationId`, blob name, etc.
3.  **Durable Orchestrator Function:**
    * Updates `JobStatus` to `Processing` or `Chunking`.
    * **File Size Check & Chunking Logic:** Calls an "activity" function to get blob size. If large, it calls another activity function to determine chunk boundaries (e.g., byte ranges or by splitting the blob into smaller temporary blobs – CSV-aware splitting is still a custom logic challenge).
    * **Fan-Out:** For each chunk, the orchestrator calls an "activity" function in parallel. This activity function would contain our `CsvFileProcessingService` logic to process that specific chunk. It would take the chunk definition and `CorrelationId` (or a new chunk-specific ID linked to the master `CorrelationId`) as input.
    * **Activity Function (Chunk Processing):** Processes its assigned chunk, writes to `CallDetailRecords` and `FailedCdrRecords`. It returns a `FileProcessingResult` (success/failure counts for that chunk).
    * **Fan-In & Aggregation:** The orchestrator waits for all chunk processing activities to complete. It then aggregates their results (total successful records, total failed records).
    * **Final Status Update:** The orchestrator updates the master `JobStatus` record to `Succeeded`, `PartiallySucceeded`, or `Failed` with aggregated counts and messages.

### Status Tracking with Durable Functions

* The API creates the initial `JobStatus` record.
* The Durable Orchestrator function is responsible for updating the `JobStatus` record at key stages (start, chunking, chunk completion, final aggregation).
* Durable Functions have their own instance ID and status query APIs. Our API could expose an endpoint that translates these or directly queries our `JobStatuses` table.

### Pros (Durable Functions)

* Serverless, scales automatically.
* Good for stateful, complex orchestrations.
* Built-in support for patterns like fan-out/fan-in, retries for activities.
* Can reuse C# processing logic in activity functions.

### Cons (Durable Functions)

* CSV-aware splitting logic within an activity function still needs to be robustly implemented.
* Managing state and potential costs for very long-running orchestrations needs consideration.
* Debugging distributed orchestrations can be more complex.

---

## 3. High-Level Cost Considerations (for 100GB/month, millions of records)

Estimating exact costs for cloud services is complex and depends heavily on usage patterns, chosen service tiers, Azure region, data retention policies, and any negotiated discounts. The following outlines the primary cost drivers for processing approximately 100GB of CSV data containing hundreds of millions of records each month. **We should Use the Azure Pricing Calculator for detailed estimates tailored to our configuration.**

### Common Cost Components (Both Solutions)

* **Azure Blob Storage:**
    * **Storage Capacity:** Storing 100GB of raw uploaded files per month. If files are chunked, this might temporarily increase storage until original large files are deleted. Consider storage tiers (Hot, Cool) based on access patterns.
    * **Operations:** Costs for write operations (uploads, chunk creation) and read operations (downloads by processing tasks). Millions of records imply many small writes to `CallDetailRecords` and `FailedCdrRecords` tables, but the blob operations are for larger files/chunks.
    * **Data Transfer:** Ingress to Blob Storage is generally free. Egress might occur if data is moved out of Azure or between regions.
* **SQL Database (e.g., Azure SQL Database):**
`This could be excluded considering we may already have a Production grade sql instance`
    * **Compute Tier (DTUs/vCores):** This will be a significant factor. Processing and inserting hundreds of millions of records will put a heavy load on the database. We'll need a tier that can handle the concurrent writes and subsequent queries.
    * **Storage:** For `CallDetailRecords`, `JobStatuses`, and `FailedCdrRecords`. The size will grow based on the number of records and retention.
    * **Backup Storage.**
* **Application Insights:**
    * **Data Ingestion:** Logging from our API, Azure Functions, ADF, and Batch tasks. High-volume processing will generate a lot of telemetry.
    * **Data Retention.**
* **Networking:**
    * **Data Transfer Out:** If data is egressed from Azure.
    * **Private Endpoints/VNet Integration:** If used for security, these can have associated costs.
* **API Hosting (e.g., Azure App Service):**
    * The service plan for our `TelecomCdr.Api` application.

### Specific Costs for ADF + Azure Batch

* **Azure Data Factory:**
    * **Pipeline Orchestration Runs:** Cost per pipeline run. If we have one pipeline run per 100GB file, this is minor. If it's per smaller file making up the 100GB, it increases.
    * **Activity Runs:** Cost per activity execution (e.g., Copy data, Custom activity for Batch). Duration and type of activity matter.
    * **Data Movement:** If ADF itself is moving large data volumes (e.g., copying chunks), this is charged per GB.
    * **Integration Runtimes:** Usually Azure IR is sufficient and cost is based on activity execution. Self-Hosted IR has different cost implications.
* **Azure Batch:**
    * **Virtual Machine (VM) Instance Hours:** This is likely the **largest cost component** within Batch.
        * **VM SKU:** Choice of VM (CPU, memory, disk type like Standard vs. Premium SSD) significantly impacts cost. CPU-intensive CSV parsing and data transformation will require decent VMs.
        * **Number of VMs:** Determined by our pool size and auto-scaling settings.
        * **Duration:** How long the VMs run to process the 100GB of data.
    * **Storage for Application Packages:** Usually minimal.
    * **Outbound Data Transfer (from Batch VMs):** If our Batch tasks write directly to an external service or a database in another region.

### Specific Costs for Durable Functions

* **Azure Functions (Consumption Plan or Premium Plan):**
    * **Consumption Plan:**
        * **Number of Executions:** For orchestrator, starter, and activity functions. Millions of records processed in chunks mean millions of activity function invocations(considering a factor of 1/10 each activity processes 100k records) if each record/small batch is an activity (less efficient) or fewer, longer-running activities if each processes a larger chunk.
        * **Execution Units (GB-seconds):** Memory consumed multiplied by execution time. Activity functions processing data chunks will be the main driver here.
    * **Premium Plan:** Provides dedicated instances, no cold starts, VNet integration. Cost is based on vCPU-hours and GB-hours of pre-warmed instances, plus a per-execution charge similar to consumption. For very high throughput and long-running tasks, Premium might be more cost-effective or necessary for performance.
* **Azure Storage (for Durable Functions Backend):**
    * Durable Functions use Azure Storage (queues, tables, blobs) for managing state, history, and work-item queues.
    * **Storage Transactions:** With a large number of activities (especially if fanning out to process many small chunks or individual records), the number of storage transactions can become very high and contribute significantly to costs. This is a key area to monitor.
    * **Storage Capacity:** For orchestration history (can grow large for long-running or numerous orchestrations).

### Estimating and Optimization

1.  **Azure Pricing Calculator:** Use this tool extensively. We'll need to estimate:
    * VM hours and types for Batch.
    * Number of function executions and average duration/memory for Durable Functions.
    * Data storage volumes.
    * Database tier.
    * Expected number of ADF pipeline/activity runs.
    * Expected number of queue transactions.
2.  **Proof of Concept (PoC):** Run a scaled-down PoC with representative data and monitor actual consumption and costs in the Azure portal.
3.  **Optimize Processing Logic:**
    * Ensure our C# data processing code (`CsvFileProcessingService`) is highly efficient (minimal allocations, efficient parsing).
    * For Durable Functions, balance chunk size: too small = too many activities and storage transactions; too large = longer activity durations.
4.  **Choose Appropriate Tiers:** Select VM SKUs, Function plans (Consumption vs. Premium), and SQL DB tiers that match our performance needs without overprovisioning.
5.  **Auto-Scaling:** Implement auto-scaling for Azure Batch pools and Azure Functions Premium plans.
6.  **Data Retention:** Configure appropriate retention policies for logs (Application Insights) and Blob Storage (lifecycle management).
7.  **Reserved Instances/Savings Plans:** If we have predictable, sustained workloads, Azure Reservations or Savings Plans can significantly reduce compute costs.
8.  **Monitor Costs:** Also, we should regularly use Azure Cost Management tools to track spending and identify areas for optimization.

Processing 100GB of data with hundreds of millions of records monthly is a substantial workload. **Azure Batch (with ADF orchestration) is often more cost-effective for very large, CPU-intensive, long-running batch tasks due to more predictable VM-based pricing for the core compute.** Durable Functions can be very effective, but for this scale, we'd need to carefully manage activity granularity to control storage transaction costs and execution costs, potentially favoring a Premium plan.

---

## 4. Recommendation

For our scenario, if we prefer to stay within a more C#-centric and potentially serverless model, **Durable Functions** offer a very compelling way to handle the chunking and parallel processing.

If Giacom already uses **Azure Data Factory** or we anticipate more complex ETL pipelines in the future, ADF + Azure Batch is an industry-standard and highly scalable solution. We can package our existing C# stream processing logic into a console app for Batch.

Both approaches allow our API to initiate the process and track status via our `JobStatuses` table, provided the chosen Azure service (or the custom code running within it) is designed to update this table. The key is to pass the `CorrelationId` through the entire workflow.
