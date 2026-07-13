# PROJECT_CONTEXT.md — PAS → Secret Server Migration Tool

**Rebuilt from scratch on July 13, 2026, against a fresh, directly-inspected tarball —
not a delta from the previous version.** The previous document had drifted from reality:
it described SSO/SCIM work as partially delivered based on a *design draft* from an
earlier session, and that draft was never clearly separated from confirmed, deployed fact.
Every claim in this document was checked against an actual file, route, or migration in the
snapshot dated 2026-07-13 — nothing here is carried forward on trust alone.

**EDITING RULE (binding, unchanged in spirit, sharpened in practice):** this document is
cumulative — extend, don't replace. The new part: **anything not yet verified against
running/deployed code goes in §9 (Open / Future Work), never blended into §7 (Features
Completed).** If a design gets drafted before it's built, say so explicitly and keep it out
of the completed list until it's actually confirmed working.

---

## 0. Prime directives (apply to every code change)

1. **Tarball + git every turn.** Every response that changes code ends with a tarball
   (repo-root-relative paths, changed/new files only) and the full deploy block: extract →
   `git add -A && git commit -m "<specific message>" && git push` → rebuild whichever
   services changed.
2. **Subatomic functions.** Smallest meaningful single-purpose functions; a function whose
   description needs "and" gets split. Route handlers stay thin; logic lives in named
   helpers.
3. **Decouple via interfaces.** New services default to interface + implementation
   (`ILlmProvider`, repositories, `IExternalIdentityService`, `IPasClient`, ...).
4. **Clean data access layer.** SQL lives only in repositories; one table's SQL per file
   where practical; property-based records for rows with array columns (Dapper/Npgsql
   `text[]` gotcha — see §12).
5. **Clean code, no known vulnerabilities.** Authn/authz on every new endpoint (secure by
   default via the fallback policy), parameterized SQL only, secrets write-only and
   encrypted at rest, no secrets/stack traces in responses or logs.
6. **Async safety.** No `.Result`/`.Wait()`/`GetAwaiter().GetResult()`, no `async void`
   (event handlers excepted), every async signature accepts and forwards a
   `CancellationToken`, no fire-and-forget without explicit handling — request-scoped
   services in particular can't safely background work past the request's lifetime (their
   `IDbConnection` gets disposed with the scope).
7. **Verify before claiming done.** A feature is "complete" only once it's been confirmed
   against the actual deployed code and — where practical — VM-tested by the person, not
   because a design for it exists. This directive exists because of exactly one documented
   failure: see the note at the top of this file.
8. **Installation testing, online AND offline.** Every feature states its impact on both
   install paths. Offline bundle has tooling (`offline/`) but — as of this snapshot — has
   **never been run end-to-end**. Treat that as true until someone reports otherwise.

---

## 1. What this application is

An internal Delinea Professional Services tool that migrates customer data from
**Privileged Access Service (PAS)** tenants into **Secret Server / Delinea Platform**
tenants. Engagement-based: each customer engagement has source (PAS) and target (SS/
Platform) tenant connections, inventory snapshots, selectable migration runs (accounts,
secrets, folders), verification/reconciliation, revert of tool-created target data, an
audit log, and a local-AI assistant that answers questions about migration state.

Users: PS engineers (operators), customers observing (viewers), admins. Runs on-prem/
in-lab, one VM per engagement context — not multi-tenant SaaS.

## 2. Stack

- **Backend:** .NET minimal API, single project `src/Api/Api.csproj` compiling sibling
  module folders (modular monolith): `Ai`, `Auth`, `Connectors`, `Data`. `Api.csproj`'s
  `<Compile Include>` globs cover each of these explicitly — it does **not** auto-glob
  `src/`, so a new sibling folder needs its own glob line, but new files inside an
  already-covered folder (e.g. another file in `src/Data/`) need nothing.
