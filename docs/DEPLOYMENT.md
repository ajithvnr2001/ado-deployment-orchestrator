# Deployment guide

## 0. Prerequisites

| Tool | Version | Notes |
|---|---|---|
| `az` CLI | ≥ 2.60 | logged in (`az login`), a subscription where you can create resources |
| `terraform` | ≥ 1.9 | provider `azurerm ~> 4.0` |
| `node` + `npm` | ≥ 20 | frontend build |
| `crane` | ≥ 0.20 | only when ACR Tasks is unavailable (see §3) |
| `dotnet` SDK 10 | | only for running tests locally / buildah-less rebuilds |

No Docker daemon is required — `scripts/build_image.sh` builds the container image
daemonlessly (dotnet publish + OCI assembly via crane; buildah alternative included).

## 1. Single-click deploy

```bash
az login
./scripts/deploy.sh
```

What it does, in order:

1. **Pre-flight** — verifies az login, terraform, node, crane availability
2. **Terraform apply** — creates (see terraform/main.tf):
   `rg-orchestrator-<suffix>` + Log Analytics + Container Apps environment + ACR +
   container app + Storage static website + SignalR + SQL server/db + firewall rules +
   Key Vault + KV secret + RBAC role
   - Random 6-char suffix per deploy (e.g. `hxuski`) — global-name-safe
   - SQL password auto-generated, deployer IP added to the SQL firewall
3. **API image build** — `az acr build` (server-side). If ACR Tasks is blocked on the
   subscription (common on sandbox/trial subscriptions), automatically falls back to
   `scripts/build_image.sh` (daemonless OCI build). The container app is updated with a
   **digest-pinned** image so a new revision is always created.
4. **Frontend build + upload** — `npm run build` with `VITE_API_BASE=<api url>`, uploaded
   to the `$web` container
5. **Smoke test** — waits for `/healthz` (HTTP 200), creates a deployment request through
   the public API, verifies it reaches `SecurityApproval`

Output: frontend URL, API URL, resource group name.

Typical end-to-end duration: **8–12 minutes** (Terraform ~6 min, ACR build/fallback ~3 min).

### Configuration knobs (terraform/variables.tf)

| Variable | Default | Meaning |
|---|---|---|
| `location` | northeurope | RG, SignalR, frontend hosting |
| `workload_location` | swedencentral | Container Apps, ACR, SQL, KV (some regions reject new services; westus3 also worked in validation) |
| `app_service_sku` | F1/B1 | (n/a with Container Apps) |
| `sql_admin_password` | generated | SQL admin password |
| `deployer_ip` | auto-detected | SQL firewall allow-list |

## 2. Single-click destroy

```bash
./scripts/destroy.sh --yes
```

1. `terraform destroy` — removes everything Terraform created (Key Vault purged via
   `purge_soft_delete_on_destroy`)
2. **Safety net** — force-deletes any leftover `rg-orchestrator-*` resource groups and
   purges soft-deleted `kv-orch-*` vaults (soft-delete keeps names reserved for 7 days)

Verify clean: `az group list --query "[?starts_with(name,'rg-orchestrator-')]" -o tsv` → empty.

## 3. Notes on container image building (subscription quota reality)

Different Azure subscriptions enforce different quotas. During validation we found:

| Capability | Available here? | Fallback implemented |
|---|---|---|
| App Service (B1/F1) | ❌ zero quota in all regions | **Azure Container Apps** (HTTPS ingress, native websockets) |
| ACR Tasks (`az acr build`) | ❌ `TasksOperationsNotAllowed` | **Daemonless OCI build** (`scripts/build_image.sh`): `dotnet publish --self-contained` → crane OCI assembly → ACR push |
| buildah | ❌ nested-container mount denied | crane/OCI path (no mount privileges needed) |
| Azure SQL | ✅ swedencentral / westus3 | region is configurable (`workload_location`) |
| Azure SignalR / Storage static site / Key Vault | ✅ | — |

`deploy.sh` handles both the clean path (ACR Tasks) and the fallback automatically.

## 4. Wiring a real Azure DevOps org (optional — enables real pipelines)

The API runs the **simulated pipeline client** unless ADO is configured, so the whole
platform works end-to-end out of the box. To switch to real pipelines:

1. **Create the pipeline definition** in your org:
   ```bash
   az devops project create --organization https://dev.azure.com/<org> --name platform
   # push this repo, or import pipelines/azure-pipelines.yml into the repo
   ```
2. **Service connection** named `azure-service-connection` (ARM, workload identity) with
   Contributor on the deployment subscription.
3. **Variable group** `deployment-secrets` containing `ADO_PAT` (or use the service
   connection identity).
4. **Map catalogue pipeline ids** — set the definition ids from your org in
   `Services/ResourceMappingService.cs` (or externalize to config).
5. **Point the API at ADO** (Container Apps secrets):
   ```bash
   az containerapp secret set -g <rg> -n <app> \
     --secrets ado-pat="<PAT>"
   az containerapp update -g <rg> -n <app> \
     --set-env-vars "Ado__Organization=<org>" "Ado__Project=platform" \
        "Ado__Pat=secretref:ado-pat"
   ```
   The monitoring worker now polls real run statuses (`AdoPipelineClient.GetRunStatusAsync`).

## 5. Webhook upgrade (recommended for production)

The monitoring worker polls every 15s (matches the reference diagram). For lower latency:

- Create an ADO **service hook** (WebHooks → `Build completed`) → POST to
  `POST /api/internal/run-completed?requestId=...&result=...` (add auth!) — the orchestrator
  completes the request immediately instead of waiting for the next poll.
- Keep polling as the safety net (timeout still 40 min).

## 6. Environments & state backend

- Local state file: `terraform/terraform.tfstate` (single-click simplicity)
- Team/state backend: uncomment the `backend "azurerm"` block in `terraform/main.tf`,
  create the storage account once, then `terraform init -reconfigure`
- The ADO pipeline uses per-request state keys (`-backend-config="key=<request_id>.tfstate"`)
  so concurrent resource deployments don't fight over locks.
