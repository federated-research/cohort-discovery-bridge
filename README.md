# Cohort Discovery Bridge

The Bridge connects the Cohort Discovery Tool with Five Safes TES (5STES). It retrieves availability jobs from the Cohort Discovery Tool, submits them for execution through TES and returns the results.

## How polling works

- `Bridge:Collections` contains the collection IDs served by this Bridge instance.
- A separate polling loop is started for each configured collection.
- All polling loops share the same `ICohortDiscoveryClient` / `HttpClient` adapter.
- Retrieved jobs are written to a bounded in-memory channel along with their originating collection ID.
- `Bridge:MaxConcurrentJobs` controls the number of processing workers.
- The original collection ID is used for both `JobResult.CollectionId` and the Task API result submission URL.

## Configuration

### appsettings.json

Example configuration:

```json
{
  "Bridge": {
    "Collections": [
      "collection-a-id",
      "collection-b-id"
    ],
    "PollIntervalSeconds": 10,
    "MaxConcurrentJobs": 2,
    "CohortDiscoveryBaseUrl": "http://localhost:8190/link_connector_api",
    "Username": "user",
    "Password": "password",
    "MockCount": 120
  }
}
```

For local execution, configure Relay credentials and the TES token using .NET user secrets

### Docker Compose environment

The Docker Compose configuration expects the following `.env` values:


The service names and URLs must match the Docker Compose network and service names used by your deployment.

If the Bridge runs directly on the host rather than inside Docker, use the host-published Relay and TES URLs instead.

## Current processing limitations

This implementation focuses on multi-collection Relay polling and correct result routing.

The current worker still:

- Submits the existing sample TES payload.

### In-memory job queue

The job queue is held in memory. Jobs already retrieved from Task API but not yet processed can be lost if the Bridge process stops unexpectedly.

Durable job recovery and idempotency are not currently implemented in this proof of concept.

## Running locally

From the Bridge project directory, run:

```bash
dotnet restore
dotnet run --project CofindBridge.csproj
```

## Health check

The Bridge exposes a health endpoint at:

`http://localhost:8080/health`

A successful response returns HTTP `200 OK`:

```json
{
  "status": "Healthy"
}
```