#!/usr/bin/env bash
# ============================================================================
#  Single-click DEPLOY — Azure DevOps Deployment Orchestration platform
#  Usage: ./scripts/deploy.sh [--location northeurope] [--workload-location swedencentral]
#
#  PREREQUISITES: run ./scripts/install_prereqs.sh once (installs az, terraform,
#  node, .NET 10, crane) and then `az login`. Beginner guide: docs/GETTING-STARTED.md
#
#  What it does (in order):
#    1. Verifies prerequisites (az login, dotnet, node, terraform)
#    2. terraform init + apply  ->  RG, Container Apps env, ACR, SQL, SignalR,
#       KeyVault, Storage static website
#    3. Builds the API container image server-side (az acr build) and updates
#       the container app to run it
#    4. Builds the frontend (vite) and uploads it to the Storage $web container
#    5. Prints the URLs and runs a smoke test against the deployed API
#
#  Tear everything down with: ./scripts/destroy.sh
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
LOCATION="northeurope"
WORKLOAD_LOCATION="swedencentral"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --location) LOCATION="$2"; shift 2 ;;
    --workload-location) WORKLOAD_LOCATION="$2"; shift 2 ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done

log()  { printf '\033[1;34m[deploy]\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31m[deploy:ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

command -v az        >/dev/null || fail "az CLI not found"
command -v terraform >/dev/null || fail "terraform not found"
command -v node      >/dev/null || fail "node not found"

log "Checking Azure login..."
SUBSCRIPTION_ID="$(az account show --query id -o tsv 2>/dev/null)" || fail "Not logged in to Azure. Run: az login"
log "Using subscription: $SUBSCRIPTION_ID"

SQL_PASSWORD="Orch$(openssl rand -hex 12)A1a!"
DEPLOYER_IP="$(curl -s https://api.ipify.org)"
log "Deployer IP for SQL firewall: $DEPLOYER_IP"

# ---------------------------------------------------------------------------
# 1) Terraform
# ---------------------------------------------------------------------------
cd "$ROOT_DIR/terraform"
log "Running terraform init..."
terraform init -input=false -reconfigure >/dev/null

log "Running terraform apply (RG, Container Apps, ACR, SQL, SignalR, KeyVault, Storage)..."
terraform apply -auto-approve -input=false \
  -var "location=$LOCATION" \
  -var "workload_location=$WORKLOAD_LOCATION" \
  -var "sql_admin_password=$SQL_PASSWORD" \
  -var "deployer_ip=$DEPLOYER_IP"

API_URL="$(terraform output -raw api_url)"
FRONTEND_URL="$(terraform output -raw frontend_url)"
ACR_SERVER="$(terraform output -raw acr_login_server)"
RG_NAME="$(terraform output -raw resource_group_name)"
CA_NAME="$(az containerapp list -g "$RG_NAME" --query '[0].name' -o tsv)"
STORAGE_NAME="$(az storage account list -g "$RG_NAME" --query "[?starts_with(name,'stororch')].name | [0]" -o tsv)"

log "API URL:      $API_URL"
log "Frontend URL: $FRONTEND_URL"

# ---------------------------------------------------------------------------
# 2) Build API image + deploy
# ---------------------------------------------------------------------------
log "Building API container image (try ACR Tasks, fall back to buildah)..."
if az acr build \
  --registry "${ACR_SERVER%%.*}" \
  --image "orchestrator-api:latest" \
  --file "$ROOT_DIR/Dockerfile" \
  "$ROOT_DIR" >/dev/null 2>&1; then
  log "Built server-side via ACR Tasks."
else
  log "ACR Tasks unavailable — building daemonless with buildah..."
  "$SCRIPT_DIR/build_image.sh" "$ACR_SERVER"
fi

log "Updating container app '$CA_NAME' to run the new image (digest-pinned => forces new revision)..."
ACR_DIGEST="$(az acr repository show -n "${ACR_SERVER%%.*}" --image "orchestrator-api:latest" --query digest -o tsv)"
az containerapp update \
  -g "$RG_NAME" -n "$CA_NAME" \
  --image "$ACR_SERVER/orchestrator-api@${ACR_DIGEST}" >/dev/null

# ---------------------------------------------------------------------------
# 3) Build + deploy frontend to the static website
# ---------------------------------------------------------------------------
log "Building frontend..."
( cd "$ROOT_DIR/frontend" && VITE_API_BASE="$API_URL" npm run build >/dev/null )

log "Uploading frontend to $FRONTEND_URL ..."
az storage blob upload-batch \
  --account-name "$STORAGE_NAME" \
  --destination '$web' \
  --source "$ROOT_DIR/frontend/dist" \
  --overwrite >/dev/null

# ---------------------------------------------------------------------------
# 4) Smoke test
# ---------------------------------------------------------------------------
log "Waiting for the API to come up..."
API_OK="false"
for i in $(seq 1 40); do
  if curl -sf "$API_URL/healthz" >/dev/null; then API_OK="true"; break; fi
  sleep 5
done
[[ "$API_OK" == "true" ]] || fail "API did not become healthy within 200s — check logs: az containerapp logs show -g $RG_NAME -n $CA_NAME"
log "API health check: OK"

log "Smoke test: creating a deployment request through the deployed API..."
HTTP_CODE="$(curl -s -o /tmp/opencode/smoke.json -w '%{http_code}' -X POST "$API_URL/api/deployments" \
  -H 'Content-Type: application/json' \
  -d '{"requestedBy":"deploy-script@contoso.com","subscription":"sub-smoke","state":"dev","resourceType":"WebApp","resourceName":"smoke web app","fields":{"resource_group":"rg-smoke","app_name":"app-smoke","runtime":"dotnet"}}')"
[[ "$HTTP_CODE" == "200" ]] || fail "Smoke test failed with HTTP $HTTP_CODE: $(cat /tmp/opencode/smoke.json)"
REQ_ID="$(python3 -c "import json;print(json.load(open('/tmp/opencode/smoke.json'))['id'])")"
log "Created request $REQ_ID — verifying it reaches SecurityApproval..."
sleep 2
STATUS="$(curl -s "$API_URL/api/deployments/$REQ_ID" | python3 -c "import json,sys;print(json.load(sys.stdin)['status'])")"
log "Request status: $STATUS"
[[ "$STATUS" == "SecurityApproval" ]] || fail "Expected SecurityApproval, got $STATUS"

log "DONE — platform deployed and verified end to end."
log "Frontend: $FRONTEND_URL"
log "API:      $API_URL  (healthz, /api/deployments, /hubs/deployment)"
log "To tear everything down: ./scripts/destroy.sh"