- **DB:** PostgreSQL + Dapper. Migrations are plain SQL in `db/migrations/00N_*.sql`,
  auto-applied on a **fresh** install via `docker-entrypoint-initdb.d` (Postgres only runs
  these once, on an empty volume) — applied **manually** on an existing install via
  `docker compose exec -T db psql -U pasmig -d pasmig < db/migrations/00N_*.sql`.
- **Frontend:** React + Vite + TypeScript in `frontend/`, served by nginx (TLS
  self-signed, reverse-proxies `/api/` and `/health/`). Code-split; lazy-loaded route
  chunks.
- **AI:** Ollama container (CPU). Three models, each independently configurable via env
  var with a coded fallback default: chat (`OLLAMA_CHAT_MODEL`, default `llama3.1:8b`),
  embedding (`OLLAMA_EMBED_MODEL`, default `nomic-embed-text`), safety guard
  (`OLLAMA_GUARD_MODEL`, default `llama-guard3:1b`). `ollama-init` pulls all three
  on first run, then exits.
- **Compose services:** `db`, `ollama`, `ollama-init`, `api`, `frontend`. **Only `frontend`
  publishes host ports** (`80`→redirect, `443` TLS). `api`'s host port was removed
  entirely in this snapshot — the comment in `docker-compose.yml` is explicit about why
  ("publishing 8080 exposed the API on the LAN, bypassing TLS entirely"). This means: the
  direct-to-container diagnostic trick used earlier (`curl http://localhost:8080/...`
  bypassing nginx) **no longer works** — that port mapping is gone. To repeat that kind of
  diagnostic now you'd need to temporarily republish the port or exec into a container on
  the same compose network.
- **`.env` requirements:** `POSTGRES_PASSWORD`, `AUTH_JWT_SECRET` (mandatory —
  `${AUTH_JWT_SECRET:?...}`, compose refuses to start without it). `ASPNETCORE_ENVIRONMENT`
  is currently `Development` in `.env` — **open item**, dev exception pages leak stack
  traces to clients.

## 3. Working relationship and delivery protocol

Andrew (PS engineer) runs the tool on a Rocky Linux VM (`rockyllmdev`). No dotnet SDK in
Claude's sandbox — C# compiles only inside the Docker build on the VM, plus GitHub Actions
CI on push. Structural correctness gets checked with a real character-level lexer (handles
verbatim/interpolated/triple-quoted strings correctly — the older regex-based brace
counter has a known false-positive class around certain string literals and should not be
trusted alone); actual compilation is still only proven by the VM build.

Frontend changes get verified for real: `npm ci`, `tsc --noEmit`, full `vite build`, all
runnable in the sandbox.

**Every response that changes repository files ends with:** a tarball (changed/new files
only, repo-root-relative paths) + a deploy block (extract, DB migration if any runs
*before* the rebuild, git commit + push, rebuild whichever of `api`/`frontend` changed) +
concrete verification steps.

**Probe before patch.** Diagnose the running system (container logs, direct curl,
`docker compose ps`) before writing a fix — don't guess at a cause. This applies to
documentation about the system too, not just bugs in it: this file's rebuild is itself an
instance of that rule.

## 4. Code structure (verified against the 2026-07-13 snapshot)

