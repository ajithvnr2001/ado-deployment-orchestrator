# Azure DevOps Deployment Orchestration Platform

**Production-style, end-to-end deployment orchestration**: a chat-driven frontend requests
infrastructure deployments, a .NET 10 Orchestration Agent validates + approves + triggers
Azure DevOps pipelines running Terraform, and a background monitoring service pushes live
status to users over SignalR — with a complete audit trail in Azure SQL.

This repository is a **working implementation** of the architecture in
`docs/images/workflow-reference.jpeg` (sequence diagram + high-level architecture).

![status](https://img.shields.io/badge/.NET-10.0-blue) ![status](https://img.shields.io/badge/React-19-blue) ![status](https://img.shields.io/badge/Terraform-1.13-purple)

---

## What is deployed

| Component | Azure service | Purpose |
|---|---|---|
| Orchestration Agent API | Azure Container Apps | .NET 10 Web API + SignalR hub + monitoring worker |
| Chat Deployment Controller | Storage static website | React + TypeScript chat UI |
| Deployment pipelines | Azure DevOps (YAML) | Terraform plan/apply, tfvars generation, approvals |
| Audit trail ("Complete Audit & History") | Azure SQL (S0) | Every workflow decision persisted |
| Real-time status push | Azure SignalR Service | Live pipeline status for long-running runs |
| Secrets | Azure Key Vault | Connection strings / PAT storage |
| Image registry | Azure Container Registry | API container image |

## Repository layout

```
ado-orchestrator/
├── backend/
│   ├── DeployOrchestrator.Api/        # .NET 10 Orchestration Agent (Web API + SignalR + worker)
│   │   ├── Controllers/               #   REST endpoints (start, inputs, approve, audit)
│   │   ├── Services/                  #   Workflow engine, resource mapping, ADO client, audit
│   │   ├── Hubs/                      #   SignalR hub (statusUpdated events)
│   │   ├── Workers/                   #   MonitoringBackgroundService (poll every 15s / 40min)
│   │   ├── Domain/                    #   Entities + DTOs + lifecycle status enum
│   │   └── Data/                      #   EF Core (Azure SQL / InMemory)
│   └── DeployOrchestrator.Tests/      # 19 xUnit tests (unit + in-process E2E)
├── frontend/                          # React 19 + TS chat UI (@microsoft/signalr)
├── pipelines/azure-pipelines.yml      # ADO pipeline: tfvars commit -> plan -> apply
├── terraform/                         # Full infrastructure as code
├── scripts/
│   ├── deploy.sh                      # ONE-COMMAND deploy (builds + provisions + verifies)
│   ├── destroy.sh                     # ONE-COMMAND destroy (everything)
│   └── build_image.sh                 # Dockerless container build (buildah/crane/OCI)
├── Dockerfile                         # Multi-stage .NET 10 build
└── docs/
    ├── ARCHITECTURE.md                # How the system works (matches the sequence diagram)
    ├── DEPLOYMENT.md                  # Single-click deploy/destroy + ADO wiring guide
    ├── PROCESS.md                     # How this repo was built end-to-end (full procedure log)
    ├── API.md                         # REST + SignalR reference
    └── SECURITY.md                    # Auth model, RBAC, secrets, hardening notes
```

## Quick start (single click)

```bash
az login                                  # any subscription where you can create resources
./scripts/deploy.sh                       # provisions + builds + deploys + verifies  (~10 min)
# ... deploy prints the frontend + API URLs and runs a smoke test ...
./scripts/destroy.sh                      # tears EVERYTHING down when done
```

`deploy.sh` is idempotent — run it again any time to rebuild/redeploy the app.
Detailed walkthrough: **docs/DEPLOYMENT.md**.

## The five workflow phases (as implemented)

1. **Deployment initiation** — `POST /api/deployments` creates the request (Draft → Validation)
2. **Resource identification & validation** — rule-based (AI-pluggable) resource type inference,
   required-field validation against the resource mapping catalogue → Security approval gate
3. **Missing input handling** — the agent asks for `key=value` inputs, merges and re-validates
4. **Terraform & pipeline execution** — tfvars generated from the merged fields, committed to
   GitHub, ADO pipeline queued (`PipelineRunning`), run id recorded
5. **Asynchronous monitoring** — background worker polls run status every **15s for up to
   40 minutes**, pushes live updates over SignalR, completes as `CompletedSuccess`/`CompletedFailed`

## Testing

```bash
cd backend  && dotnet test -c Release    # 19 tests: unit + in-process end-to-end
cd frontend && npm test                  # vitest contract test
cd frontend && npm run build             # strict TS build
cd terraform && terraform validate       # infra validation
```

## Documentation index

- **docs/ARCHITECTURE.md** — components, data flow, state machine, sequence-diagram mapping
- **docs/DEPLOYMENT.md** — deploy/destroy scripts, ADO service connection + variable group setup
- **docs/PROCESS.md** — the exact procedure used to build, test, deploy and verify this system
- **docs/API.md** — REST + SignalR API reference with examples
- **docs/SECURITY.md** — auth, RBAC, secrets, network, and hardening checklist
