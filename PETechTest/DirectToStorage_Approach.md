# Direct-to-Storage Upload for Large Files


## Client-Side Workflow:


This document outlines the typical steps a client application would follow to upload a large file directly to Azure Blob Storage, orchestrated by our TelecomCdr API. This pattern is recommended for files of 1GB or more to ensure reliability and scalability.

**Assumptions:**
* Our Test API base URL: `https://localhost:7100`
* The client has the file to upload (e.g., `my_large_cdr_file.csv`).
* Our API should be secured (e.g., using Bearer tokens for authorization), in this case we have not set any sort of authorization

---

### Step 1: Client Initiates Upload Request to Our API

The client first informs our API about its intent to upload a file. This request includes metadata about the file, such as its name and content type.

* **Action:** Client sends an HTTP `POST` request.
* **Endpoint:** `/api/cdr/initiate-direct-upload`
* **Headers:**
    * `Content-Type: application/json`
    * `Authorization: Bearer <auth_token>` **(Not applicable for the PETest)**
* **Request Body:**
    ```json
    {
      "fileName": "my_large_cdr_file.csv",
      "contentType": "text/csv",
      "fileSize": 1073741824 // Optional: File size in bytes (e.g., 1GB)
    }
    ```

**Sample `curl` Example:**
```bash
curl -X POST "[https://https://localhost:7100/api/cdr/initiate-direct-upload](https://https://localhost:7100/api/cdr/initiate-direct-upload)" \
-H "Content-Type: application/json" \
-H "Authorization: Bearer <auth_token>" \ 
-d '{
      "fileName": "my_large_cdr_file.csv",
      "contentType": "text/csv"
    }'
```
### Step 2: Our API Responds with SAS URI and Job Details

Our API's `InitiateDirectUploadCommandHandler` processes the request. It generates a Shared Access Signature (SAS) URI for Azure Blob Storage, and returns these details to the client.
Expected API Response: 200 OKResponse 
```json
Body:{
  "uploadUrl": "[https://yourstorageaccount.blob.core.windows.net/our-actual-upload-container-name/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/my_large_cdr_file.csv?sv=](https://yourstorageaccount.blob.core.windows.net/our-actual-upload-container-name/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/my_large_cdr_file.csv?sv=)...",
  "blobName": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/my_large_cdr_file.csv",
  "containerName": "our-actual-upload-container-name",
  "jobId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "expiresOn": "2025-05-18T10:30:00.1234567Z" // Example expiry time, for this app we are configuring the expiry to be 30 mins
}
```

* `UploadUrl`: The pre-signed SAS URL the client will use for the direct upload.
* `jobId`: The unique identifier for this upload job, which the client can use later to track the processing status.

### Step 3: Client Uploads File Directly to Azure Blob Storage. 
The client now uses the uploadUrl received from our API to upload the actual file content directly to Azure Blob Storage. This request bypasses our API server for the data transfer.

* `Action`: Client sends an HTTP PUT request.
* `Endpoint`: The uploadUrl received in Step 2.
* `Headers` (Crucial):`x-ms-blob-type: BlockBlob` (This header is required by Azure Blob Storage when uploading using a SAS URI for a block blob).
* `Content-Type`: text/csv (Should match the contentType provided in Step 1 and/or what the SAS token might be configured to expect).
* `Content-Length`: <size_of_file_in_bytes> (The actual size of the file being uploaded).
* `Request Body`: The raw binary content of the file (e.g., my_large_cdr_file.csv).
Conceptual curl Example (for uploading a local file):
#### Ensure UPLOAD_URL is the full URL from the API response in Step 2, including the SAS token
UPLOAD_URL="[https://yourstorageaccount.blob.core.windows.net/our-container/jobId/my_large_cdr_file.csv?sastoken](https://yourstorageaccount.blob.core.windows.net/our-container/jobId/my_large_cdr_file.csv?sastoken)..."
```bash
curl -X PUT "$UPLOAD_URL" \
-H "x-ms-blob-type: BlockBlob" \
-H "Content-Type: text/csv" \
--data-binary "@path/to/local/my_large_cdr_file.csv"
##Ensure all required headers (x-ms-blob-type, Content-Type) are correctly set.
```
### Step 4: Azure Blob Storage Responds to ClientAzure Blob Storage processes the PUT request from the client.
Successful Upload Response (from Azure Blob Storage directly to the client): `201 Created` 
,Failed Upload Response: An appropriate HTTP error code (e.g., 403 Forbidden if the SAS token is invalid, expired, or permissions are insufficient; 400 Bad Request for missing required headers like x-ms-blob-type, etc.). 

