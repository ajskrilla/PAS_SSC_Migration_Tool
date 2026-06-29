import { useEffect, useState } from "react";
import { api, type Engagement, type TestConnectionResult, type SnapshotSummary } from "../lib/api";
import { downloadInventoryCsv, printInventorySummary } from "../lib/inventoryExport";

type Role = "source" | "target";

interface FormState {
  // metadata (persisted)
  baseUrl: string;
  platformTenant: string;
  authMode: "platform_client_credentials" | "legacy_password";
  appId: string;
  // platform/SS split URLs (optional overrides)
  platformBaseUrl: string;
  secretServerBaseUrl: string;
  // credentials (session only, never stored)
  clientId: string;
  clientSecret: string;
  username: string;
  scope: string;
}

const blankForm = (authMode: FormState["authMode"]): FormState => ({
  baseUrl: "",
  platformTenant: "",
  authMode,
  appId: "",
  platformBaseUrl: "",
  secretServerBaseUrl: "",
  clientId: "",
  clientSecret: "",
  username: "",
  scope: "",
});

export function Connections() {
  const [engagements, setEngagements] = useState<Engagement[]>([]);
  const [engagementId, setEngagementId] = useState<string>("");
  const [loadError, setLoadError] = useState<string | null>(null);
  const [credInfo, setCredInfo] = useState<Awaited<ReturnType<typeof api.credentialInfo>> | null>(null);

  useEffect(() => {
    if (engagementId)
      api.credentialInfo(engagementId).then(setCredInfo).catch(() => {});
  }, [engagementId]);

  useEffect(() => {
    api.listEngagements()
      .then((rows) => {
        setEngagements(rows);
        if (rows.length && !engagementId) setEngagementId(rows[0].id);
      })
      .catch((e) => setLoadError(String(e)));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div>
      <header className="page-head"><h1>Tenant connections</h1></header>

      {loadError && <div className="panel bad-note">Couldn’t load engagements: {loadError}</div>}

      <div className="panel" style={{ marginBottom: 16 }}>
        <label className="field">
          <span>Engagement</span>
          {engagements.length ? (
            <select value={engagementId} onChange={(e) => setEngagementId(e.target.value)}>
              {engagements.map((e) => (
                <option key={e.id} value={e.id}>{e.name} — {e.customer_name}</option>
              ))}
            </select>
          ) : (
            <span className="muted">No engagements yet. Create one on the Engagements tab first.</span>
          )}
        </label>
        <p className="muted note">
          Test your connections below. Credentials are encrypted and persisted —
          no need to re-enter after a restart.
        </p>
      </div>

      {engagementId && credInfo && (credInfo.source || credInfo.target) && (
        <div className="cred-status-banner">
          <div className="cred-status-header">
            <span className="ok-text" style={{fontWeight:600}}>🔒 Credentials stored</span>
            <button className="btn-ghost" style={{fontSize:".75rem",padding:".2rem .5rem"}}
              onClick={() => api.clearCredentials(engagementId).then(() => setCredInfo(null))}>
              Clear
            </button>
          </div>
          <div className="cred-status-cards">
            {credInfo.source && (
              <div className="cred-status-card">
                <div className="cred-status-role">SOURCE · PAS</div>
                <div className="cred-field"><span>URL</span><code>{credInfo.source.baseUrl ?? credInfo.source.platformBaseUrl ?? "—"}</code></div>
                <div className="cred-field"><span>Client ID</span><code>{credInfo.source.clientId}</code></div>
                {credInfo.source.appId && <div className="cred-field"><span>App ID</span><code>{credInfo.source.appId}</code></div>}
                <div className="cred-field"><span>Secret</span><span className="cred-masked">••••••••</span></div>
              </div>
            )}
            {credInfo.target && (
              <div className="cred-status-card">
                <div className="cred-status-role">TARGET · SECRET SERVER</div>
                <div className="cred-field"><span>SS URL</span><code>{credInfo.target.secretServerBaseUrl ?? credInfo.target.platformBaseUrl ?? "—"}</code></div>
                <div className="cred-field"><span>Client ID</span><code>{credInfo.target.clientId}</code></div>
                <div className="cred-field"><span>Secret</span><span className="cred-masked">••••••••</span></div>
              </div>
            )}
          </div>
        </div>
      )}

      {engagementId && (
        <div className="conn-grid">
          <ConnectionCard role="source" engagementId={engagementId} />
          <ConnectionCard role="target" engagementId={engagementId} />
        </div>
      )}

      {engagementId && (
        <InventoryExport
          engagementId={engagementId}
          engagement={engagements.find((e) => e.id === engagementId)}
        />
      )}
    </div>
  );
}

function InventoryExport({
  engagementId, engagement,
}: {
  engagementId: string; engagement?: Engagement;
}) {
  const [summary, setSummary] = useState<SnapshotSummary | null>(null);
  const [busy, setBusy] = useState<"csv" | "pdf" | null>(null);
  const [err, setErr] = useState<string | null>(null);

  // Load the source snapshot summary so we know whether an inventory exists + show counts.
  useEffect(() => {
    setSummary(null); setErr(null);
    api.inventorySummary(engagementId)
      .then((rows) => setSummary(rows.find((r) => r.role === "source") ?? null))
      .catch((e) => setErr(String(e)));
  }, [engagementId]);

  const baseName = () => {
    const cust = (engagement?.customer_name || "customer").replace(/[^\w.-]+/g, "_");
    const stamp = new Date().toISOString().slice(0, 10);
    return `${cust}_vault_inventory_${stamp}`;
  };

  const exportCsv = async () => {
    if (!summary) return;
    setBusy("csv"); setErr(null);
    try {
      const items = await api.snapshotItems(summary.snapshot_id);
      downloadInventoryCsv(items, baseName());
    } catch (e) {
      setErr(`CSV export failed: ${String(e)}`);
    } finally {
      setBusy(null);
    }
  };

  const exportPdf = () => {
    if (!summary) return;
    setBusy("pdf"); setErr(null);
    try {
      printInventorySummary(summary, {
        engagementName: engagement?.name || "Engagement",
        customerName: engagement?.customer_name || "Customer",
      });
    } catch (e) {
      setErr(`Summary export failed: ${String(e)}`);
    } finally {
      setBusy(null);
    }
  };

  return (
    <section className="panel" style={{ marginTop: 16 }}>
      <div className="phase-no">INVENTORY EXPORT</div>
      <h2 style={{ marginTop: 0 }}>Export the captured inventory</h2>
      {!summary ? (
        <p className="muted">
          {err
            ? err
            : "No source inventory captured yet. Run inventory on the PAS connection above, then export here."}
        </p>
      ) : (
        <>
          <p className="muted">
            Source snapshot captured {new Date(summary.captured_at).toLocaleString()} —{" "}
            {summary.summary.total} items ({summary.summary.accounts} accounts,{" "}
            {summary.summary.managed} managed). The CSV lists every secret; the summary report is a
            one-page overview for the customer.
          </p>
          <div className="conn-actions">
            <button className="btn" onClick={exportCsv} disabled={busy !== null}>
              {busy === "csv" ? "Exporting…" : "Export secret list (CSV)"}
            </button>
            <button className="btn ghost" onClick={exportPdf} disabled={busy !== null}>
              {busy === "pdf" ? "Opening…" : "Export summary report (PDF)"}
            </button>
          </div>
          {err && <div className="result bad" style={{ marginTop: 10 }}>{err}</div>}
        </>
      )}
    </section>
  );
}

function ConnectionCard({ role, engagementId }: { role: Role; engagementId: string }) {
  // Source is PAS; target is Secret Server / Platform.
  const systemType: "pas" | "secret_server" = role === "source" ? "pas" : "secret_server";
  const [form, setForm] = useState<FormState>(blankForm("platform_client_credentials"));
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<TestConnectionResult | null>(null);
  const [invMsg, setInvMsg] = useState<string | null>(null);
  const [tested, setTested] = useState(false);

  const [saved, setSaved] = useState(false);

  const set = (k: keyof FormState, v: string) => {
    setForm((f) => ({ ...f, [k]: v }));
    setResult(null);
    setSaved(false);
    setTested(false);
    setInvMsg(null);
  };

  const saveMetadata = async () => {
    setSaving(true);
    setResult(null);
    try {
      await api.saveConnection(engagementId, {
        role,
        systemType,
        baseUrl: form.baseUrl || undefined,
        platformTenant: form.platformTenant || undefined,
        authMode: form.authMode,
      });
      setSaved(true);
    } catch (e) {
      setResult({ success: false, message: `Save failed: ${String(e)}` });
    } finally {
      setSaving(false);
    }
  };

  const test = async () => {
    setTesting(true);
    setResult(null);
    try {
      const r = await api.testConnection({
        systemType,
        authMode: form.authMode,
        baseUrl: form.baseUrl || undefined,
        platformBaseUrl: form.platformBaseUrl || undefined,
        secretServerBaseUrl: form.secretServerBaseUrl || undefined,
        appId: form.appId || undefined,
        clientId: form.clientId,
        clientSecret: form.clientSecret,
        username: form.username || undefined,
        scope: form.scope || undefined,
        engagementId,
        role,
      });
      setResult(r);
      setTested(r.success);
    } catch (e) {
      setResult({ success: false, message: `Request failed: ${String(e)}` });
    } finally {
      setTesting(false);
    }
  };

  const credPayload = () => ({
    systemType,
    authMode: form.authMode,
    baseUrl: form.baseUrl || undefined,
    platformBaseUrl: form.platformBaseUrl || undefined,
    secretServerBaseUrl: form.secretServerBaseUrl || undefined,
    appId: form.appId || undefined,
    clientId: form.clientId,
    clientSecret: form.clientSecret,
    username: form.username || undefined,
    scope: form.scope || undefined,
  });

  const runInventory = async () => {
    setRunning(true);
    setInvMsg(null);
    try {
      const r = await api.runInventory(engagementId, { ...credPayload(), role });
      setInvMsg(
        `Captured ${r.total} items — ${r.accounts} accounts, ${r.textSecrets} text, ` +
        `${r.fileSecrets} file, ${r.folders} folders.`,
      );
    } catch (e) {
      setInvMsg(`Inventory failed: ${String(e)}`);
    } finally {
      setRunning(false);
    }
  };

  const isLegacy = form.authMode === "legacy_password";

  return (
    <section className="panel conn-card">
      <div className="conn-head">
        <span className="phase-no">{role === "source" ? "SOURCE" : "TARGET"}</span>
        <h2>{role === "source" ? "Delinea / Centrify PAS" : "Secret Server / Platform"}</h2>
      </div>

      <label className="field">
        <span>Auth mode</span>
        <select value={form.authMode} onChange={(e) => set("authMode", e.target.value)}>
          <option value="platform_client_credentials">Client credentials (OAuth2)</option>
          {role === "target" && <option value="legacy_password">Legacy password (standalone SS)</option>}
        </select>
      </label>

      {role === "source" ? (
        <>
          <Text label="Tenant base URL" placeholder="https://acme.my.centrify.net"
            value={form.baseUrl} onChange={(v) => set("baseUrl", v)} />
          <Text label="OAuth2 App ID" placeholder="e.g. migration-app"
            value={form.appId} onChange={(v) => set("appId", v)} />
        </>
      ) : (
        <>
          <Text label="Platform base URL" placeholder="https://acme.delinea.app"
            value={form.platformBaseUrl} onChange={(v) => set("platformBaseUrl", v)} />
          <Text label="Secret Server base URL" placeholder="https://acme.secretservercloud.com"
            value={form.secretServerBaseUrl} onChange={(v) => set("secretServerBaseUrl", v)} />
        </>
      )}

      <div className="cred-block">
        <div className="cred-label">Credentials · session only</div>
        {isLegacy ? (
          <>
            <Text label="Username" value={form.username} onChange={(v) => set("username", v)} />
            <Text label="Password" type="password" value={form.clientSecret}
              onChange={(v) => set("clientSecret", v)} />
          </>
        ) : (
          <>
            <Text label="Client ID" value={form.clientId} onChange={(v) => set("clientId", v)} />
            <Text label="Client secret" type="password" value={form.clientSecret}
              onChange={(v) => set("clientSecret", v)} />
            {role === "source" && (
              <Text label="Scope (optional)" value={form.scope} onChange={(v) => set("scope", v)} />
            )}
          </>
        )}
      </div>

      <div className="conn-actions">
        <button className="btn ghost" onClick={saveMetadata} disabled={saving}>
          {saving ? "Saving…" : "Save settings"}
        </button>
        <button className="btn" onClick={test}
          disabled={testing || (!isLegacy && !form.clientId) || !form.clientSecret}>
          {testing ? "Testing…" : "Test connection"}
        </button>
        <button className="btn" onClick={runInventory}
          disabled={running || !tested}
          title={tested ? "Capture a full read-only inventory" : "Test the connection first"}>
          {running ? "Running…" : "Run inventory"}
        </button>
      </div>

      {saved && <div className="result ok">Settings saved.</div>}
      {result && (
        <div className={`result ${result.success ? "ok" : "bad"}`}>
          {result.success ? "✓ " : "✗ "}{result.message}
        </div>
      )}
      {invMsg && (
        <div className={`result ${invMsg.startsWith("Inventory failed") ? "bad" : "ok"}`}>
          {invMsg}
        </div>
      )}
    </section>
  );
}

function Text({
  label, value, onChange, type = "text", placeholder,
}: {
  label: string; value: string; onChange: (v: string) => void;
  type?: string; placeholder?: string;
}) {
  return (
    <label className="field">
      <span>{label}</span>
      <input type={type} value={value} placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)} autoComplete="off" spellCheck={false} />
    </label>
  );
}
