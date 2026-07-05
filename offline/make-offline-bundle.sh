#!/usr/bin/env bash
#
# make-offline-bundle.sh — produce a fully self-contained, air-gapped install bundle.
#
# RUN THIS ON AN INTERNET-CONNECTED MACHINE that has Docker + this repo checked out.
# It builds/pulls every image, bakes the Ollama models into a saved volume, and packs
# everything into ./offline/dist/pas-offline-bundle.tar.gz — which is what you carry
# (USB / one-way transfer) to the air-gapped customer host.
#
# The customer side never touches the internet: no docker pull, no dotnet restore,
# no npm install, no ollama pull. See install-offline.sh + OFFLINE_INSTALL.md.
#
# Usage:
#   ./offline/make-offline-bundle.sh
#
# Prereqs on THIS machine: docker, docker compose v2, gzip, ~10-15 GB free disk.

set -euo pipefail

# ── Resolve paths ─────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
STAGE="$SCRIPT_DIR/stage"
DIST="$SCRIPT_DIR/dist"
BUNDLE="$DIST/pas-offline-bundle.tar.gz"

# Models to bake in (match what the assistant runs today).
CHAT_MODEL="${OLLAMA_CHAT_MODEL:-qwen2.5:3b}"
EMBED_MODEL="${OLLAMA_EMBED_MODEL:-nomic-embed-text}"

# Base images pulled by the build/runtime (kept in sync with the Dockerfiles + compose).
BASE_IMAGES=(
  "pgvector/pgvector:pg17"
  "ollama/ollama:latest"
)

# App images produced by `docker compose build` (compose project name is "pas-migration").
APP_IMAGES=(
  "pas-migration-api"
  "pas-migration-frontend"
)

echo "==> Offline bundle builder"
echo "    repo:        $REPO_ROOT"
echo "    chat model:  $CHAT_MODEL"
echo "    embed model: $EMBED_MODEL"
echo ""

rm -rf "$STAGE" "$DIST"
mkdir -p "$STAGE/images" "$STAGE/repo" "$DIST"

# ── 1. Build the application images ───────────────────────────────────────────
echo "==> [1/6] Building application images (docker compose build)..."
( cd "$REPO_ROOT" && docker compose build )

# ── 2. Pull the base images (db, ollama) ──────────────────────────────────────
echo "==> [2/6] Pulling base images..."
for img in "${BASE_IMAGES[@]}"; do
  echo "    pull $img"
  docker pull "$img"
done

# ── 3. Bake the Ollama models into a named volume ─────────────────────────────
# Spin up a throwaway ollama container, pull the models into its volume, then
# export that volume as a tarball. On the customer side we restore this volume so
# the models are present with zero network.
echo "==> [3/6] Baking Ollama models into a volume (this downloads several GB)..."
BAKE_VOL="pasoffline_ollama_bake"
docker volume rm "$BAKE_VOL" >/dev/null 2>&1 || true
docker volume create "$BAKE_VOL" >/dev/null

docker run -d --name pasoffline_ollama \
  -v "$BAKE_VOL:/root/.ollama" \
  ollama/ollama:latest >/dev/null

# Wait for the ollama server to be ready.
echo "    waiting for ollama to start..."
for i in $(seq 1 30); do
  if docker exec pasoffline_ollama ollama list >/dev/null 2>&1; then break; fi
  sleep 2
done

echo "    pulling $CHAT_MODEL"
docker exec pasoffline_ollama ollama pull "$CHAT_MODEL"
echo "    pulling $EMBED_MODEL"
docker exec pasoffline_ollama ollama pull "$EMBED_MODEL"

docker stop pasoffline_ollama >/dev/null
docker rm   pasoffline_ollama >/dev/null

# Export the volume contents to a tarball (portable, restored on the customer side).
echo "    exporting model volume..."
docker run --rm -v "$BAKE_VOL:/data:ro" -v "$STAGE/images:/out" \
  alpine:latest sh -c "cd /data && tar czf /out/ollama-models.tar.gz ." 2>/dev/null || \
docker run --rm -v "$BAKE_VOL:/data:ro" -v "$STAGE/images:/out" \
  "$(docker images --format '{{.Repository}}:{{.Tag}}' | grep -m1 alpine || echo alpine)" \
  sh -c "cd /data && tar czf /out/ollama-models.tar.gz ."
docker volume rm "$BAKE_VOL" >/dev/null

# ── 4. Save all images to tarballs ────────────────────────────────────────────
echo "==> [4/6] Saving images (docker save)..."
ALL_IMAGES=( "${BASE_IMAGES[@]}" )
for img in "${APP_IMAGES[@]}"; do
  # Only include app images that actually built (guards against a rename).
  if docker image inspect "$img" >/dev/null 2>&1; then
    ALL_IMAGES+=( "$img" )
  else
    echo "    WARNING: expected image '$img' not found — skipping. Check compose build output."
  fi
done
echo "    images: ${ALL_IMAGES[*]}"
docker save "${ALL_IMAGES[@]}" | gzip > "$STAGE/images/images.tar.gz"

# ── 5. Stage the repo bits the customer needs at runtime ──────────────────────
# Runtime needs: compose file, the offline override, db migrations, env template,
# and the install script + README. NOT the source tree (images are prebuilt).
echo "==> [5/6] Staging runtime files..."
cp "$REPO_ROOT/docker-compose.yml"        "$STAGE/repo/"
cp "$SCRIPT_DIR/docker-compose.offline.yml" "$STAGE/repo/"
cp "$SCRIPT_DIR/install-offline.sh"        "$STAGE/repo/"
cp "$SCRIPT_DIR/OFFLINE_INSTALL.md"        "$STAGE/repo/"
cp "$REPO_ROOT/.env.example"               "$STAGE/repo/"
cp -r "$REPO_ROOT/db"                      "$STAGE/repo/db"
chmod +x "$STAGE/repo/install-offline.sh"

# Record exactly which images/models this bundle carries (for the install script + audit).
cat > "$STAGE/repo/BUNDLE_MANIFEST.txt" <<EOF
PAS Migration Tool — offline bundle
built:       $(date -u +"%Y-%m-%dT%H:%M:%SZ")
chat model:  $CHAT_MODEL
embed model: $EMBED_MODEL
images:
$(for i in "${ALL_IMAGES[@]}"; do echo "  - $i"; done)
EOF

# ── 6. Pack the final bundle ──────────────────────────────────────────────────
echo "==> [6/6] Packing final bundle..."
tar -C "$STAGE" -czf "$BUNDLE" images repo

echo ""
echo "==> DONE"
echo "    bundle: $BUNDLE"
echo "    size:   $(du -h "$BUNDLE" | cut -f1)"
echo ""
echo "    Transfer this file to the air-gapped host, extract it, and run install-offline.sh."
echo "    (Extraction: tar -xzf pas-offline-bundle.tar.gz)"
