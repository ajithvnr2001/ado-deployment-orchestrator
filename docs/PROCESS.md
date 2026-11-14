# Build process & procedure — how this system was built end-to-end

This document records the exact procedure followed to take the reference architecture
(sequence diagram) from analysis → implementation → tests → deployment → verification →
(teardown), including every obstacle hit and the fix applied. It doubles as a
reproducible runbook.

## Phase 0 — Analysis of the reference architecture

Input: the sequence-workflow image (Azure DevOps Deployment Orchestration —
ADO Agent | Terraform | GitHub | Monitoring | End-to-End Deployment Flow).

Conclusions from analysis:
1. The architecture is **feasible and standard** — every component maps to a real,
   widely-used Azure capability (see README "what is deployed").
2. Caveats identified and incorporated:
   - Prefer managed identity / service principals over PATs where possible
     → system-assigned identity + Key Vault RBAC role implemented
   - Polling (15s/40min) is fine, webhooks lower latency
     → implemented polling (matches diagram), documented the webhook upgrade
   - AI inference must not be trusted blindly → deterministic rule-based inference
     implemented, AI is a pluggable replacement behind the same interface
   - Complete audit trail is essential → every workflow transition persisted to Azure SQL

## Phase 1 — Environment verification & tooling

```bash
az account show          # verified Azure login + subscription
node --version           # v22.23.5 ✓
git --version            # ✓
# Missing tools installed:
dotnet-install --channel 10.0   # .NET 10 SDK (10.0.404)
terraform 1.13.3                # static binary release
```

## Phase 2 — Backend implementation (.NET 10)

Procedure:
1. `dotnet new sln` + `webapi` + `xunit` projects, wired with `dotnet sln add`.
2. Packages: EF Core 10.0.1 (InMemory + SqlServer), JWT bearer, MVC.Testing (tests),
   Microsoft.Azure.SignalR (scale-out ready).
3. Code written in this order (bottom-up, so every layer compiles alone):
   - `Domain/` — `DeploymentRequest` entity + lifecycle enum + DTOs
   - `Data/` — EF Core context (fields/missingFields as JSON columns)
   - `Services/ResourceMappingService.cs` — catalogue + rule-based inference
   - `Services/AdoPipelineClient.cs` — `IPipelineClient` + real ADO client + simulator
   - `Services/AuditService.cs` — append-only audit
   - `Services/DeploymentOrchestrator.cs` — the 5-phase workflow engine
   - `Hubs/DeploymentStatusHub.cs` — SignalR groups per request
   - `Workers/MonitoringBackgroundService.cs` — 15s/40min poller
   - `Program.cs` — DI wiring, provider selection (SQL vs InMemory), CORS, OpenAPI
4. Build fixed iteratively (`dotnet build` after each layer):
   - EF Core needs `version 10.0.x` pinned (latest 11.x targets net11.0)
   - SignalR server is in the shared framework — removed stale package
   - Missing `using` statements (Encoding, EF) and StringContent overload resolved

## Phase 3 — Tests

- **Unit tests**: resource-type inference rules, catalogue mapping correctness
- **Workflow tests**: full lifecycle, inference fallback, missing-input flow,
  out-of-order approval rejection, simulated pipeline completion
- **In-process E2E tests** (`WebApplicationFactory`): real HTTP over the full stack —
  health, full lifecycle, missing-input chat flow, audit trail count
- Test-hardening fixes found by the tests themselves:
  1. Required-field validation ignored top-level `subscription`/`state`
     → effective-fields merge added (test caught it)
  2. Monitoring poll interval (15s) too slow for tests → `Monitoring:IntervalSeconds`
     config override via `UseSetting` in the test factory
  3. Approve-out-of-order test premise wrong (approve on `SecurityApproval` is always
     a security approval) → test rewritten against a Draft request

Result: **19/19 tests pass** (`dotnet test -c Release`).

## Phase 4 — Frontend (React 19 + TypeScript)

1. Vite react-ts template; added `@microsoft/signalr`, `vitest`.
2. `src/api.ts` — typed REST client (start/inputs/approve/get/audit).
3. `src/signalr.ts` — hub connection with auto-reconnect + per-request subscription.
4. `src/App.tsx` — chat UI: request creation, missing-input prompting (`key=value`),
   approval actions, live `[live]` status messages from SignalR, audit-trail viewer.