```
src/
  Api/
    Program.cs           — composition root + ALL route handlers. Large by design (thin
                            orchestration, not business logic) — DI registrations at top,
                            routes grouped by area, request DTO records at the bottom.
    Api.csproj            — explicit <Compile Include> per sibling folder (Ai/, Auth/,
                            Connectors/, Data/) — see §2.
  Ai/
    ILlmProvider.cs       — chat/embed abstraction.
    OllamaProvider.cs     — the concrete implementation. Config keys read as
                            "Ai:Ollama:*" (colon) — NOT "Ai__Ollama__*" (double
                            underscore); IConfiguration normalizes env-var names to
                            colon-form on load, so double-underscore lookups silently
                            return null and fall through to hardcoded defaults. This bit
                            the chat model specifically (default fallback "qwen2.5:3b"
                            was never pulled) until fixed.
    AssistantPrompt.cs, AssistantTools.cs, AssistantService.cs
                          — the assistant itself: a keyword router picks a "tool"; some
                            tools render structured data directly (fast path, no LLM at
                            all); others narrate via the chat model over SSE (slow path).
    ContentGuard.cs       — Llama Guard wrapper (reuses the "ollama" HttpClient, points at
                            the small guard model). Input-side: blocks before the slow
                            path's chat call runs. Output-side: the full answer streams
                            live as normal, then gets classified in the background and
                            logged to event_log (event_type "ai_safety") if flagged —
                            audit-after-the-fact by design, not a pre-display gate (that
                            would require buffering the whole answer before showing any of
                            it).
  Auth/
    AuthService.cs        — local auth: BCrypt(12), JWT issuance (now via
                            GenerateTokenAsync, reading the configurable session-timeout
                            setting — see §7), password change, user CRUD helpers.
    EngagementAuthorization.cs — engagement-scoped authorization handler/requirement.
    IdpSecretProtector.cs  — AES-256-GCM for OIDC client secrets, same scheme as tenant
                            credentials, HKDF-salted with the provider's own id.
    ExternalIdentityService.cs — SSO identity resolution: (provider, subject) is the
                            identity key, never email (except once, for first-time linking
                            of a pre-created active local user). Domain allow-list,
                            deactivation-wins, typed rejection reasons.
    SsoLogin.cs            — SsoSchemeRegistry (slug → scheme, 404s unknown slugs) +
                            SsoLogin.CompleteAsync, the OnTicketReceived handler that runs
                            after a validated OIDC callback.
  Connectors/
    PasConnector.cs, SecretServerConnector.cs — tenant API clients (IPasClient /
                            ISecretServerClient interfaces).
    ConnectionService.cs, InventoryService.cs, MigrationService.cs — orchestration.
                            MigrationService.CheckoutThenUnmanageAsync (public static) is
                            the account-migration safety net: checks out the password
                            BEFORE unmanaging, not after — unmanaging first could strand an
                            account (unmanaged, password never captured) if the checkout
                            was then refused.
    CredentialVault.cs, CredentialEncryptionService.cs, CredentialResolver.cs —
                            in-memory session cache, at-rest encryption, and the single
                            merge path (stored-over-sent) used by every endpoint that needs
                            tenant credentials.
    JobRegistry.cs         — tracks running migration jobs for cancellation.
  Data/
    I*Repository.cs        — one interface + sealed Dapper implementation per file (mostly).
                            Covers: Engagement, User, Credential, Inventory, Migration,
                            Settings, IdentityProvider, UserIdentity. Property-based records
                            required for any row with an array column (Npgsql reports
                            array-typed fields as System.Array, which breaks
                            positional-record constructor matching).

frontend/src/
  components/  — one file per page: Dashboard, Engagements, Connections (pre-migration
                 creds), Readiness, Migration, Logs, Assistant, Users, Settings,
                 ChangePassword, Login, Setup.
  lib/api.ts   — every fetch call goes through credFetch() (cookies included) + json<T>()
                 (extracts a `.message` field from error bodies — NOT `.error`; a couple of
                 admin pages bypass this wrapper with raw fetch() specifically because of
                 that mismatch, which is worth resolving consistently at some point rather
                 than propagating the workaround).

db/migrations/  001 core · 002 migration · 003 ai · 004 auth · 005 credential_unique
                (fixed mid-session: Postgres has no "ADD CONSTRAINT IF NOT EXISTS" —
                that's only valid for columns/indexes/DROP CONSTRAINT, never ADD
                CONSTRAINT) · 006 settings (key-value app_setting table) · 007 sso
                (identity_provider, user_identity, scim_token tables; drops the
                password_hash NOT NULL constraint for future password-less SSO users).

test/UnitTests/  Fakes at the seams, no DB, no network. Current suites: AuthService,
                 EngagementAuthorization, CredentialResolver, IdentityProvider,
                 ExternalIdentityService, PasConnectorUnmanage, MigrationCheckoutOrder.
```

## 5. Security posture (current, verified)

