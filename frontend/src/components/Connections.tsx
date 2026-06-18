import { useEffect, useState } from "react";
import { api, type Engagement, type TestConnectionResult } from "../lib/api";

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
          Credentials are used only to test and run the migration. They’re held in memory for
          the request and never written to the database or logs.
        </p>
      </div>

      {engagementId && (
        <div className="conn-grid">
          <ConnectionCard role="source" engagementId={engagementId} />
          <ConnectionCard role="target" engagementId={engagementId} />
        </div>
      )}
    </div>
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
