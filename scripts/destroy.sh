#!/usr/bin/env bash
# ============================================================================
#  Single-click DESTROY — tears down everything deploy.sh created:
#    1. terraform destroy (RG + all resources: App Service, SQL, SignalR,
#       KeyVault with purge, Storage static site)
#    2. Safety net: force-deletes any leftover resource group with the
#       orchestrator prefix and purges soft-deleted Key Vaults.
#  Usage: ./scripts/destroy.sh [--yes]
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

log()  { printf '\033[1;33m[destroy]\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31m[destroy:ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

if [[ "${1:-}" != "--yes" ]]; then
  read -r -p "This will DELETE all deployed Azure resources. Continue? [y/N] " answer
  [[ "$answer" =~ ^[yY]$ ]] || { log "Aborted."; exit 0; }
fi

cd "$ROOT_DIR/terraform"

if [[ -d .terraform && -f terraform.tfstate ]]; then
  log "Running terraform destroy..."
  terraform destroy -auto-approve -input=false || log "terraform destroy returned non-zero — continuing to cleanup"
else
  log "No terraform state found — skipping terraform destroy."
fi

# Safety net: delete any leftover resource groups with the orchestrator prefix
log "Deleting any leftover rg-orchestrator-* resource groups..."
for RG in $(az group list --query "[?starts_with(name,'rg-orchestrator-')].name" -o tsv); do
  log "  Deleting $RG (no-wait)..."
  az group delete -n "$RG" --yes --no-wait || true
done

# Purge soft-deleted key vaults (soft-delete keeps names reserved for 7 days)
log "Purging soft-deleted key vaults (kv-orch-*)..."
for KV in $(az keyvault list-deleted --query "[?starts_with(name,'kv-orch-')].name" -o tsv); do
  log "  Purging $KV..."
  az keyvault purge -n "$KV" || true
done

log "All resources destroyed. Verify with: az group list --query \"[?starts_with(name,'rg-orchestrator-')]\""
