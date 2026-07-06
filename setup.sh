#!/usr/bin/env bash
# setup.sh - prerequisite check + first-run setup for the PAS -> Secret Server migration platform.
# Works on Rocky/RHEL/Ubuntu/macOS. Requires only Docker + the Compose plugin.
set -euo pipefail

# ---------- pretty output ----------
RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[0;33m'; BOLD=$'\033[1m'; NC=$'\033[0m'
info()  { echo "${GREEN}[ok]${NC}   $*"; }
warn()  { echo "${YELLOW}[warn]${NC} $*"; }
err()   { echo "${RED}[fail]${NC} $*" >&2; }
step()  { echo; echo "${BOLD}==> $*${NC}"; }

NO_AI=0
DETACH="-d"
for arg in "$@"; do
  case "$arg" in
    --no-ai)     NO_AI=1 ;;
    --foreground) DETACH="" ;;
    -h|--help)
      echo "Usage: ./setup.sh [--no-ai] [--foreground]"
      echo "  --no-ai       skip starting the local Ollama AI containers"
      echo "  --foreground  run docker compose in the foreground (stream logs)"
      exit 0 ;;
    *) err "unknown option: $arg"; exit 1 ;;
  esac
done

cd "$(dirname "$0")"

# ---------- 1. Docker present? ----------
step "Checking Docker"
if ! command -v docker >/dev/null 2>&1; then
  err "Docker is not installed or not on PATH."
  echo "    Install Docker Engine: https://docs.docker.com/engine/install/"
  echo "    On Rocky/RHEL 10 (per https://docs.rockylinux.org/10/gemstones/containers/docker/):"
  echo "      sudo dnf -y install dnf-plugins-core"
  echo "      sudo dnf config-manager --add-repo https://download.docker.com/linux/rhel/docker-ce.repo"
  echo "      sudo dnf -y install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin"
  echo "      sudo systemctl --now enable docker"
  echo "    Then let your user run docker without sudo:"
  echo "      sudo usermod -aG docker \"\$USER\""
  echo "      newgrp docker        # apply the group in THIS shell (or log out and back in)"
  echo "    Verify:  docker run --rm hello-world"
  exit 1
fi
info "docker found: $(docker --version)"

# ---------- 2. Docker daemon reachable? ----------
if ! docker info >/dev/null 2>&1; then
  err "The Docker daemon isn't reachable."
  echo "    Start it:  sudo systemctl start docker"
  echo "    Or add yourself to the docker group so sudo isn't needed:"
  echo "      sudo usermod -aG docker \"\$USER\""
  echo "      newgrp docker   # apply it in this shell now, or log out and back in"
  exit 1
fi
info "docker daemon is running"

# ---------- 3. Compose v2 plugin? ----------
step "Checking Docker Compose"
if ! docker compose version >/dev/null 2>&1; then
  err "The Docker Compose v2 plugin is missing (the 'docker compose' subcommand)."
  echo "    On Rocky/RHEL:  sudo dnf -y install docker-compose-plugin"
  exit 1
fi
info "compose found: $(docker compose version | head -1)"

# ---------- 4. RAM advisory (local AI is memory-hungry) ----------
step "Checking host resources"
TOTAL_GB=""
if [ -r /proc/meminfo ]; then
  TOTAL_GB=$(awk '/MemTotal/ {printf "%.0f", $2/1024/1024}' /proc/meminfo)
elif command -v sysctl >/dev/null 2>&1; then  # macOS
  TOTAL_GB=$(( $(sysctl -n hw.memsize) / 1024 / 1024 / 1024 ))
fi
if [ -n "$TOTAL_GB" ]; then
  if [ "$NO_AI" -eq 0 ] && [ "$TOTAL_GB" -lt 8 ]; then
    warn "Detected ${TOTAL_GB}GB RAM. The local AI model needs ~5-6GB free."
    warn "Consider a smaller model (set OLLAMA_CHAT_MODEL=llama3.2:3b in .env) or run with --no-ai."
  else
    info "detected ${TOTAL_GB}GB RAM"
  fi
fi

