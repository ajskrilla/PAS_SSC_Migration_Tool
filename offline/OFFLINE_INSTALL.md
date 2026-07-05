# Offline / Air-Gapped Install

This bundle installs the PAS → Secret Server Migration Tool on a host with **no internet
access**. It ships prebuilt container images and pre-downloaded AI models, so the air-gapped
host never pulls images, restores NuGet/npm packages, or downloads Ollama models.

There are two sides:

- **Build side** (internet-connected): you run `make-offline-bundle.sh` once to produce a single
  portable file.
- **Install side** (air-gapped): the customer extracts that file and runs `install-offline.sh`.

---

## 1. Build the bundle (internet-connected machine)

Prereqs: Docker, Docker Compose v2, ~10–15 GB free disk, this repo checked out.

```bash
./offline/make-offline-bundle.sh
```

This will:
1. `docker compose build` the `api` and `frontend` images.
2. Pull the base images (`pgvector/pgvector:pg17`, `ollama/ollama:latest`).
3. Bake the Ollama models (`qwen2.5:3b` + `nomic-embed-text`) into a volume and export it
   (this step downloads several GB — it's the bulk of the bundle).
4. `docker save` all images.
5. Stage the runtime files (compose files, DB migrations, env template, install script).
6. Pack everything into **`offline/dist/pas-offline-bundle.tar.gz`**.

To bundle different models, set the env vars before running:

```bash
OLLAMA_CHAT_MODEL=qwen2.5:3b OLLAMA_EMBED_MODEL=nomic-embed-text ./offline/make-offline-bundle.sh
```

> The bundle is large (several GB, mostly the model weights). Transfer it to the air-gapped
> host by whatever one-way/removable-media process your environment allows.

---

## 2. Install on the air-gapped host

Prereqs: Docker + Docker Compose v2. **Nothing else** — no SDK, Node, or internet.

```bash
# 1. Extract the bundle
tar -xzf pas-offline-bundle.tar.gz

# 2. Run the installer (it lives in repo/)
cd repo
./install-offline.sh
```

The installer will:
1. `docker load` all images from the bundle.
2. Create `.env` from the template (if absent) and pin the offline models.
3. Restore the baked Ollama models into the `pas-migration_ollama` volume.
4. Start the stack with the offline overlay (which disables the model-pull step and uses the
   prebuilt images — no build, no pull).

### Before production use

Edit `repo/.env` and set:
- `POSTGRES_PASSWORD` — a strong password (the default is a dev placeholder).
- `ASPNETCORE_ENVIRONMENT=Production`.

Then restart:

```bash
docker compose -f docker-compose.yml -f docker-compose.offline.yml up -d
```

---

## 3. Verify

```bash
# All services up? (db + ollama healthy, api + frontend running, ollama-init exited 0)
docker compose -f docker-compose.yml -f docker-compose.offline.yml ps

# API logs (first start runs DB migrations automatically)
docker compose -f docker-compose.yml -f docker-compose.offline.yml logs -f api

# Confirm the models are present in the volume
docker compose -f docker-compose.yml -f docker-compose.offline.yml exec ollama ollama list
```

Open **https://\<host\>/** — the cert is self-signed (generated locally at startup), so expect a
browser warning; proceed past it. After a frontend start, **hard-refresh (Ctrl+Shift+R)** to avoid
a stale cached bundle.

---

## Operational notes

- **Always use both compose files together** on the air-gapped host:
  `-f docker-compose.yml -f docker-compose.offline.yml`. The overlay is what keeps it offline.
- **Never run `up --build`** on the air-gapped host — it would try to build (and fail, no
  internet). Plain `up -d` reuses the loaded images.
- **Updating the deployment** = build a new bundle on the connected side and re-run the installer;
  `docker load` replaces the images, and `up -d` recreates the changed containers. Data in the
  `pgdata` volume and models in the `ollama` volume are preserved.
- **`BUNDLE_MANIFEST.txt`** (inside the bundle) records exactly which images and models it carries,
  and when it was built — useful for change control / audit.
- The bundle does **not** include application source — images are prebuilt. The `db/migrations`
  are included because they run at first DB startup.
