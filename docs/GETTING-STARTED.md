# Getting Started — from zero to deployed (beginner guide)

This page assumes you have **never used Azure, Terraform, or the Azure CLI**.
Follow it top to bottom and you will have the platform running in ~15 minutes.

---

## 1. What you need (big picture)

| You need | Why | Cost |
|---|---|---|
| **An Azure subscription** | Everything is deployed inside your subscription | Pay-as-you-go account works; trial/student subscriptions also work (see §6 notes) |
| **A Linux/macOS machine** (or WSL2 on Windows) with `sudo` | The install + deploy scripts are bash | Free |
| **~2 GB disk + internet** | Downloads tools + builds the app | — |
| **Nothing else** | No Docker, no Kubernetes, no Azure DevOps org needed for the default run | — |

The only two commands you will actually run:

```bash
./scripts/install_prereqs.sh   # installs every tool automatically
./scripts/deploy.sh            # provisions + builds + deploys + verifies
./scripts/destroy.sh --yes     # deletes EVERYTHING when done
```

---

## 2. Software requirements (exact versions)

| Tool | Version | Purpose | Installed automatically? |
|---|---|---|---|
| **Azure CLI** (`az`) | ≥ 2.60 | Log in to Azure, create/manage resources | ✅ `install_prereqs.sh` |
| **Terraform** | ≥ 1.9 | Provisions all Azure infrastructure | ✅ |
| **Node.js + npm** | ≥ 20 | Builds the React frontend | ✅ |
| **.NET SDK** | 10.x | Builds the backend image (fallback path) + runs tests | ✅ |
| **crane** | ≥ 0.20 | Pushes the container image to Azure **without Docker** | ✅ |
| **git** | any | Clone the repository | ✅ |
| **bash** | any | Runs the scripts | preinstalled (Linux/macOS/WSL2) |
| **python3** | ≥ 3.8 | Assembles the container image layer | usually preinstalled |
| **unzip, curl, jq** | any | Used by installers/tests | usually preinstalled |

**No Docker daemon is required.** The container image is built daemonlessly.
(If you *have* Docker, `pipelines/azure-pipelines.yml` + `Dockerfile` still work
in Azure DevOps agents as usual.)

### Windows users
Use **WSL2** (Ubuntu). In PowerShell (admin):
```powershell
wsl --install -d Ubuntu
```
Then open Ubuntu and continue below. Native Windows (PowerShell) is *not* supported
by the scripts out of the box.

---

## 3. Step 0 — Get an Azure account