# ---------- 4b. Port availability ----------
# Only the nginx frontend publishes host ports now (80 redirect + 443 TLS). Postgres,
# the API, and Ollama are compose-network-internal, so native services on 5432/8080/11434
# no longer conflict.
step "Checking host ports"
port_in_use() {
  _p="$1"
  if command -v ss >/dev/null 2>&1; then
    ss -ltn 2>/dev/null | awk '{print $4}' | grep -Eq "[:.]${_p}\$" && return 0
    return 1
  elif command -v lsof >/dev/null 2>&1; then
    lsof -iTCP:"${_p}" -sTCP:LISTEN >/dev/null 2>&1 && return 0
    return 1
  else
    # No tools: try to connect to the port via bash's /dev/tcp. A successful
    # connect means something is listening. (bash-only; sh may lack /dev/tcp.)
    (exec 3<>"/dev/tcp/127.0.0.1/${_p}") >/dev/null 2>&1 && { exec 3>&- 3<&-; return 0; }
    return 1
  fi
}
PORTS="80 443"
PORT_CLASH=0
for p in $PORTS; do
  if port_in_use "$p"; then
    PORT_CLASH=1
    err "Port $p is already in use by another process."
    echo "    Stop whatever is using it, or change the host port in docker-compose.yml."
  fi
done
if [ "$PORT_CLASH" -eq 1 ]; then
  err "Resolve the port conflict(s) above, then run ./setup.sh again."
  exit 1
fi
info "required host ports are free"

# ---------- 5. .env ----------
step "Preparing environment file (.env)"
if [ -f .env ]; then
  info ".env already exists - leaving it untouched"
  # Upgrade path: older installs predate AUTH_JWT_SECRET (now mandatory). Add it if missing.
  if ! grep -q "^AUTH_JWT_SECRET=..*" .env; then
    if command -v openssl >/dev/null 2>&1; then
      GENSECRET=$(openssl rand -base64 48 | tr -d '/+=')
      # Remove an empty placeholder line if present, then append the generated value.
      if sed --version >/dev/null 2>&1; then
        sed -i '/^AUTH_JWT_SECRET=$/d' .env
      else
        sed -i '' '/^AUTH_JWT_SECRET=$/d' .env
      fi
      printf '\nAUTH_JWT_SECRET=%s\n' "$GENSECRET" >> .env
      info "added generated AUTH_JWT_SECRET to existing .env (now required)"
      warn "any previously stored tenant credentials were encrypted under the old dev secret"
      echo "    and must be re-entered once on the Pre-migration page after startup."
    else
      err "existing .env lacks AUTH_JWT_SECRET and openssl is unavailable to generate one."
      echo "    Set AUTH_JWT_SECRET (32+ random chars) in .env, then re-run."
      exit 1
    fi
  fi
else
  cp .env.example .env
  # Generate a strong DB password and the mandatory app secret (AUTH_JWT_SECRET signs JWTs
  # and derives the credential-encryption key — the stack refuses to start without it).
  if command -v openssl >/dev/null 2>&1; then
    GENPW=$(openssl rand -base64 24 | tr -d '/+=' | cut -c1-24)
    GENSECRET=$(openssl rand -base64 48 | tr -d '/+=')
    # portable in-place edit (GNU + BSD sed)
    if sed --version >/dev/null 2>&1; then
      sed -i "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=${GENPW}|" .env
      sed -i "s|^AUTH_JWT_SECRET=.*|AUTH_JWT_SECRET=${GENSECRET}|" .env
    else
      sed -i '' "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=${GENPW}|" .env
      sed -i '' "s|^AUTH_JWT_SECRET=.*|AUTH_JWT_SECRET=${GENSECRET}|" .env
    fi
    info "created .env with a generated database password and app secret"
  else
    err "openssl not found - cannot generate AUTH_JWT_SECRET, which the stack requires."
    echo "    Install openssl, or manually set AUTH_JWT_SECRET (32+ random chars) in .env."
    exit 1
  fi
fi

# ---------- 6. Bring the stack up ----------
step "Building and starting containers"
if [ "$NO_AI" -eq 1 ]; then
  info "skipping AI containers (--no-ai)"
  docker compose up --build $DETACH db api frontend
else
  info "starting full stack incl. local AI (first run pulls the model - this can take several minutes)"
  docker compose up --build $DETACH
fi

if [ -n "$DETACH" ]; then
  echo
  info "Stack is starting in the background."
  echo "    Frontend:    https://localhost  (self-signed cert - accept the browser warning)"
  echo "    API health:  curl -k https://localhost/health/ready"
  echo "    Logs:        docker compose logs -f"
  echo "    Stop:        docker compose down        (keeps data)"
  echo "    Reset DB:    docker compose down -v     (wipes the database volume)"
fi