### Step 5: Backend Processing Triggered (Server-Side Eventing)

Azure Events is the recommended way to initiate backend processing once the file is in Blob Storage.
* `Event Emission`: Azure Blob Storage emits a Microsoft.Storage.BlobCreated event automatically when the client's PUT operation in Step 3 completes successfully.
* `Event Subscription`: We configure an Azure Event Grid subscription to listen for these events on our target storage container. 
This subscription routes the event to a handler, typically an Azure Function.
* `Azure Function Handler`:The Azure Function is triggered by the BlobCreated event.
The event data includes details like the `containerName` and `blobName` (which contains the jobId as part of its path, e.g., jobId/filename.csv).
 
  The Function extracts these details and then typically dispatches a new MediatR command (e.g., ProcessBlobCommand { JobId, BlobName, ContainerName, OriginalFileName }) to our backend API's Core layer.

  This command handler then updates the job status (e.g., from "PendingUpload" to "Queued" or "Processing") and initiates the actual file processing workflow (e.g., starting a Durable Functions orchestration).

`This server-side eventing approach is more reliable than relying on the client to make an additional notification call.`

### Step 6: Client Polls for Job Status (Optional)
The client can use the jobId obtained in Step 2 to periodically check the status of the file processing via an endpoint on our API.

* Action: Client sends an HTTP GET request.Endpoint: /api/jobstatus/{jobId} (Assuming you have a JobStatusController with such an endpoint).
* Headers:Authorization: Bearer <auth_token>
* Expected API Response :
```JSON
{
  "jobId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "status": "Processing", // Example states: PendingUpload, Queued, Processing, Validating, Chunking, Completed, Failed
  "message": "File is currently being processed. Validation complete, starting chunking.",
  "fileName": "my_large_cdr_file.csv",
  "createdDate": "2025-05-18T10:00:00Z",
  "lastUpdated": "2025-05-18T10:35:00Z",
  "processedCount": null, // Or number of records processed so far
  "failedCount": null    // Or number of records failed so far
}
```

The client would continue polling at reasonable intervals (e.g., with exponential backoff) until the status indicates a terminal state like "Completed" or "Failed".


# Triggering Backend Processing After Direct Blob Upload

The document outlines how we would automatically trigger our backend processing pipeline once a large file has been successfully uploaded directly to Azure Blob Storage using the SAS URI pattern. The recommended approach leverages Azure Event Grid and Azure Functions.

### Overall Flow

