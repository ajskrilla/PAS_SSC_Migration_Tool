# Running the platform

Step-by-step commands to compile and run the whole stack (database, API, frontend, and the
local AI), plus the URLs to open and how to stop or reset. Everything runs in Docker — you do
not need .NET, Node, or PostgreSQL installed on the host.

---

## 0. Prerequisites (one time)

You only need **Docker**.

**Rocky / RHEL 10:**
```bash
sudo dnf -y install dnf-plugins-core
sudo dnf config-manager --add-repo https://download.docker.com/linux/centos/docker-ce.repo
sudo dnf -y install docker-ce docker-ce-cli containerd.io docker-compose-plugin
sudo systemctl enable --now docker
# optional: run docker without sudo (log out/in afterwards)
sudo usermod -aG docker "$USER"
```

**Windows:** install Docker Desktop, launch it, and wait until it says "running."
<https://docs.docker.com/desktop/install/windows-install/>

Verify Docker is ready:
```bash
docker --version
docker compose version
docker info        # should print without error
```

---

## 1. Start everything (the easy way)

From the project folder:

**Linux / macOS:**
```bash
./setup.sh
```

**Windows (PowerShell):**
```powershell
.\setup.ps1
```

This checks prerequisites, writes a `.env` with a generated database password, builds the
images, and starts all containers in the background. The **first run takes several minutes** —
it pulls the .NET SDK image, installs the frontend packages, and downloads ~5 GB of AI models.
Later runs start in seconds.

That's it — skip to [section 4](#4-open-the-website) for the URL.

---

## 2. Start everything (manual, if you prefer)

If you want to run the steps yourself instead of the script:

```bash
# 1. create your env file and set a database password
cp .env.example .env
#   (edit .env and change POSTGRES_PASSWORD)

# 2. build the images (compiles the .NET API and the React frontend inside Docker)
docker compose build

# 3. start all services in the background
docker compose up -d
```

To build and start in one command:
```bash
docker compose up -d --build
```

To watch the logs while it starts (foreground):
```bash
docker compose up --build
#   press Ctrl+C to stop when run this way
```

---

## 3. Watch it come up

The first run downloads the AI models; track progress with:
```bash
docker compose logs -f ollama-init      # model download progress
docker compose logs -f api              # API startup
docker compose ps                       # status of every container
```

The stack is ready when `docker compose ps` shows `db`, `api`, and `frontend` as **running**
(and `healthy` where a healthcheck is defined). `ollama-init` will show **exited (0)** once the
models finish downloading — that is expected; it is a one-shot job.

Confirm the API is live (through the nginx proxy — the API no longer publishes a host port):
```bash
curl -k https://localhost/health/ready
# -> {"status":"ready"}
```

---

## 4. Open the website

| What            | URL                                      |
|-----------------|------------------------------------------|
| **Web app**     | <https://localhost> (self-signed cert — accept the browser warning; port 80 redirects here) |
| API health      | `curl -k https://localhost/health/ready` |
| API             | `https://localhost/api/...` (via nginx; requires login) |
| Local AI (Ollama)| compose-internal only (`http://ollama:11434` from the api container) |
| PostgreSQL      | compose-internal only — `docker compose exec db psql -U pasmig -d pasmig` |

Only nginx (80/443) publishes host ports. Postgres, the API, and Ollama are reachable solely on
the compose network — nothing else on the LAN can hit them directly.

Open **<https://localhost>** in a browser. The Overview page shows an "API ready" badge once
the frontend can reach the backend.

> Running on a remote VM (e.g. the Rocky server over SSH)? Either browse from the VM's own
> desktop, or forward the port from your workstation:
> ```bash
> ssh -L 8443:localhost:443 user@your-vm
> ```
> then open <https://localhost:8443> on your workstation.

---

## 5. Common operations

```bash
# stop everything (keeps the database data)
docker compose down

# stop AND wipe the database + AI model volumes (full reset)
docker compose down -v

# restart after a code change (rebuilds only what changed)
docker compose up -d --build

# rebuild a single service from scratch
docker compose build --no-cache api
docker compose up -d api

# tail logs for one service
docker compose logs -f api

# open a shell in a running container
docker compose exec api /bin/sh
docker compose exec db psql -U pasmig -d pasmig
```

---

## 6. Run without the local AI

The AI containers are the heaviest part. To run everything except them:

**Linux / macOS:** `./setup.sh --no-ai`
**Windows:** `.\setup.ps1 -NoAi`

Or manually:
```bash
docker compose up -d --build db api frontend
```

---

## 7. Troubleshooting

- **`docker: permission denied`** — your user isn't in the `docker` group. Run with `sudo`, or
  `sudo usermod -aG docker "$USER"` and log out/in.
- **Port already in use** — something else is on 80/443 (the only published ports). Stop it, or
  change the left-hand (host) port in `docker-compose.yml`, e.g. `"8443:8443"`. A native
  Ollama or Postgres on the host no longer conflicts — those services are compose-internal.
- **API shows "db not ready"** — the database is still starting; wait a few seconds and retry
  `curl -k https://localhost/health/ready`.
- **AI responses are slow** — expected on CPU. Set `OLLAMA_CHAT_MODEL=llama3.2:3b` in `.env` and
  run `docker compose down && docker compose up -d` to pull a smaller, faster model.
- **Database changes not taking effect** — schema migrations only run on a fresh volume. After
  editing files in `db/migrations/`, run `docker compose down -v` then `up` again.

## HTTPS (added)

The frontend now serves over HTTPS on port 443 with a self-signed certificate generated
inside the container at first start. Access the app at:

- **https://localhost** (or https://<vm-ip>)
- Port 80 redirects to 443.

Because the cert is self-signed, the browser will show a one-time security warning — accept it
to proceed (expected for a lab). Replace with a CA-issued cert for production.

Session credentials: after a green Test connection, credentials are cached in the API's memory
for 60 minutes of inactivity, so you don't re-enter them for inventory/migration in the same
session. They are never written to disk and are cleared on container restart.
