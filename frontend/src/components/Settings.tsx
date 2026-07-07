import { useEffect, useState } from "react";
import { api, IdentityProvider, IdentityProviderInput } from "../lib/api";

export function Settings() {
  const [hours, setHours] = useState<number | "">("");
  const [bounds, setBounds] = useState<{ min: number; max: number } | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.getSettings()
      .then((s) => {
        setHours(s.sessionTimeoutHours);
        setBounds({ min: s.minSessionTimeoutHours, max: s.maxSessionTimeoutHours });
      })
      .catch((e) => setError(String(e)))
      .finally(() => setLoading(false));
  }, []);

  async function save() {
    if (hours === "") return;
    setSaving(true); setMsg(null); setError(null);
    try {
      const res = await api.updateSettings(hours);
      setMsg(res.message);
    } catch (e) {
      setError(String(e));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div>
      <header className="page-head"><h1>Settings</h1></header>

      <div className="panel" style={{ maxWidth: 480 }}>
        <h2 style={{ marginTop: 0 }}>Login session length</h2>
        <p className="muted note" style={{ marginTop: 0 }}>
          How long a user stays logged in after entering their password, before they need to sign
          in again. Applies to logins from this point forward — anyone already signed in keeps
          the session length they were issued, so shortening this won't immediately log anyone out.
        </p>

        {loading ? (
          <p className="muted">Loading…</p>
        ) : (
          <>
            <label className="field" style={{ maxWidth: 220 }}>
              <span>Session timeout (hours)</span>
              <input
                type="number"
                value={hours}
                min={bounds?.min ?? 1}
                max={bounds?.max ?? 168}
                onChange={(e) => setHours(e.target.value === "" ? "" : Number(e.target.value))}
              />
            </label>
            {bounds && (
              <p className="muted note">
                Allowed range: {bounds.min}–{bounds.max} hours ({Math.round(bounds.max / 24)} days max).
              </p>
            )}

            <div className="conn-actions" style={{ marginTop: 4 }}>
              <button className="btn" onClick={save} disabled={saving || hours === ""}>
                {saving ? "Saving…" : "Save"}
              </button>
            </div>

            {msg && <div className="result ok">{msg}</div>}
            {error && <div className="result bad">{error}</div>}
          </>
        )}
      </div>

      <IdentityProviders />
    </div>
  );
}

// ── Identity providers (SSO) ─────────────────────────────────────────────────────────────
// Admin CRUD over /api/admin/identity-providers. Providers configured here are INERT until
// the OIDC sign-in flow ships (SSO step 2) — this panel says so, honestly, in its note.
// Client secrets are write-only: the list shows only whether one is stored; the edit form's
// secret field left blank means "keep the stored secret".

const EMPTY_IDP_FORM = {
  name: "", slug: "", authority: "", clientId: "", clientSecret: "",
  enabled: true, jitProvisioning: false, defaultRole: "viewer",
  allowedEmailDomains: "", roleClaim: "", roleMappingsJson: "",
};

