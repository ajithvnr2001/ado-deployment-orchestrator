# Architecture

## 1. Overview

The platform implements the sequence workflow from the reference diagram:

```
User ──> Chat Deployment Controller (React+TS)
              │ HTTPS + SignalR websocket
              ▼
      Orchestration Agent (.NET 10, Container Apps)
              │                    │                      │
              ▼                    ▼                      ▼
      Resource Mapping      Azure DevOps           Monitoring Background
      Service (rules/       (YAML pipelines        Service (poll 15s /
      AI inference)         + Terraform)           40min, push via SignalR)
              │                    │                      │
              └──── Audit trail ───┴──── Azure SQL ──────┘
                     (every decision persisted)
```

## 2. Components

### 2.1 Chat Deployment Controller (frontend, React 19 + TypeScript)
- Sends natural-language deployment requests ("create a new web app for the payments team").
- Joins a SignalR group per request (`/hubs/deployment`, group = request id) and renders
  live status events pushed by the monitoring service.
- Guides the user through missing inputs (`key=value` chat format) and approvals.
- Served from the Storage static website (`$web` container), CORS-enabled against the API.

### 2.2 Orchestration Agent (backend, .NET 10)
Single deployable containing:

| Piece | File | Responsibility |
|---|---|---|
| Workflow engine | `Services/DeploymentOrchestrator.cs` | Implements all 5 phases: validation, inference, approvals, trigger, completion |
| Resource mapping | `Services/ResourceMappingService.cs` | Catalogue: resource type → ADO pipeline id + Terraform folder + required fields. Rule-based inference (deterministic keyword rules); the AI/LLM classifier is a drop-in replacement for `InferResourceType` |
| ADO integration | `Services/AdoPipelineClient.cs` | Real ADO REST v7 client (queues builds, polls status). `SimulationAdoPipelineClient` is used when `Ado:Organization` is not configured — deterministic, always green after ~3s, supports `simulate_failure=true` for tests |
| Audit | `Services/AuditService.cs` + `Data/OrchestratorDbContext.cs` | EF Core writes every transition to Azure SQL (InMemory provider for local dev/CI) |
| Live push | `Hubs/DeploymentStatusHub.cs` | Clients subscribe per-request; server broadcasts `statusUpdated` |
| Monitoring | `Workers/MonitoringBackgroundService.cs` | BackgroundService: every **15s** polls every `PipelineRunning` request for up to **40 minutes** (config `Monitoring:IntervalSeconds` / `Monitoring:PollTimeoutMinutes`), pushes intermediate states, completes terminal ones, times out otherwise |

### 2.3 Azure DevOps pipeline (`pipelines/azure-pipelines.yml`)
Triggered by the agent via REST with template parameters:
1. **generate_tfvars** — converts the `tfvars_json` parameter into
   `generated.tfvars` + `deploy_meta.tfvars` in the resolved Terraform folder and commits
   them (the "Commit terraform.tfvars" step in the diagram)
2. **terraform_plan** — `init → validate → plan`, publishes the plan artifact
3. **terraform_apply** — deployment job with an **environment gate** (`state` name;
   the `prod` ADO environment carries the manual approval gate)

### 2.4 Resource mapping catalogue

| ResourceType | Pipeline | Terraform folder | Required fields |
|---|---|---|---|
| Subscription | 101 | terraform/subscription | subscription, state, owner |
| ResourceGroup | 102 | terraform/resource-group | subscription, resource_group, location |
| WebApp | 103 | terraform/webapp | subscription, resource_group, app_name, runtime |
| SqlDatabase | 104 | terraform/sql | subscription, resource_group, server_name, database_name |
| KeyVault | 105 | terraform/keyvault | subscription, resource_group, vault_name |
| StorageAccount | 106 | terraform/storage | subscription, resource_group, storage_name |
| VirtualMachine | 107 | terraform/vm | subscription, resource_group, vm_name, vm_size |

## 3. Request lifecycle state machine

```
Draft ──(all required inputs present)──> Validation
  ▲        │ valid
  │        ▼
  │     SecurityApproval ──(approve)──> ReviewTeamApproval
  │        │ invalid                       │ approve
  │ missing┘                               ▼
  │ (ask for inputs)                 PipelineRunning ──(monitor 15s/40min)──┐
  │                                                                        │
  └──────────────────────────────────────────── CompletedSuccess ◄─────────┤
                                            CompletedFailed  ◄─────────────┘
                                               (timeout after 40 min → Failed)
```

## 4. Data model (Azure SQL)

- `DeploymentRequests` — full request state (fields/missingFields serialized as JSON),
  lifecycle status, approval records, run id, timestamps
- `AuditEntryRows` — append-only audit: `start`, `infer`, `validation-*`, `missing-inputs`,
  `inputs-submitted`, `security-approved`, `review-approved`, `pipeline-queued`, `completed`

`Database.EnsureCreated()` provisions the schema on first boot (swap to
`Database.Migrate()` + EF migrations for production evolution — see SECURITY.md).

## 5. Hosting topology (as provisioned by Terraform)

- **API**: Azure Container Apps (0.5 vCPU / 1Gi), external HTTPS ingress on port 8080,
  system-assigned managed identity, autoscale 1–2 replicas
- **Frontend**: Storage static website
- **SignalR**: Azure SignalR Service (Free_F1) wired via `Azure:SignalR:ConnectionString`
  (scale-out path; container ingress also passes websockets natively)
- **SQL**: Azure SQL S0 + firewall (Azure services + deployer IP)
- **Key Vault**: standard SKU, RBAC role `Key Vault Secrets User` for the API identity
- **ACR**: Basic, admin-enabled for container-app pull

## 6. Design decisions & rationale

| Decision | Why |
|---|---|
| `IPipelineClient` abstraction + simulation | The workflow can be exercised and tested without an ADO org; the real client is one config value away |
| Rule-based inference first | AI can guess dangerously (e.g. resource types); deterministic rules are auditable. Plug the LLM classifier behind `InferResourceType` with the approval gates as the safety net |
| Polling (15s/40min) for run status | Matches the reference diagram; ADO webhooks would be lower latency — see "Webhook upgrade" in DEPLOYMENT.md |
| String-enum JSON contract | Frontend consumes human-readable statuses directly |
| EF Core InMemory fallback | Zero-dependency local dev + fast CI; SQL is one connection string away |
| Single local Terraform state | Single-click deploy/destroy simplicity; swap to the azurerm backend (commented in providers.tf) for team use |
