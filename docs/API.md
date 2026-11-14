# API reference

Base URL: the deployed API (Container Apps HTTPS ingress), e.g.
`https://ca-orch-api-<suffix>.<region>.azurecontainerapps.io`

All bodies are JSON. Enums serialize as **strings**
(`Draft`, `Validation`, `SecurityApproval`, `ReviewTeamApproval`, `PipelineRunning`,
`CompletedSuccess`, `CompletedFailed`, `Decommissioned`).

## REST endpoints

### `GET /healthz`
Liveness probe. `200 OK` with "Healthy".

### `POST /api/deployments` — StartDeploymentCommand (phase 1)
```json
{
  "requestedBy": "user@contoso.com",
  "subscription": "sub-payments",
  "state": "dev",
  "resourceType": "WebApp",        // optional — inferred when omitted
  "resourceName": "payments api",
  "fields": { "app_name": "app-pay" }   // optional extra fields
}
```
Response `200`: full `DeploymentRequestResponse` (id, workItemId, status, missingFields,
pipelineId, terraformScriptFolder, ...).

Behavior: runs validation immediately; if required fields are missing the request stays in
`Draft` with `missingFields` populated (chat UI asks for them); otherwise moves to
`SecurityApproval`.

### `POST /api/deployments/{id}/inputs` — SubmitInputsCommand (phase 3)
```json
{ "fields": { "app_name": "app-payments-01", "runtime": "dotnet" } }
```
Merges values and re-validates (the "Merge values & validate" step). `400` on empty values,
`404` unknown id.

### `POST /api/deployments/{id}/approve` — approval gates (phase 2)
```json
{ "approvedBy": "security@contoso.com", "comment": "ok" }
```
- On `SecurityApproval` → records security approval, moves to `ReviewTeamApproval`
- On `ReviewTeamApproval` → records review approval and **triggers the ADO pipeline**
  (phase 4) → `PipelineRunning` with `pipelineRunId`
- `409` when the request is not awaiting approval

### `GET /api/deployments/{id}`
Current state of one request.

### `GET /api/deployments`
Last 100 requests (newest first).

### `GET /api/deployments/{id}/audit`
Complete audit & history:
```json
[
  { "requestId": "...", "action": "start", "detail": "Deployment requested by ...",
    "actor": "orchestrator", "timestampUtc": "2026-11-14T19:25:53+00:00" },
  { "action": "pipeline-queued", "detail": "ADO pipeline 106, run 1001", ... },
  { "action": "completed", "detail": "Result: CompletedSuccess. ...", ... }
]
```
Audit actions: `start`, `infer`, `validation-passed`, `validation-failed`,
`missing-inputs`, `inputs-submitted`, `security-approved`, `review-approved`,
`pipeline-queued`, `completed`.

## SignalR hub — `/hubs/deployment`

Client methods:
- `Subscribe(requestId)` / `Unsubscribe(requestId)` — join/leave the per-request group

Server events (pushed live by the workflow engine + monitoring worker):
- **`statusUpdated`** →
```json
{
  "requestId": "22added1-...",
  "status": "PipelineRunning",
  "message": "Run 1001: inProgress",
  "timestampUtc": "2026-11-14T19:26:41+00:00"
}
```

JavaScript client:
```js
const conn = new signalR.HubConnectionBuilder()
  .withUrl("https://<api>/hubs/deployment").withAutomaticReconnect().build();
conn.on("statusUpdated", u => console.log(u.status, u.message));
await conn.start();
await conn.invoke("Subscribe", requestId);
```

## Configuration (env / appsettings)

| Key | Meaning | Default |
|---|---|---|
| `ConnectionStrings:Sql` | Azure SQL conn string; empty ⇒ EF InMemory | "" |
| `Ado:Organization` / `Ado:Project` / `Ado:Pat` | ADO REST integration; empty ⇒ simulated pipeline client | "" |
| `Monitoring:IntervalSeconds` | poll interval | 15 |
| `Monitoring:PollTimeoutMinutes` | run timeout | 40 |
| `Cors:Origins:0..n` | allowed browser origins | localhost:5173 |
| `Azure:SignalR:ConnectionString` | routes hub traffic through Azure SignalR Service | — |

## curl walkthrough

```bash
API=https://<api-url>
ID=$(curl -s -X POST $API/api/deployments -H 'Content-Type: application/json' \
  -d '{"requestedBy":"me@contoso.com","subscription":"sub-x","state":"dev",
       "resourceType":"StorageAccount","resourceName":"saccount-demo",
       "fields":{"resource_group":"rg-demo","storage_name":"sademo01"}}' \
  | jq -r .id)

curl -s -X POST $API/api/deployments/$ID/approve \
  -H 'Content-Type: application/json' -d '{"approvedBy":"sec@contoso.com"}'
curl -s -X POST $API/api/deployments/$ID/approve \
  -H 'Content-Type: application/json' -d '{"approvedBy":"lead@contoso.com"}'
# watch status:
curl -s $API/api/deployments/$ID | jq .status
curl -s $API/api/deployments/$ID/audit | jq
```