5. Strict TS build (fixed: unused import, fetch-handling generic).
6. `npm run build` ✓ + `npm test` ✓ (1 contract test).

## Phase 5 — Infrastructure as code (Terraform)

1. Providers pinned `azurerm ~> 4.0`; local state (azurerm backend documented/commented).
2. Resources: RG → service plan + web app (first attempt) — later replaced (see Phase 7).
3. Iterated with `terraform validate` until clean:
   - `azurerm_signalr_service`: `features` block not valid in azurerm 4 → `service_mode`
   - `azurerm_linux_web_app.application_stack` requires `dotnet_version` → `10.0`
4. `azurerm_container_app` schema learned from `terraform providers schema -json`:
   - registry uses `password_secret_name` (not `password`)
   - ingress uses `external_enabled` + requires a `traffic_weight` block
   - FQDN attribute is `ingress.0.fqdn`

## Phase 6 — Azure DevOps pipeline + scripts

- `pipelines/azure-pipelines.yml`: 3 stages (tfvars commit → plan → gated apply),
  parameterized by the orchestrator (`request_id`, `terraform_script_folder`,
  `tfvars_json`, `state`, `work_item_id`).
- `scripts/deploy.sh` + `scripts/destroy.sh`: single-click lifecycle with pre-flight,
  idempotent re-deploy, smoke test, and a teardown safety net (leftover RG force-delete
  + Key Vault purge).
- `Dockerfile`: multi-stage .NET 10 build.

## Phase 7 — Real Azure deployment & quota discovery (the interesting part)

The first real `terraform apply` exposed subscription-level restrictions, resolved in order:

| # | Obstacle | Error | Resolution |
|---|---|---|---|
| 1 | West Europe rejects new App Service | `RequestDisallowedByAzure` | Tried swedencentral → eastus |
| 2 | Zero App Service quota anywhere (B1 **and** F1) | `70007 Unauthorized (quota)` | Quota API not self-serviceable → **architecture pivot to Azure Container Apps** (probed with a test env — worked) |
| 3 | SQL blocked in northeurope/eastus, allowed in **swedencentral/westus3** | `RegionDoesNotAllowProvisioning` | `workload_location` variable defaults to swedencentral |
| 4 | ACR Tasks blocked | `TasksOperationsNotAllowed` | Daemonless image build (below) |
| 5 | buildah unpack fails (nested container, no mounts) | `remount /: permission denied` | **Pure OCI assembly**: `crane pull --format=oci` base image + python builds the /app layer + patches manifest/config + `crane push` |
| 6 | First image start failed | `exec: permission denied` | Layer tar modes were 0644 → 0755 |
| 7 | `az containerapp update` with same tag made no new revision | silent | **Digest-pinned** image reference forces revision |
| 8 | Enums serialized as numbers to the UI | `status: 2` | `JsonStringEnumConverter` in MVC JSON options (tests updated to match) |

Final topology (see docs/ARCHITECTURE.md §5) deploys 15 resources successfully.

## Phase 8 — Deployment verification (real Azure, live)

1. `GET /healthz` → **HTTP 200**
2. `POST /api/deployments` (StorageAccount, qa) → request created,
   **persisted in Azure SQL**, status `SecurityApproval`
3. Security approval → `ReviewTeamApproval`; review approval → `PipelineRunning` (run 1001)
4. Monitoring worker detected simulated completion → **`CompletedSuccess`** (poll #1)
5. Audit trail: 6 entries in Azure SQL (start → validation → approvals → pipeline-queued → completed)
6. `POST /hubs/deployment/negotiate` → SignalR connection + websocket transports offered
7. CORS preflight from the frontend origin → `204` with correct allow-origin/headers
8. Frontend uploaded to the Storage static website and serving HTML

## Phase 9 — Publishing

- GitHub repo created via REST API, full repo pushed (main branch) with docs.
- No secrets in the repo (SQL password/PATs are runtime-only values; `.gitignore` covers
  state files, node_modules, publish output).

## Phase 10 — Teardown

- `./scripts/destroy.sh --yes` → `terraform destroy` (15 resources) + leftover-RG sweep +
  Key Vault purge; verified empty with `az group list`.

## Reproducing the whole run

```bash
cd backend  && dotnet test -c Release   # Phase 3
cd frontend && npm test && npm run build
./scripts/deploy.sh                      # Phase 7/8 (needs az login)
./scripts/destroy.sh --yes               # Phase 10
```
