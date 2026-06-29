import { useState, useEffect } from "react";
import { api, Engagement } from "../lib/api";

interface AppUser {
  id: string;
  username: string;
  email: string;
  displayName: string;
  role: string;
  forcePasswordChange: boolean;
  isActive: boolean;
  engagementIds: string[];
}

export function Users() {
  const [users, setUsers]             = useState<AppUser[]>([]);
  const [engagements, setEngagements] = useState<Engagement[]>([]);
  const [msg, setMsg]                 = useState<string | null>(null);
  const [error, setError]             = useState<string | null>(null);
  const [showCreate, setShowCreate]   = useState(false);
  const [form, setForm] = useState({
    username: "", email: "", displayName: "",
    role: "viewer", engagementIds: [] as string[],
  });

  useEffect(() => {
    loadUsers();
    api.listEngagements().then(setEngagements).catch(() => {});
  }, []);

  async function loadUsers() {
    try {
      const res = await fetch("/api/admin/users", { credentials: "include" });
      if (res.ok) setUsers(await res.json());
    } catch { /* ignore */ }
  }

  async function createUser() {
    setMsg(null); setError(null);
    try {
      const res = await fetch("/api/admin/users", {
        method: "POST", headers: { "Content-Type": "application/json" },
        credentials: "include",
        body: JSON.stringify({ ...form, engagementIds: form.engagementIds.length ? form.engagementIds : null }),
      });
      const data = await res.json();
      if (res.ok) {
        setMsg(data.message); setShowCreate(false);
        setForm({ username: "", email: "", displayName: "", role: "viewer", engagementIds: [] });
        loadUsers();
      } else setError(data.error);
    } catch { setError("Network error."); }
  }

  async function deactivate(id: string) {
    if (!confirm("Deactivate this user? They will no longer be able to log in.")) return;
    const res = await fetch(`/api/admin/users/${id}/deactivate`, { method: "POST", credentials: "include" });
    if (res.ok) { setMsg("User deactivated."); loadUsers(); }
  }

  async function resetPwd(id: string) {
    const res = await fetch(`/api/admin/users/${id}/reset-password`, { method: "POST", credentials: "include" });
    const data = await res.json();
    if (res.ok) setMsg(data.message);
  }

  const ROLE_COLORS: Record<string, string> = {
    admin:    "#e74c3c",
    operator: "#f39c12",
    viewer:   "var(--accent)",
  };

  return (
    <div className="page-content">
      {/* Header */}
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", marginBottom: "1.5rem" }}>
        <div>
          <h2 style={{ margin: "0 0 .25rem" }}>User Management</h2>
          <p className="muted" style={{ margin: 0, fontSize: ".85rem" }}>
            Manage PS engineer and customer accounts
          </p>
        </div>
        <button className="btn-primary" onClick={() => setShowCreate(true)}>+ New user</button>
      </div>

      {/* Feedback banners */}
      {msg && (
        <div style={{ marginBottom: "1rem", padding: ".65rem 1rem", background: "rgba(35,229,126,.1)",
          borderRadius: "var(--radius)", border: "1px solid var(--accent)", fontSize: ".88rem", color: "var(--accent)" }}>
          {msg}
        </div>
      )}
      {error && (
        <div style={{ marginBottom: "1rem", padding: ".65rem 1rem", background: "rgba(231,76,60,.1)",
          borderRadius: "var(--radius)", border: "1px solid #e74c3c", fontSize: ".88rem", color: "#e74c3c" }}>
          {error}
        </div>
      )}

      {/* User table */}
      <div style={{ background: "var(--surface-1)", border: "1px solid var(--border)", borderRadius: "var(--radius)", overflow: "hidden" }}>
        <table className="users-table">
          <thead>
            <tr>
              <th>User</th>
              <th>Role</th>
              <th>Status</th>
              <th>Access</th>
              <th style={{ textAlign: "right" }}>Actions</th>
            </tr>
          </thead>
          <tbody>
            {users.length === 0 && (
              <tr><td colSpan={5} style={{ textAlign: "center", color: "var(--text-muted)", padding: "2rem" }}>No users yet.</td></tr>
            )}
            {users.map(u => (
              <tr key={u.id} style={{ opacity: u.isActive ? 1 : .5 }}>
                <td>
                  <div style={{ display: "flex", alignItems: "center", gap: ".6rem" }}>
                    <div style={{
                      width: 32, height: 32, borderRadius: "50%",
                      background: u.isActive ? "var(--accent)" : "var(--border)",
                      color: "#0a0a0a", fontWeight: 700, fontSize: ".85rem",
                      display: "flex", alignItems: "center", justifyContent: "center", flexShrink: 0,
                    }}>
                      {(u.displayName || u.username).charAt(0).toUpperCase()}
                    </div>
                    <div>
                      <div className="user-name">
                        {u.displayName || u.username}
                        {u.forcePasswordChange && (
                          <span className="user-pwd-reset">pwd reset required</span>
                        )}
                      </div>
                      <div style={{ fontSize: ".75rem", color: "var(--text-muted)" }}>{u.email}</div>
                    </div>
                  </div>
                </td>
                <td>
                  <span className="role-badge" style={{
                    background: (ROLE_COLORS[u.role] ?? "var(--border)") + "22",
                    color: ROLE_COLORS[u.role] ?? "var(--text)",
                    border: `1px solid ${ROLE_COLORS[u.role] ?? "var(--border)"}`,
                  }}>
                    {u.role}
                  </span>
                </td>
                <td>
                  <span style={{ fontSize: ".82rem" }}>
                    <span style={{ color: u.isActive ? "var(--accent)" : "var(--text-muted)" }}>
                      {u.isActive ? "● Active" : "○ Inactive"}
                    </span>
                  </span>
                </td>
                <td style={{ fontSize: ".82rem", color: "var(--text-muted)" }}>
                  {u.engagementIds.length === 0
                    ? "All engagements"
                    : u.engagementIds
                        .map(id => engagements.find(e => e.id === id)?.name ?? id.slice(0, 8) + "…")
                        .join(", ")}
                </td>
                <td style={{ textAlign: "right" }}>
                  <div style={{ display: "flex", gap: ".4rem", justifyContent: "flex-end" }}>
                    <button className="btn-ghost"
                      style={{ fontSize: ".78rem", padding: ".25rem .6rem" }}
                      onClick={() => resetPwd(u.id)}>
                      Reset pwd
                    </button>
                    {u.isActive && (
                      <button className="btn-ghost"
                        style={{ fontSize: ".78rem", padding: ".25rem .6rem", color: "#e74c3c", borderColor: "#e74c3c" }}
                        onClick={() => deactivate(u.id)}>
                        Deactivate
                      </button>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Role legend */}
      <div style={{ display: "flex", gap: "1.5rem", marginTop: "1rem", fontSize: ".78rem", color: "var(--text-muted)" }}>
        <span><strong style={{color:"#e74c3c"}}>Admin</strong> — full access + user management</span>
        <span><strong style={{color:"#f39c12"}}>Operator</strong> — PS engineer, no user management</span>
        <span><strong style={{color:"var(--accent)"}}>Viewer</strong> — customer, read-only, engagement-scoped</span>
      </div>

      {/* Create user modal */}
      {showCreate && (
        <div className="modal-overlay">
          <div className="modal-card" style={{ maxWidth: 460 }}>
            <div className="modal-header">Create new user</div>
            <div style={{ display: "flex", flexDirection: "column", gap: ".75rem" }}>
              <div className="form-group">
                <label>Username *</label>
                <input value={form.username} onChange={e => setForm(f => ({ ...f, username: e.target.value }))} placeholder="jsmith" />
              </div>
              <div className="form-group">
                <label>Email *</label>
                <input type="email" value={form.email} onChange={e => setForm(f => ({ ...f, email: e.target.value }))} placeholder="jsmith@company.com" />
              </div>
              <div className="form-group">
                <label>Display name</label>
                <input value={form.displayName} onChange={e => setForm(f => ({ ...f, displayName: e.target.value }))} placeholder="John Smith" />
              </div>
              <div className="form-group">
                <label>Role</label>
                <select style={{ background: "var(--surface-2)", border: "1px solid var(--border)", color: "var(--text)", padding: ".5rem .75rem", borderRadius: "var(--radius)", fontSize: ".9rem" }}
                  value={form.role} onChange={e => setForm(f => ({ ...f, role: e.target.value }))}>
                  <option value="admin">Admin — PS engineer, full access</option>
                  <option value="operator">Operator — PS engineer, no user management</option>
                  <option value="viewer">Viewer — Customer, read-only</option>
                </select>
              </div>
              {form.role === "viewer" && engagements.length > 0 && (
                <div className="form-group">
                  <label>Scope to engagements (hold Ctrl/Cmd for multiple)</label>
                  <select multiple style={{ background: "var(--surface-2)", border: "1px solid var(--border)", color: "var(--text)", padding: ".4rem", borderRadius: "var(--radius)", height: 100 }}
                    value={form.engagementIds}
                    onChange={e => setForm(f => ({ ...f, engagementIds: Array.from(e.target.selectedOptions, o => o.value) }))}>
                    {engagements.map(e => <option key={e.id} value={e.id}>{e.name} · {e.customer_name}</option>)}
                  </select>
                  <span style={{ fontSize: ".73rem", color: "var(--text-muted)" }}>Leave empty to allow access to all engagements</span>
                </div>
              )}
              <p style={{ fontSize: ".78rem", color: "var(--text-muted)", margin: 0 }}>
                A temporary password will be generated and shown after creation.
                The user must change it on first login.
              </p>
              <div style={{ display: "flex", gap: ".5rem", justifyContent: "flex-end", marginTop: ".25rem" }}>
                <button className="btn-ghost" onClick={() => setShowCreate(false)}>Cancel</button>
                <button className="btn-primary" onClick={createUser}
                  disabled={!form.username.trim() || !form.email.trim()}>
                  Create user
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
