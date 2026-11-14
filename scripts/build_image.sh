#!/usr/bin/env bash
# ============================================================================
#  Build + push the Orchestration Agent container image WITHOUT Docker,
#  WITHOUT buildah and WITHOUT ACR Tasks — for sandbox subscriptions.
#
#  Technique (pure OCI assembly):
#    1. dotnet publish (self-contained linux-x64)
#    2. crane pull --format=oci the official .NET runtime-deps base image
#    3. python3 assembles a new layer (the /app files) into the OCI layout,
#       patching manifest + config (diff_ids/history)
#    4. crane push the OCI layout to ACR
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ACR_SERVER="${1:?usage: build_image.sh <acr-login-server>}"
IMAGE_TAG="orchestrator-api:latest"
WORK=/tmp/opencode/image-build

log()  { printf '\033[1;34m[image]\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31m[image:ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

command -v crane >/dev/null || fail "crane not found"
command -v dotnet >/dev/null || fail "dotnet not found"
command -v python3 >/dev/null || fail "python3 not found"

rm -rf "$WORK" && mkdir -p "$WORK"

log "Publishing self-contained linux-x64..."
dotnet publish "$ROOT_DIR/backend/DeployOrchestrator.Api/DeployOrchestrator.Api.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -o "$WORK/app" >/dev/null

log "Pulling base image (OCI layout)..."
crane pull --platform linux/amd64 --format=oci \
  "mcr.microsoft.com/dotnet/runtime-deps:10.0" "$WORK/oci"

log "Assembling new layer + patching manifest/config..."
python3 - "$WORK" "$ROOT_DIR" <<'PYEOF'
import hashlib, io, json, os, shutil, subprocess, sys, tarfile, time

work, root = sys.argv[1], sys.argv[2]
oci = os.path.join(work, "oci")
blobs = os.path.join(oci, "blobs", "sha256")

def digest_bytes(b):
    return "sha256:" + hashlib.sha256(b).hexdigest()

def write_blob(data):
    d = digest_bytes(data)
    with open(os.path.join(blobs, d.split(":")[1]), "wb") as f:
        f.write(data)
    return d, len(data)

index = json.load(open(os.path.join(oci, "index.json")))
mani_digest = index["manifests"][0]["digest"].split(":")[1]
manifest = json.load(open(os.path.join(blobs, mani_digest)))
config_digest = manifest["config"]["digest"].split(":")[1]
config = json.load(open(os.path.join(blobs, config_digest)))

# ---- build layer tar.gz from the publish output (/app) ----
app_dir = os.path.join(work, "app")
tar_path = os.path.join(work, "layer.tar")
with tarfile.open(tar_path, "w") as tar:
    for dirpath, dirnames, filenames in os.walk(app_dir):
        for name in filenames + dirnames:
            src = os.path.join(dirpath, name)
            rel = os.path.join("app", os.path.relpath(src, app_dir))
            ti = tar.gettarinfo(src, arcname=rel)
            ti.uid = ti.gid = 0
            ti.uname = ti.gname = ""
            if ti.isfile():
                # App Service-style app dir: give all files exec/read (harmless, avoids exec-bit misses)
                ti.mode = 0o755
                with open(src, "rb") as f:
                    tar.addfile(ti, f)
            else:
                ti.mode = 0o755
                tar.addfile(ti)

uncompressed = open(tar_path, "rb").read()
diff_id = digest_bytes(uncompressed)

gz_path = tar_path + ".gz"
with open(gz_path, "wb") as f:
    p = subprocess.Popen(["gzip", "-9", "-n"], stdin=open(tar_path, "rb"), stdout=f)
    p.wait()
gz = open(gz_path, "rb").read()
layer_digest, layer_size = write_blob(gz)

# ---- patch config ----
config.setdefault("rootfs", {"type": "layers", "diff_ids": []})
config["rootfs"]["diff_ids"].append(diff_id)
config.setdefault("history", []).append({
    "created": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    "created_by": "scripts/build_image.sh (dotnet publish /app)",
})
config["config"] = config.get("config", {})
config["config"]["WorkingDir"] = "/app"
config["config"]["Env"] = list(config["config"].get("Env") or []) + [
    "ASPNETCORE_URLS=http://+:8080",
    "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false",
]
config["config"]["Entrypoint"] = ["/app/DeployOrchestrator.Api"]
config["config"]["ExposedPorts"] = {"8080/tcp": {}}

cfg_data = json.dumps(config, separators=(",", ":")).encode()
cfg_digest, cfg_size = write_blob(cfg_data)

# ---- patch manifest ----
manifest["config"] = {"mediaType": "application/vnd.docker.container.image.v1+json",
                      "size": cfg_size, "digest": cfg_digest}
manifest["layers"].append({
    "mediaType": "application/vnd.docker.image.rootfs.diff.tar.gzip",
    "size": layer_size, "digest": layer_digest,
})

mani_data = json.dumps(manifest, separators=(",", ":")).encode()
new_mani_digest, new_mani_size = write_blob(mani_data)
index["manifests"] = [{
    "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
    "digest": new_mani_digest, "size": new_mani_size,
    "platform": {"architecture": "amd64", "os": "linux"},
}]
json.dump(index, open(os.path.join(oci, "index.json"), "w"), separators=(",", ":"))
print("layer:", layer_digest[:19], "config:", cfg_digest[:19])
PYEOF

log "Authenticating to ACR..."
ACR_NAME="${ACR_SERVER%%.*}"
az acr login --name "$ACR_NAME" >/dev/null 2>&1 || true
ACR_USER="$(az acr credential show -n "$ACR_NAME" --query username -o tsv)"
ACR_PASS="$(az acr credential show -n "$ACR_NAME" --query passwords[0].value -o tsv)"
mkdir -p ~/.docker
AUTH="$(printf '%s:%s' "$ACR_USER" "$ACR_PASS" | base64 -w0)"
cat > ~/.docker/config.json <<EOF
{ "auths": { "$ACR_SERVER": { "auth": "$AUTH" } } }
EOF

log "Pushing image to $ACR_SERVER/$IMAGE_TAG ..."
crane push "$WORK/oci" "$ACR_SERVER/$IMAGE_TAG"
log "Image pushed successfully."
