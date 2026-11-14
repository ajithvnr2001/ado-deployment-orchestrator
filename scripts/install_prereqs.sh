#!/usr/bin/env bash
# ============================================================================
#  ONE-SHOT TOOLING INSTALLER — installs everything needed to run
#  ./scripts/deploy.sh and ./scripts/destroy.sh on a fresh Linux/macOS machine.
#
#  Usage:  ./scripts/install_prereqs.sh
#
#  Installs (per-OS, best-effort with clear errors):
#    - Azure CLI (az)
#    - Terraform >= 1.9
#    - Node.js >= 20 + npm
#    - .NET SDK 10 (only needed when ACR Tasks is unavailable on your
#      subscription — the daemonless image-build fallback uses it locally)
#    - crane (container image push without Docker)
#
#  After running this: `az login` and you are ready for deploy.sh.
# ============================================================================
set -euo pipefail

log()  { printf '\033[1;32m[setup]\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31m[setup:ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

OS="$(uname -s)"
ARCH="$(uname -m)"
log "Detected OS=$OS ARCH=$ARCH"

have() { command -v "$1" >/dev/null 2>&1; }

# ---------------------------------------------------------------------------
# 1) Azure CLI
# ---------------------------------------------------------------------------
if have az; then
  log "az CLI already installed: $(az version --query '"azure-cli"' -o tsv 2>/dev/null || echo yes)"
else
  case "$OS" in
    Linux)
      if have apt-get; then
        curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
      elif have dnf; then
        sudo dnf install -y azure-cli
      elif have yum; then
        sudo rpm --import https://packages.microsoft.com/keys/microsoft.asc
        sudo sh -c 'echo -e "[azure-cli]\nname=Azure CLI\nbaseurl=https://packages.microsoft.com/yumrepos/azure-cli\nenabled=1\ngpgcheck=1\ngpgkey=https://packages.microsoft.com/keys/microsoft.asc" > /etc/yum.repos.d/azure-cli.repo'
        sudo yum install -y azure-cli
      else
        fail "Unsupported Linux package manager. Install az: https://learn.microsoft.com/cli/azure/install-azure-cli"
      fi
      ;;
    Darwin)
      have brew || fail "Install Homebrew first: https://brew.sh"
      brew install azure-cli
      ;;
    *) fail "Unsupported OS: $OS" ;;
  esac
fi

# ---------------------------------------------------------------------------
# 2) Terraform >= 1.9
# ---------------------------------------------------------------------------
if have terraform; then
  log "terraform already installed: $(terraform version -json 2>/dev/null | python3 -c 'import json,sys;print(json.load(sys.stdin)["terraform_version"])' 2>/dev/null || terraform version | head -1)"
else
  case "$ARCH" in
    x86_64|amd64) TF_ARCH="amd64" ;;
    aarch64|arm64) TF_ARCH="arm64" ;;
    *) fail "Unsupported architecture: $ARCH" ;;
  esac
  TF_VERSION="1.13.3"
  TMP="$(mktemp -d)"
  log "Installing terraform ${TF_VERSION}..."
  curl -sSL -o "$TMP/tf.zip" "https://releases.hashicorp.com/terraform/${TF_VERSION}/terraform_${TF_VERSION}_${OS,,}_${TF_ARCH}.zip"
  unzip -q "$TMP/tf.zip" -d "$TMP"
  sudo mv "$TMP/terraform" /usr/local/bin/ 2>/dev/null || sudo install "$TMP/terraform" /usr/local/bin/
  rm -rf "$TMP"
fi

# ---------------------------------------------------------------------------
# 3) Node.js >= 20 + npm
# ---------------------------------------------------------------------------
if have node && [ "$(node -p 'process.versions.node.split(".")[0]')" -ge 20 ]; then
  log "node already installed: $(node --version)"
else
  case "$OS" in
    Linux)
      if have apt-get; then
        curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash -
        sudo apt-get install -y nodejs
      elif have dnf; then sudo dnf install -y nodejs npm
      elif have yum; then sudo yum install -y nodejs npm
      else fail "Install Node.js >= 20 manually: https://nodejs.org"; fi
      ;;
    Darwin) have brew || fail "Install Homebrew first: https://brew.sh"; brew install node ;;
  esac
fi

# ---------------------------------------------------------------------------
# 4) .NET SDK 10 (used by the daemonless image build fallback + tests)
# ---------------------------------------------------------------------------
if have dotnet && dotnet --list-sdks 2>/dev/null | grep -q "10\."; then
  log ".NET SDK 10 already installed: $(dotnet --version)"
else
  log "Installing .NET SDK 10 (dotnet-install script)..."
  TMP="$(mktemp -d)"
  curl -sSL https://dot.net/v1/dotnet-install.sh -o "$TMP/dotnet-install.sh"
  chmod +x "$TMP/dotnet-install.sh"
  if [ "$EUID" -eq 0 ]; then
    "$TMP/dotnet-install.sh" --channel 10.0 --install-dir /usr/share/dotnet
    ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
  else
    "$TMP/dotnet-install.sh" --channel 10.0
    log "NOTE: .NET installed to ~/.dotnet — ensure ~/.dotnet is in your PATH."
  fi
  rm -rf "$TMP"
fi

# ---------------------------------------------------------------------------
# 5) crane (container push without Docker)
# ---------------------------------------------------------------------------
if have crane; then
  log "crane already installed: $(crane version)"
else
  case "$ARCH" in
    x86_64|amd64) CRANE_ARCH="x86_64" ;;
    aarch64|arm64) CRANE_ARCH="arm64" ;;
  esac
  CRANE_VERSION="v0.20.3"
  TMP="$(mktemp -d)"
  log "Installing crane ${CRANE_VERSION}..."
  curl -sSL -o "$TMP/crane.tar.gz" \
    "https://github.com/google/go-containerregistry/releases/download/${CRANE_VERSION}/go-containerregistry_${OS}_${CRANE_ARCH}.tar.gz"
  tar xzf "$TMP/crane.tar.gz" -C "$TMP" crane
  sudo mv "$TMP/crane" /usr/local/bin/ 2>/dev/null || sudo install "$TMP/crane" /usr/local/bin/
  rm -rf "$TMP"
fi

# ---------------------------------------------------------------------------
# 6) git (needed to clone this repo; usually preinstalled)
# ---------------------------------------------------------------------------
have git || { case "$OS" in
  Linux) have apt-get && sudo apt-get install -y git ;;
  Darwin) have brew && brew install git ;;
esac; }

# ---------------------------------------------------------------------------
# Summary check
# ---------------------------------------------------------------------------
echo
log "=== Verification ==="
MISSING=""
for tool in az terraform node npm dotnet crane git; do
  if have "$tool"; then
    printf '  ✔ %-10s %s\n' "$tool" "$($tool --version 2>/dev/null | head -1 | cut -c1-40)"
  else
    printf '  ✘ %-10s MISSING\n' "$tool"
    MISSING="$MISSING $tool"
  fi
done
echo
if [ -n "$MISSING" ]; then
  fail "Still missing:$MISSING — install manually (see docs/GETTING-STARTED.md §2), then re-run."
fi
log "All prerequisites installed. Next steps:"
log "  1. az login                # sign in to Azure"
log "  2. ./scripts/deploy.sh     # single-click deploy"
log "  3. ./scripts/destroy.sh    # when done"