1. Go to https://azure.microsoft.com/free → create a free/pay-as-you-go account.
2. You will need **Owner or Contributor** rights on the subscription you use
   (if it's your own subscription, you already do).
3. Have a credit card or free credits attached (deploy creates paid-tier resources
   like SQL S0 — they are destroyed by `destroy.sh`; a full deploy+destroy cycle
   costs roughly a few dollars of metered time).

---

## 4. Step 1 — Install the tooling

```bash
# clone this repository
git clone https://github.com/ajithvnr2001/ado-deployment-orchestrator.git
cd ado-deployment-orchestrator

# install az + terraform + node + dotnet + crane (one command)
./scripts/install_prereqs.sh
```

The installer:
- detects your OS/arch (Ubuntu/Debian/RHEL/macOS),
- skips tools you already have,
- **verifies at the end** and prints a ✔/✘ table.

If the ✘ table shows anything missing, install it manually (links in the
troubleshooting table, §8) and re-run the script.

<details>
<summary>Manual install (if you prefer doing it yourself)</summary>

```bash
# Azure CLI (Ubuntu)
curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash

# Terraform 1.13
curl -sSL -o /tmp/tf.zip https://releases.hashicorp.com/terraform/1.13.3/terraform_1.13.3_linux_amd64.zip
sudo unzip /tmp/tf.zip -d /usr/local/bin

# Node 22
curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash - && sudo apt-get install -y nodejs

# .NET 10 SDK
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0

# crane
curl -sSL https://github.com/google/go-containerregistry/releases/download/v0.20.3/go-containerregistry_Linux_x86_64.tar.gz \
  | tar xz crane && sudo mv crane /usr/local/bin/
```
</details>

---

## 5. Step 2 — Log in to Azure and deploy

```bash
az login
# A browser opens (or a device code is printed) → sign in with your Azure account.
az account show   # sanity check: prints your subscription

./scripts/deploy.sh
```

### What `deploy.sh` does (you don't have to do anything, just wait ~10 min)

| Step | What happens | Where you see it |
|---|---|---|
| 1 | Pre-flight check (all tools + az login) | `[deploy]` log lines |
| 2 | `terraform apply` → creates resource group, Container Apps, ACR, SQL, SignalR, Key Vault, Storage (~6 min) | terraform progress |
| 3 | Builds the backend container image & pushes it to ACR (~3 min; uses ACR Tasks if allowed, else the daemonless crane build) | `[image]` log lines |
| 4 | Builds the React frontend and uploads it to the static website | `[deploy]` log lines |
| 5 | **Smoke test**: waits for `/healthz`, creates a deployment via the API, checks it reaches `SecurityApproval` | `Smoke test...` lines |

At the end you get:
```
Frontend: https://stororch<suffix>.<region>.web.core.windows.net
API:      https://ca-orch-api-<suffix>.<region>.azurecontainerapps.io
```
Open the **Frontend URL in your browser** → the chat controller loads →
type *"create a new web app for the payments team"* and watch the workflow run.

> Out of the box, the API uses the built-in **pipeline simulator** (always-succeed
> simulated ADO runs), so the whole workflow works with zero Azure DevOps setup.
> To plug in a real ADO org + real Terraform pipelines: **docs/DEPLOYMENT.md §4**.

---

## 6. Step 3 — Destroy everything (important!)

```bash
./scripts/destroy.sh --yes
```

- Runs `terraform destroy` for every resource it created
- Force-deletes leftover resource groups, purges soft-deleted Key Vaults
- Verify it's clean: `az group list --query "[?starts_with(name,'rg-orchestrator-')]"` → `[]`

Skipping this step = you keep paying for the resources. **Always destroy when done.**

### Subscription-tier notes
- **Free trial / student subscriptions** often have *zero App Service quota* and no
  ACR Tasks. This repo handles both automatically (Container Apps + daemonless build)
  — that's the default path, not an error.
- SQL is provisioned in `swedencentral` by default because some regions reject new
  servers; override with `--workload-location` if needed.

---

## 7. Running the tests (optional but recommended first)

```bash
cd backend  && dotnet test -c Release   # 19 tests — full workflow in-process
cd ../frontend && npm install && npm test && npm run build
cd ../terraform && terraform validate   # infra syntax check
```

---

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `az: command not found` | install failed | https://learn.microsoft.com/cli/azure/install-azure-cli then re-run installer |
| `Not logged in to Azure. Run: az login` | session expired | `az login` |
| `RequestDisallowedByAzure` / quota `70007` | subscription blocks a service/region | The scripts already pick fallbacks; try `--location` / `--workload-location` variations (e.g. `swedencentral`, `westus3`, `northeurope`) |
| API `healthz` not healthy within 200s | image pull/start issue | `az containerapp logs show -g <rg> -n <app> --type system` and `--type console` |
| SQL timeout from the API | firewall missing your IP | deploy.sh adds it automatically; re-check `az sql server firewall-rule list` |
| Terraform "Object already exists" | suffix collision from an earlier partial run | `./scripts/destroy.sh --yes` then redeploy |
| `frontend` shows blank page / API errors in console | stale build | re-run `./scripts/deploy.sh` (idempotent, rebuilds frontend with the correct API URL) |
| crane push 401 | stale ACR credentials | `az acr login -n <acr>` and re-run deploy |
| Port 5173 CORS errors in local dev | frontend dev server origin not allowed | it is allowed by default (`http://localhost:5173` in appsettings.json) |

---

## 9. What gets created (so you can audit it)

```
rg-orchestrator-<suffix>/
├── cae-orch-<suffix>          Container Apps environment (+ Log Analytics)
├── ca-orch-api-<suffix>       the .NET 10 API container app
├── acrorch<suffix>            Azure Container Registry
├── sql-orch-<suffix>          Azure SQL server + orchestrator-db (S0)
├── kv-orch-<suffix>           Key Vault
├── signalr-orch-<suffix>      Azure SignalR (Free_F1)
└── stororch<suffix>           Storage account (static website for the UI)
```

All names are tagged `project=ado-deployment-orchestrator, managed_by=terraform` —
search by tag in the portal to find everything.

---

## 10. Where to go next

- **docs/ARCHITECTURE.md** — how the pieces work and why
- **docs/API.md** — call the API yourself with curl
- **docs/DEPLOYMENT.md** — wire a real Azure DevOps org (real Terraform pipelines)
- **docs/SECURITY.md** — the production-hardening checklist
- **docs/PROCESS.md** — how this repo was built, tested, and verified end-to-end
