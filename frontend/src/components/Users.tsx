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
  const [users, setUsers]           = useState<AppUser[]>([]);
  const [engagements, setEngagements] = useState<Engagement[]>([]);
  const [msg, setMsg]               = useState<string | null>(null);
  const [error, setError]           = useState<string | null>(null);
  const [showCreate, setShowCreate] = useState(false);

  // Create form state
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
        method: "POST",
        headers: { "Content-Type": "application/json" },
        credentials: "include",
        body: JSON.stringify({
          ...form,
          engagementIds: form.engagementIds.length > 0 ? form.engagementIds : null,
        }),
      });
      const data = await res.json();
      if (res.ok) {
        setMsg(data.message);
        setShowCreate(false);
        setForm({ username: "", email: "", displayName: "", role: "viewer", engagementIds: [] });
        loadUsers();
      } else {
        setError(data.error);
      }
    } catch { setError("Network error."); }
  }

  async function deactivate(id: string) {
    if (!confirm("Deactivate this user?")) return;
    const res = await fetch(`/api/admin/users/${id}/deactivate`, {
      method: "POST", credentials: "include"
    });
    if (res.ok) { setMsg("User deactivated."); loadUsers(); }
  }

  async function resetPwd(id: string) {
    const res = await fetch(`/api/admin/users/${id}/reset-password`, {
      method: "POST", credentials: "include"
    });
    const data = await res.json();
    if (res.ok) setMsg(data.message);
  }

  const roleBadge = (role: string) => {
    const colors: Record<string, string> = {
      admin: "#e74c3c", operator: "#f39c12", viewer: "var(--accent)"
    };
    return (
      <span className="role-badge" style={{ background: (colors[role] ?? "var(--surface-3)") + "33",
        color: colors[role] ?? "var(--text)", border: `1px solid ${colors[role] ?? "var(--border)"}` }}>
        {role}
      </span>
    );
  };

  return (
    <div className="page-content">
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: "1.5rem" }}>
        <div>
          <h2>User Management</h2>
          <p className="muted">Manage PS engineer and customer accounts</p>
        </div>
        <button className="btn-primary" onClick={() => setShowCreate(true)}>+ New user</button>
      </div>

      {msg   && <div className="banner ok-text"   style={{ marginBottom: "1rem", padding: ".6rem 1rem", background: "rgba(35,229,126,.1)", borderRadius: "var(--radius)", border: "1px solid var(--accent)" }}>{msg}</div>}
      {error && <div className="banner warn-text" style={{ marginBottom: "1rem", padding: ".6rem 1rem", background: "rgba(231,76,60,.1)", borderRadius: "var(--radius)", border: "1px solid #e74c3c" }}>{error}</div>}

      {/* User table */}
      <div className="card">
        <table className="stats-table" style={{ fontSize: ".88rem" }}>
          <thead>
            <tr><th>Username</th><th>Email</th><th>Role</th><th>Status</th><th>Engagements</th><th>Actions</th></tr>
          </thead>
          <tbody>
            {users.map(u => (
              <tr key={u.id} style={{ opacity: u.isActive ? 1 : .5 }}>
                <td>
                  <strong>{u.username}</strong>
                  {u.forcePasswordChange && <span className="warn-text" style={{ fontSize: ".7rem", marginLeft: ".4rem" }}>pwd reset</span>}
                </td>
                <td className="muted">{u.email}</td>
                <td>{roleBadge(u.role)}</td>
                <td><span className={u.isActive ? "ok-text" : "muted"}>{u.isActive ? "Active" : "Inactive"}</span></td>
                <td className="muted" style={{ fontSize: ".78rem" }}>
                  {u.engagementIds.length === 0 ? "All" :
                    u.engagementIds.map(id => engagements.find(e => e.id === id)?.name ?? id.slice(0, 8)).join(", ")}
                </td>
                <td>
                  <button className="btn-ghost" style={{ marginRight: ".3rem", fontSize: ".78rem", padding: ".2rem .5rem" }}
                    onClick={() => resetPwd(u.id)}>Reset pwd</button>
                  {u.isActive && (
                    <button className="btn-ghost" style={{ fontSize: ".78rem", padding: ".2rem .5rem", color: "#e74c3c", borderColor: "#e74c3c" }}
                      onClick={() => deactivate(u.id)}>Deactivate</button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Create user modal */}
      {showCreate && (
        <div className="modal-overlay">
          <div className="modal-card" style={{ minWidth: "400px" }}>
            <div className="modal-header">Create new user</div>
            <div className="login-form" style={{ gap: ".75rem" }}>
              <div className="form-group">
                <label>Username</label>
                <input value={form.username} onChange={e => setForm(f => ({ ...f, username: e.target.value }))} />
              </div>
              <div className="form-group">
                <label>Email</label>
                <input type="email" value={form.email} onChange={e => setForm(f => ({ ...f, email: e.target.value }))} />
              </div>
              <div className="form-group">
                <label>Display name (optional)</label>
                <input value={form.displayName} onChange={e => setForm(f => ({ ...f, displayName: e.target.value }))} />
              </div>
              <div className="form-group">
                <label>Role</label>
                <select className="assistant-eng-select" style={{ width: "100%" }}
                  value={form.role} onChange={e => setForm(f => ({ ...f, role: e.target.value }))}>
                  <option value="admin">Admin (PS engineer — full access)</option>
                  <option value="operator">Operator (PS engineer — no user management)</option>
                  <option value="viewer">Viewer (Customer — read-only)</option>
                </select>
              </div>
              {form.role === "viewer" && (
                <div className="form-group">
                  <label>Scoped engagements (leave empty for all)</label>
                  <select multiple className="assistant-eng-select" style={{ width: "100%", height: "100px" }}
                    value={form.engagementIds}
                    onChange={e => setForm(f => ({ ...f, engagementIds: Array.from(e.target.selectedOptions, o => o.value) }))}>
                    {engagements.map(e => <option key={e.id} value={e.id}>{e.name} · {e.customer_name}</option>)}
                  </select>
                  <span className="muted" style={{ fontSize: ".75rem" }}>Hold Ctrl/Cmd to select multiple</span>
                </div>
              )}
              <div style={{ display: "flex", gap: ".5rem", justifyContent: "flex-end", marginTop: ".5rem" }}>
                <button className="btn-ghost" onClick={() => setShowCreate(false)}>Cancel</button>
                <button className="btn-primary" onClick={createUser}
                  disabled={!form.username || !form.email}>Create user</button>
              </div>
              <p className="muted" style={{ fontSize: ".78rem" }}>
                A temporary password will be generated and shown after creation. The user must change it on first login.
              </p>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