1.  **Client Uploads File:** The client application uses the SAS URI (obtained from our API's `/initiate-direct-upload` endpoint) to upload the file directly to a designated Azure Blob Storage container. The blob name is structured to include the `JobId` (e.g., `{JobId}/{originalFileName.csv}`).
2.  **Blob Created Event:** Upon successful upload, Azure Blob Storage emits a `Microsoft.Storage.BlobCreated` event.
3.  **Event Grid Subscription:** An Azure Event Grid subscription is configured for our storage account (or specifically, the upload container). This subscription filters for `BlobCreated` events and routes them to an endpoint.
4.  **Azure Function Trigger:** The endpoint for the Event Grid subscription is an Azure Function with an Event Grid trigger. This function is designed to handle the incoming event.
5.  **Event Processing in Function:**
    * The Azure Function receives the event data, which includes details about the newly created blob (e.g., `url`, `contentType`, `contentLength`).
    * It parses the blob URL or name to extract the `JobId` and the `originalFileName`.
    * It might perform initial validation or logging.
6.  **Dispatching Processing Command:** The Azure Function creates a MediatR command (e.g., `ProcessBlobCommand`) containing the `JobId`, `BlobName`, `ContainerName`, and any other relevant metadata.
7.  **MediatR Command Handler:**
    * This handler, typically located in our Core business logic layer, receives the `ProcessBlobCommand`.
    * It updates the `JobStatus` (e.g., from "PendingUpload" to "Queued" or "ProcessingInitiated").
    * It then initiates the actual backend processing, such as starting a Durable Functions orchestration (e.g., `BlobProcessingOrchestrator`) or enqueueing a job in Hangfire. The `JobId`, `BlobName`, and `ContainerName` are passed to this processing pipeline.

### Azure Setup

1.  **Storage Account & Container:**
    * Ensure we have an Azure Storage Account and a specific container designated for these direct uploads (e.g., `direct-cdr-uploads`). This is the container name configured in our API's `FileUploadSettings`.

2.  **Azure Function App:**
    * We'll need an Azure Function App (e.g., the one hosting our `TelecomCdr.DurableFunctions`/`TelecomCdr.AzureFunctions` or a dedicated one for event handling).
    * The function within this app will have an Event Grid trigger.

3.  **Event Grid Subscription:**
    * In the Azure portal, navigate to our Storage Account.
    * Go to "Events" under the "Data storage" section.
    * Click "+ Event Subscription".
    * **Basics Tab:**
        * **Name:** Give the subscription a descriptive name (e.g., `CdrBlobUploadTrigger`).
        * **Event Schema:** Choose "Event Grid Schema".
        * **Topic Type:** "Storage Accounts".
        * **Subscription:** Your Azure subscription.
        * **Resource Group:** Resource group of the storage account.
        * **Resource:** Our specific storage account.
        * **System Topic Name:** (Optional) Can be auto-generated or specified.
    * **Filters Tab:**
        * Enable "Subject filtering" (Recommended).
            * **Subject Begins With:** `/blobServices/default/containers/cdr-direct-uploads/` (Replace `cdr-direct-uploads` with the container we are monitoring. This ensures the function only triggers for blobs in this specific container).
            * **Subject Ends With:** (Optional) We could filter by file extension if needed (e.g., `.csv`).
        * **Event Types:** Filter to only include `Blob Created` (`Microsoft.Storage.BlobCreated`).
    * **Endpoint Details Tab:**
        * **Endpoint Type:** Select "Azure Function".
        * **Endpoint:** Click "Select an endpoint" and choose the Azure Function App and the specific Event Grid triggered function you will create (e.g., `BlobUploadProcessorFunction`).
    * **Additional Features Tab:** (Configure retries, dead-lettering as needed for production robustness).
    * Click "Create".

### Benefits of this Approach

* **Decoupling:** The upload process is decoupled from the backend processing. Your API is not burdened with handling the file stream directly.
* **Scalability:** Azure Functions and Event Grid are highly scalable services, capable of handling many concurrent uploads and events.
* **Reliability:** Event Grid provides retry mechanisms and dead-lettering for event delivery, enhancing the reliability of triggering your backend.
* **Event-Driven:** Promotes a responsive, event-driven architecture.

### Security Considerations

* The Azure Function triggered by Event Grid should ideally have appropriate authentication/authorization if it's an HTTP trigger (though Event Grid triggers often use a webhook mechanism with a key or Azure AD authentication for Event Grid).
* Ensure the managed identity of the Azure Function App (if used) has the necessary permissions if it needs to interact with other Azure resources (e.g., to read from the blob if it doesn't just pass metadata, or to write to a database).

This event-driven mechanism provides a robust and scalable way to kick off the complex file processing workflows as soon as files land in our designated storage container.
