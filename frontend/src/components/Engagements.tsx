import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { api, type Engagement } from "../lib/api";

export function Engagements() {
  const [rows, setRows] = useState<Engagement[]>([]);
  const [err, setErr] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [customer, setCustomer] = useState("");
  const [creating, setCreating] = useState(false);
  const navigate = useNavigate();

  const load = () => api.listEngagements().then(setRows).catch((e) => setErr(String(e)));
  useEffect(() => { load(); }, []);

  const create = async () => {
    if (!name.trim() || !customer.trim()) return;
    setCreating(true);
    setErr(null);
    try {
      await api.createEngagement(name.trim(), customer.trim());
      setName("");
      setCustomer("");
      await load();
    } catch (e) {
      setErr(String(e));
    } finally {
      setCreating(false);
    }
  };

  return (
    <div>
      <header className="page-head"><h1>Engagements</h1></header>

      {err && <div className="panel bad-note">{err}</div>}

      <div className="panel" style={{ marginBottom: 16 }}>
        <h2>New engagement</h2>
        <p className="muted" style={{ marginBottom: 12 }}>
          An engagement is one customer migration. Create one, then set up its tenant
          connections on the Pre-migration tab.
        </p>
        <div className="create-row">
          <label className="field">
            <span>Engagement name</span>
            <input value={name} placeholder="e.g. Q3 PAS Migration"
              onChange={(e) => setName(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && create()} />
          </label>
          <label className="field">
            <span>Customer name</span>
            <input value={customer} placeholder="e.g. Acme Corp"
              onChange={(e) => setCustomer(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && create()} />
          </label>
          <button className="btn" onClick={create}
            disabled={creating || !name.trim() || !customer.trim()}>
            {creating ? "Creating…" : "Create"}
          </button>
        </div>
      </div>

      {rows.length === 0 ? (
        <div className="panel"><p className="muted">No engagements yet. Create one above to begin.</p></div>
      ) : (
        <table className="data">
          <thead>
            <tr><th>Name</th><th>Customer</th><th>Status</th><th>Created</th><th></th></tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id}>
                <td>{r.name}</td>
                <td>{r.customer_name}</td>
                <td><span className={`tag ${r.status}`}>{r.status}</span></td>
                <td className="muted">{new Date(r.created_at).toLocaleDateString()}</td>
                <td>
                  <button className="btn ghost small" onClick={() => navigate("/inventory")}>
                    Set up connections &rarr;
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