- Fallback authorization policy — endpoints are authenticated unless explicitly
  `.AllowAnonymous()`.
- Login rate limiting (`RequireRateLimiting("login")`) — confirmed present.
- Cookie `Secure = true` in both places the auth cookie is set (login, password change) —
  confirmed fixed; this was flagged as an open item earlier in this same session and has
  since been addressed.
- `AUTH_JWT_SECRET` mandatory, no hardcoded fallback.
- Session timeout is admin-configurable (Settings page), bounded 1–168 hours server-side,
  applies to new logins only — existing sessions keep whatever length they were issued.
- Change-password rejects null/empty password hashes rather than skipping verification
  (the fix that had to land before SSO could safely introduce password-less accounts) —
  confirmed present, with a comment documenting the history.
- `api` container: no host port, read-only filesystem, reached only through nginx.

## 6. AI Assistant — verified current state

- Fully wired: DI registrations, the `/api/engagements/{id}/assistant` SSE route, and the
  `"ollama"` named HttpClient all present and consistent (this was not always true earlier
  in the session — the whole subsystem existed as classes with zero DI registration or
  routing for a while; that gap is closed now).
- Fast path (keyword-matched, no LLM): prerequisites, migration stats, reconciliation
  status, risk scan, environment summary — render straight from the database.
- Slow path (LLM narration via the chat model): explain_failures, recent_activity, and
  genuinely open-ended questions. This is the only path ContentGuard touches.
- `/api/diag/ollama` — admin-only health check (endpoint, chat model, embed model, embed
  test result). Was silently broken by the same config-key bug as the main provider; fixed
  alongside it.
- Live elapsed timer + indeterminate progress indicator + streamed token count in the UI —
  no fabricated percentage, since token-by-token streaming has no known total.

## 7. Features completed (VM-confirmed this session unless noted otherwise)

- **Credential persistence bug** — `005_credential_unique.sql`'s invalid syntax meant the
  unique constraint backing every credential upsert never actually existed; every
  Test Connection call failed after a successful tenant auth. Fixed + applied to the live
  DB.
- **Inventory rerun** — `/inventory/run` now merges stored/vault credentials the same way
  `/migrate` does; the "Run inventory" button's enablement now checks actual stored
  credential state instead of a local flag that reset on every page load.
- **Load Templates / Create file template** — were unconditionally disabled (gated on a
  frontend field that structurally could never be populated); now resolve the real secret
  from the vault server-side via engagementId+role, matching the `/migrate` pattern.
- **Account migration checkout order** — was unmanaging before checking out the password;
  reordered to checkout-then-unmanage, with checkin-on-failure, closing a real
  stranded-account risk. Tier 1 regression test added.
- **Search/filter** — migration item checklist (client-side, migrated-last sort
  reinforced) and Logs page (server-side: free text + event type + failures-only, with a
  new "AI safety" filterable type).
- **Assistant wiring** (see §6) + the elapsed-timer/progress UI.
- **Content guard** (see §6): input blocked before generation, output flagged to
  `event_log` after streaming completes.
- **Settings page** (new): admin-only, currently holds session timeout; also — confirmed
  in this snapshot but built in a separate line of work — an "Identity providers (SSO)"
  admin CRUD panel lives on the same page.
- **SSO — steps 0–2, confirmed actually wired, not just designed:**
  - Step 0 (prereqs): empty-hash bypass fixed, rate limiting, `Secure=true` cookies — all
    verified present in the code, not just claimed.
  - Step 1 (schema + DAL + admin CRUD): migration 007, `IIdentityProviderRepository` +
    `IUserIdentityRepository`, full `/api/admin/identity-providers` CRUD, admin UI panel.
  - Step 2 (OIDC flow): per-provider named authentication schemes registered from the DB,
    `OnTicketReceived` → `SsoLogin.CompleteAsync` → `ExternalIdentityService.ResolveAsync`
    → issues the same local JWT cookie as password login. Typed rejection reasons
    (unknown user, deactivated, domain not allowed) redirect with an
    `?sso_error=<code>` query param.
  - `ExternalIdentityService` resolution rules confirmed by direct code read: domain
    allow-list on every sign-in, `(provider, sub)` link takes priority, first-time email
    link only for a pre-created active user with a verified/absent-but-trusted email
    claim, deactivation wins everywhere. JIT provisioning is explicitly NOT honored yet,
    even when the flag is set in the admin UI — that's a deliberate no-op pending phase 4.
  - 7 + 10 unit tests across `ExternalIdentityServiceTests.cs` / `IdentityProviderTests.cs`.