function IdentityProviders() {
  const [providers, setProviders] = useState<IdentityProvider[]>([]);
  const [loading, setLoading]     = useState(true);
  const [showForm, setShowForm]   = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm]           = useState({ ...EMPTY_IDP_FORM });
  const [saving, setSaving]       = useState(false);
  const [msg, setMsg]             = useState<string | null>(null);
  const [error, setError]         = useState<string | null>(null);

  useEffect(() => { load(); }, []);

  async function load() {
    try { setProviders(await api.listIdentityProviders()); }
    catch (e) { setError(String(e)); }
    finally { setLoading(false); }
  }

  function toInput(): IdentityProviderInput {
    return {
      name: form.name, slug: form.slug, authority: form.authority, clientId: form.clientId,
      clientSecret: form.clientSecret || undefined,   // blank on edit = keep stored secret
      enabled: form.enabled, jitProvisioning: form.jitProvisioning,
      defaultRole: form.defaultRole,
      allowedEmailDomains: form.allowedEmailDomains
        ? form.allowedEmailDomains.split(",").map((d) => d.trim()).filter(Boolean)
        : [],
      roleClaim: form.roleClaim || undefined,
      roleMappingsJson: form.roleMappingsJson || undefined,
    };
  }

  function startCreate() {
    setEditingId(null); setForm({ ...EMPTY_IDP_FORM });
    setShowForm(true); setMsg(null); setError(null);
  }

  function startEdit(p: IdentityProvider) {
    setEditingId(p.id);
    setForm({
      name: p.name, slug: p.slug, authority: p.authority, clientId: p.clientId,
      clientSecret: "",
      enabled: p.enabled, jitProvisioning: p.jitProvisioning, defaultRole: p.defaultRole,
      allowedEmailDomains: (p.allowedEmailDomains ?? []).join(", "),
      roleClaim: p.roleClaim ?? "", roleMappingsJson: p.roleMappingsJson ?? "",
    });
    setShowForm(true); setMsg(null); setError(null);
  }

  async function save() {
    setSaving(true); setMsg(null); setError(null);
    try {
      if (editingId) { await api.updateIdentityProvider(editingId, toInput()); setMsg("Provider updated."); }
      else           { await api.createIdentityProvider(toInput());            setMsg("Provider created."); }
      setShowForm(false); setEditingId(null); setForm({ ...EMPTY_IDP_FORM });
      await load();
    } catch (e) { setError(String(e)); }
    finally { setSaving(false); }
  }

  async function toggleEnabled(p: IdentityProvider) {
    setMsg(null); setError(null);
    try {
      await api.updateIdentityProvider(p.id, {
        name: p.name, slug: p.slug, authority: p.authority, clientId: p.clientId,
        enabled: !p.enabled, jitProvisioning: p.jitProvisioning, defaultRole: p.defaultRole,
        allowedEmailDomains: p.allowedEmailDomains ?? [],
        roleClaim: p.roleClaim ?? undefined,
        roleMappingsJson: p.roleMappingsJson ?? undefined,
        // clientSecret omitted -> stored secret kept
      });
      await load();
    } catch (e) { setError(String(e)); }
  }

  async function remove(p: IdentityProvider) {
    if (!confirm(`Delete identity provider "${p.name}"? Users will no longer be able to sign in with it once SSO is live.`)) return;
    setMsg(null); setError(null);
    try {
      await api.deleteIdentityProvider(p.id);
      setMsg("Provider deleted.");
      await load();
    } catch (e) { setError(String(e)); }
  }

  const inputStyle = { width: "100%" } as const;

  return (
    <div className="panel" style={{ maxWidth: 860, marginTop: "1.25rem" }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start" }}>
        <div>
          <h2 style={{ marginTop: 0 }}>Identity providers (SSO)</h2>
          <p className="muted note" style={{ marginTop: 0 }}>
            External sign-in via OIDC (Entra ID, Okta, Delinea Platform, …). Providers configured
            here are not used for sign-in yet — the SSO login flow ships in a later update; this
            page prepares the configuration. Client secrets are stored encrypted and never shown again.
          </p>
        </div>
        {!showForm && (
          <button className="btn" onClick={startCreate}>+ Add provider</button>
        )}
      </div>

      {msg && <div className="result ok">{msg}</div>}
      {error && <div className="result bad">{error}</div>}

      {loading ? (
        <p className="muted">Loading…</p>
      ) : providers.length === 0 && !showForm ? (
        <p className="muted">No identity providers configured.</p>
      ) : (
        providers.length > 0 && (
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: ".88rem", marginBottom: ".75rem" }}>
            <thead>
              <tr style={{ textAlign: "left", borderBottom: "1px solid var(--border)" }}>
                <th style={{ padding: ".4rem .5rem" }}>Name</th>
                <th style={{ padding: ".4rem .5rem" }}>Slug</th>
                <th style={{ padding: ".4rem .5rem" }}>Authority</th>
                <th style={{ padding: ".4rem .5rem" }}>Default role</th>
                <th style={{ padding: ".4rem .5rem" }}>Secret</th>
                <th style={{ padding: ".4rem .5rem" }}>Status</th>
                <th style={{ padding: ".4rem .5rem", textAlign: "right" }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {providers.map((p) => (
                <tr key={p.id} style={{ borderBottom: "1px solid var(--border)", opacity: p.enabled ? 1 : 0.55 }}>
                  <td style={{ padding: ".4rem .5rem" }}>{p.name}</td>
                  <td style={{ padding: ".4rem .5rem" }}><code>{p.slug}</code></td>
                  <td style={{ padding: ".4rem .5rem", maxWidth: 220, overflow: "hidden",
                               textOverflow: "ellipsis", whiteSpace: "nowrap" }} title={p.authority}>
                    {p.authority}
                  </td>
                  <td style={{ padding: ".4rem .5rem" }}>{p.defaultRole}</td>
                  <td style={{ padding: ".4rem .5rem" }}>{p.hasClientSecret ? "set" : "—"}</td>
                  <td style={{ padding: ".4rem .5rem" }}>{p.enabled ? "enabled" : "disabled"}</td>
                  <td style={{ padding: ".4rem .5rem", textAlign: "right", whiteSpace: "nowrap" }}>
                    <button className="btn" style={{ marginRight: ".4rem" }} onClick={() => startEdit(p)}>Edit</button>
                    <button className="btn" style={{ marginRight: ".4rem" }} onClick={() => toggleEnabled(p)}>
                      {p.enabled ? "Disable" : "Enable"}
                    </button>
                    <button className="btn" onClick={() => remove(p)}>Delete</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )
      )}

      {showForm && (
        <div style={{ borderTop: "1px solid var(--border)", paddingTop: ".9rem" }}>
          <h3 style={{ marginTop: 0 }}>{editingId ? "Edit provider" : "New provider"}</h3>

          <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: ".6rem .9rem" }}>
            <label className="field">
              <span>Display name</span>
              <input style={inputStyle} value={form.name}
                     onChange={(e) => setForm({ ...form, name: e.target.value })}
                     placeholder="Contoso Entra ID" />
            </label>
            <label className="field">
              <span>Slug (appears in URLs)</span>
              <input style={inputStyle} value={form.slug}
                     onChange={(e) => setForm({ ...form, slug: e.target.value })}
                     placeholder="contoso-entra" />
            </label>
            <label className="field" style={{ gridColumn: "1 / -1" }}>
              <span>Authority (OIDC issuer URL)</span>
              <input style={inputStyle} value={form.authority}
                     onChange={(e) => setForm({ ...form, authority: e.target.value })}
                     placeholder="https://login.microsoftonline.com/<tenant-id>/v2.0" />
            </label>
            <label className="field">
              <span>Client ID</span>
              <input style={inputStyle} value={form.clientId}
                     onChange={(e) => setForm({ ...form, clientId: e.target.value })} />
            </label>
            <label className="field">
              <span>Client secret</span>
              <input style={inputStyle} type="password" value={form.clientSecret}
                     onChange={(e) => setForm({ ...form, clientSecret: e.target.value })}
                     placeholder={editingId ? "leave blank to keep current" : ""} />
            </label>
            <label className="field">
              <span>Default role for new SSO users</span>
              <select value={form.defaultRole}
                      onChange={(e) => setForm({ ...form, defaultRole: e.target.value })}>
                <option value="viewer">viewer</option>
                <option value="operator">operator</option>
                <option value="admin">admin</option>
              </select>
            </label>
            <label className="field">
              <span>Allowed email domains (comma-separated, blank = any)</span>
              <input style={inputStyle} value={form.allowedEmailDomains}
                     onChange={(e) => setForm({ ...form, allowedEmailDomains: e.target.value })}
                     placeholder="contoso.com, delinea.com" />
            </label>
          </div>

          <div style={{ display: "flex", gap: "1.2rem", margin: ".6rem 0" }}>
            <label style={{ display: "flex", alignItems: "center", gap: ".4rem", fontSize: ".88rem" }}>
              <input type="checkbox" checked={form.enabled}
                     onChange={(e) => setForm({ ...form, enabled: e.target.checked })} />
              Enabled
            </label>
            <label style={{ display: "flex", alignItems: "center", gap: ".4rem", fontSize: ".88rem" }}>
              <input type="checkbox" checked={form.jitProvisioning}
                     onChange={(e) => setForm({ ...form, jitProvisioning: e.target.checked })} />
              JIT provisioning (auto-create unknown users on first sign-in)
            </label>
          </div>

          <div className="conn-actions">
            <button className="btn" onClick={save}
                    disabled={saving || !form.name || !form.slug || !form.authority || !form.clientId}>
              {saving ? "Saving…" : editingId ? "Save changes" : "Create provider"}
            </button>
            <button className="btn" onClick={() => { setShowForm(false); setEditingId(null); }}>
              Cancel
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
