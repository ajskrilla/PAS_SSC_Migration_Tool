#!/usr/bin/env bash
#
# install-offline.sh — install the PAS Migration Tool on an AIR-GAPPED host.
#
# Run this AFTER extracting pas-offline-bundle.tar.gz. It loads the prebuilt images,
# restores the Ollama models into a volume, and starts the stack — with NO internet:
# no docker pull, no build, no model download.
#
# Prereqs on the air-gapped host: docker, docker compose v2. Nothing else.
#
# Layout expected (produced by make-offline-bundle.sh, after extraction):
#   ./images/images.tar.gz          <- all container images
#   ./images/ollama-models.tar.gz   <- baked Ollama models
#   ./repo/docker-compose.yml
#   ./repo/docker-compose.offline.yml
#   ./repo/.env.example
#   ./repo/db/migrations/*
#   ./repo/BUNDLE_MANIFEST.txt

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# The bundle extracts to a dir containing images/ and repo/. This script ships inside repo/,
# so the bundle root is one level up.
BUNDLE_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
IMAGES_DIR="$BUNDLE_ROOT/images"
REPO_DIR="$SCRIPT_DIR"

COMPOSE_PROJECT="pas-migration"
# The compose network prefixes volumes with the project name: <project>_ollama.
OLLAMA_VOLUME="${COMPOSE_PROJECT}_ollama"

echo "==> PAS Migration Tool — offline install"
if [ -f "$REPO_DIR/BUNDLE_MANIFEST.txt" ]; then
  echo ""
  cat "$REPO_DIR/BUNDLE_MANIFEST.txt"
  echo ""
fi

# ── 0. Sanity: docker present, files present ──────────────────────────────────
command -v docker >/dev/null || { echo "ERROR: docker not found on this host."; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "ERROR: docker compose v2 not found."; exit 1; }
[ -f "$IMAGES_DIR/images.tar.gz" ]        || { echo "ERROR: missing images/images.tar.gz"; exit 1; }
[ -f "$IMAGES_DIR/ollama-models.tar.gz" ] || { echo "ERROR: missing images/ollama-models.tar.gz"; exit 1; }

# ── 1. Load images ────────────────────────────────────────────────────────────
echo "==> [1/4] Loading container images (this can take a minute)..."
gunzip -c "$IMAGES_DIR/images.tar.gz" | docker load

# ── 2. Prepare .env ───────────────────────────────────────────────────────────
echo "==> [2/4] Preparing .env..."
if [ ! -f "$REPO_DIR/.env" ]; then
  cp "$REPO_DIR/.env.example" "$REPO_DIR/.env"
  # Force the offline models so the app matches the baked volume regardless of the
  # template's defaults.
  {
    echo ""
    echo "# --- set by install-offline.sh ---"
    echo "OLLAMA_CHAT_MODEL=qwen2.5:3b"
    echo "OLLAMA_EMBED_MODEL=nomic-embed-text"
  } >> "$REPO_DIR/.env"
  echo "    created .env from template."
  echo "    >>> IMPORTANT: edit $REPO_DIR/.env and set a strong POSTGRES_PASSWORD"
  echo "        (and set ASPNETCORE_ENVIRONMENT=Production) before production use."
else
  echo "    .env already exists — leaving it as-is."
fi

# ── 3. Restore the Ollama model volume ────────────────────────────────────────
# Create the named volume compose will use, then unpack the baked models into it,
# so Ollama starts already holding qwen2.5:3b + nomic-embed-text (no pull).
echo "==> [3/4] Restoring Ollama models into volume '$OLLAMA_VOLUME'..."
docker volume create "$OLLAMA_VOLUME" >/dev/null
# Use the just-loaded ollama image as the helper (guaranteed present, no external pull).
docker run --rm \
  -v "$OLLAMA_VOLUME:/root/.ollama" \
  -v "$IMAGES_DIR:/in:ro" \
  --entrypoint sh \
  ollama/ollama:latest \
  -c "cd /root/.ollama && tar xzf /in/ollama-models.tar.gz"
echo "    models restored."

# ── 4. Start the stack (offline override disables the model-pull init service) ─
echo "==> [4/4] Starting the stack..."
cd "$REPO_DIR"
docker compose -f docker-compose.yml -f docker-compose.offline.yml up -d

echo ""
echo "==> DONE. The stack is starting."
echo "    - Web UI:   https://<this-host>/   (self-signed cert; expect a browser warning)"
echo "    - Check:    docker compose -f docker-compose.yml -f docker-compose.offline.yml ps"
echo "    - Logs:     docker compose -f docker-compose.yml -f docker-compose.offline.yml logs -f api"
echo ""
echo "    First start runs DB migrations automatically. Give it ~30-60s, then hard-refresh the UI."