## 8. Known documentation gap worth closing

- `IdentityProviders` admin panel's own code comment says providers are "inert until the
  OIDC sign-in flow ships (SSO step 2)" — but step 2 has shipped (see §7). The comment is
  stale, not the code. Practically, providers configured today are still not *usable* by an
  end user yet, but for a different reason than the comment states: there's no
  `/api/auth/providers` discovery endpoint and no login-page button — an admin can create a
  provider and it'll answer at `/api/auth/sso/{slug}/login` if you know the exact URL, but
  nothing surfaces that URL anywhere in the UI. Worth fixing the comment when phase 3 lands
  so it stops describing the wrong blocker.
- `007_sso.sql`'s own comment references `docs/SSO_SCIM_DESIGN.md` as an existing document.
  No `docs/` folder exists in this repo. The design draft was generated in a past session
  but explicitly never committed — the comment should either get that doc committed
  alongside it, or stop citing a file that isn't there.

## 9. Open / future work (explicitly NOT done — verified absent, not merely undocumented)

- **SSO phase 3** — `/api/auth/providers` + login-page "Sign in with X" buttons. Nothing
  exists for this yet; confirmed via direct route search.
- **SSO phase 4** — JIT provisioning + role-mapping strategy. The DB columns
  (`jit_provisioning`, `default_role`, `role_claim`, `role_mappings`) exist and are
  settable via the admin UI, but `ExternalIdentityService` explicitly ignores them right
  now (see the comment in that file). Configuring them today has no effect.
- **SSO phase 5 — SCIM.** This is the one the person most recently asked about directly.
  Schema groundwork exists (`scim_token` table, hashed bearer tokens), but **zero SCIM
  routes exist** — confirmed via direct search of `Program.cs`. No `/scim/v2/...` endpoints
  at all. This is genuinely phase 5 of a 6-phase plan and nothing before it but OIDC
  phases 0–2 has shipped.
- **SSO phase 6** — SCIM Groups, dynamic scheme reload without restart, SAML (deliberately
  deprioritized: "every modern IdP speaks OIDC" was the original reasoning, and it holds —
  SAML would need a third-party library with real version-compatibility risk that OIDC,
  being built into ASP.NET Core directly, avoids entirely).
- **Offline bundle** — tooling exists (`offline/make-offline-bundle.sh`,
  `install-offline.sh`), genuinely never run end-to-end. Needs to also bundle the guard
  model now (`llama-guard3:1b`) alongside chat + embed.
- **`ASPNETCORE_ENVIRONMENT` → Production** — still `Development` in `.env`; dev exception
  pages currently leak stack traces to clients.
- **`api.ts`'s `json()` helper only reads `.message` from error bodies, not `.error`** —
  a couple of admin pages route around this with raw `fetch()` instead of the shared
  wrapper. Worth picking one convention.
- **Restart rule not surfaced in the UI** — changing a provider's authority/clientId/secret
  or enabling/disabling it requires `docker compose restart api` (schemes are registered at
  startup from the DB); the admin panel doesn't say so anywhere yet.
- **Event log retention** — `event_log` (now also holding `ai_safety` entries) has no
  pruning. Hangfire is registered in DI but nothing is actually scheduled with it anywhere
  in the codebase — it would be the natural mechanism for a retention job, sitting there
  unused.
- **Failed-login lockout** — still just a log line, no actual lockout/backoff after
  repeated failures.
- **Settings additions worth considering** (not yet built, just recommended): password
  policy (currently hardcoded), content-guard on/off toggle.
