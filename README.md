# PAS → Secret Server Migration Platform

Containerized full-stack platform to migrate customers from **Delinea/Centrify PAS** to
**Delinea Secret Server** (with the Delinea Platform as IdP). Covers the full lifecycle —
pre-migration inventory/health check, orchestrated migration, post-migration verification —
with analytics at each stage and an embedded, advisory, read-only AI assistant.

This is the **initial scaffold** (build sequence step 1 + connector skeletons). See the master
context document for the full vision, API references, and roadmap.

> **Just want to get it running?** See [RUNNING.md](RUNNING.md) for the full command set,
> URLs, and troubleshooting.

## Stack

- **Frontend** — React 19 + Vite + TypeScript SPA (`frontend/`)
- **API / orchestration** — C# / .NET 10, modular monolith, Hangfire for resumable jobs (`src/`)
- **Database** — PostgreSQL 17 + pgvector (`db/migrations/`)
- **AI** — `ILlmProvider` abstraction; Ollama (local, default) or Azure OpenAI (opt-in)
- **Packaging** — docker-compose; non-root, chiseled/distroless runtime images

## Layout

```
src/                 .NET modular monolith (single project, module folders)
  Api/               host, minimal-API endpoints, Dockerfile, auth, Hangfire wiring
  Connectors/        PasConnector, SecretServerConnector, TenantCredentials (in-memory only)
  Inventory/         pre-migration snapshots + reconciliation (next)
  Migration/         orchestrator (next)
  Verification/      fidelity checks (next)
  Ai/                ILlmProvider abstraction + RAG (next)
db/migrations/       001_core, 002_migration, 003_ai — run automatically on first DB start
frontend/            React SPA shell with phase navigation
docker-compose.yml   db + api + frontend
```

## Prerequisites

The only thing a customer needs to install is **Docker** — everything else runs in containers.

- **Linux (Rocky/RHEL/Ubuntu) / macOS:** Docker Engine + the Compose v2 plugin.
- **Windows:** Docker Desktop (WSL2 backend recommended).

Recommended host: 4 CPU cores and 16 GB RAM (the local AI model needs ~5–6 GB free). On smaller
hosts, use a smaller model or run without AI (see below).

## Run locally

A setup script checks prerequisites, generates a secure database password into `.env`, and
brings the stack up.

**Linux / macOS:**
```bash
./setup.sh              # full stack incl. local AI
./setup.sh --no-ai      # skip the AI containers
./setup.sh --foreground # stream logs instead of detaching
```

**Windows (PowerShell):**
```powershell
.\setup.ps1             # full stack incl. local AI
.\setup.ps1 -NoAi       # skip the AI containers
.\setup.ps1 -Foreground # stream logs instead of detaching
```

If you'd rather not use the script:
```bash
cp .env.example .env    # then edit POSTGRES_PASSWORD
docker compose up --build
```

After it's up:
- Frontend: http://localhost:5173
- API health: http://localhost:8080/health/ready
- Postgres: localhost:5432
- Ollama (local AI): http://localhost:11434

Migrations in `db/migrations/` are applied on the first start of an empty volume. To re-run
them, remove the volume: `docker compose down -v`.

## Local AI (Ollama)

Runs entirely in a container so customer tenant data stays inside the boundary. On first start,
`ollama-init` pulls the chat model (`llama3.1:8b`, ~4.7 GB) and the embedding model
(`nomic-embed-text`) — this takes a few minutes and only happens once (cached in the `ollama`
volume). CPU-only by default; expect slower first responses on a 4-core host.

To trade quality for speed on constrained hosts, set in `.env` before first run:
```
OLLAMA_CHAT_MODEL=llama3.2:3b
```

## Pushing to GitHub

To push to your own remote (handle auth however you prefer — PAT over HTTPS or SSH key):
```bash
git init                 # if not already a repo
git add -A
git commit -m "Initial scaffold + setup scripts"
git branch -M main
git remote add origin https://github.com/<you>/<repo>.git
git push -u origin main
```
`.env` is gitignored, so generated credentials are never committed. For later changes, the
usual `git add -A && git commit -m "..." && git push` applies.

## Security model (non-negotiable)

- Tenant credentials are **in-memory only by default** — never persisted, never logged.
  Optional persistence uses envelope encryption (`encrypted_credential`); default flow leaves
  it unused.
- Secret material stays in memory, over TLS — **never written to the DB, never logged.**
- The database stores **metadata, status, hashes, and analytics only.**
- The write (migration) phase is gated behind **dry-run + explicit human confirmation**;
  pre/post phases are read-only.
- The AI layer is **advisory and read-only** and **never receives credentials, tokens, or
  secret values** — only metadata/analytics via explicit read-only tools.
- Containers run **non-root** on minimal/chiseled images with a read-only rootfs.

## Status / next steps

Per the build sequence: connectors are skeletoned (auth, RedRock query, retrieval, folder
search-or-create). Next: finish secret creation on the SS side (inline-base64 file fields —
pending the byte-fidelity open item), then pre-migration inventory + the first analytics
dashboard, then the migration orchestrator (folders + file secrets first), verification, and
the AI assistant (docs/verification RAG first).

### Verified in this scaffold
- All three SQL migrations apply cleanly on Postgres; the `migration_item` idempotency
  constraint correctly rejects duplicate re-runs.
- compose YAML, package/tsconfig JSON, and C# structure validated.

### Not yet verified (no network access to NuGet/npm in the build sandbox)
- `dotnet restore`/`build` and `npm install`/`build` must be run on your machine.
- pgvector `hnsw` index runs against the `pgvector/pgvector:pg17` image (not testable without
  the extension installed).
