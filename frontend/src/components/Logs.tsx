import { useEffect, useState } from "react";
import { api, type Engagement, type LogRow } from "../lib/api";

const outcomeTone = (o: string | null) => {
  if (!o) return "";
  const s = o.toLowerCase();
  if (s.includes("fail") || s.includes("excluded") || s.includes("error")) return "bad";
  if (s.includes("created") || s.includes("ok") || s.includes("ready") || s.includes("verified")) return "ok";
  return "";
};

export function Logs() {
  const [engagements, setEngagements] = useState<Engagement[]>([]);
  const [engagementId, setEngagementId] = useState("");
  const [rows, setRows] = useState<LogRow[]>([]);
  const [auto, setAuto] = useState(false);

  useEffect(() => {
    api.listEngagements().then((r) => {
      setEngagements(r);
      if (r.length && !engagementId) setEngagementId(r[0].id);
    }).catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const load = () => { if (engagementId) api.logs(engagementId, 300).then(setRows).catch(() => {}); };
  useEffect(load, [engagementId]);

  useEffect(() => {
    if (!auto) return;
    const t = setInterval(load, 3000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auto, engagementId]);

  return (
    <div>
      <header className="page-head">
        <h1>Logs</h1>
        <div style={{ display: "flex", gap: 12, alignItems: "center" }}>
          <label className="check-row" style={{ border: "none", padding: 0 }}>
            <input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} />
            <span className="muted">Auto-refresh</span>
          </label>
          <button className="btn ghost small" onClick={load}>Refresh</button>
        </div>
      </header>

      <div className="panel" style={{ marginBottom: 16 }}>
        <label className="field">
          <span>Engagement</span>
          <select value={engagementId} onChange={(e) => setEngagementId(e.target.value)}>
            {engagements.map((e) => <option key={e.id} value={e.id}>{e.name} — {e.customer_name}</option>)}
          </select>
        </label>
      </div>

      {rows.length === 0 ? (
        <div className="panel"><p className="muted">No log entries yet for this engagement.</p></div>
      ) : (
        <table className="data">
          <thead>
            <tr><th>Time</th><th>Type</th><th>Action</th><th>Outcome</th><th>Detail</th></tr>
          </thead>
          <tbody>
            {rows.map((r, i) => (
              <tr key={i}>
                <td className="muted" style={{ whiteSpace: "nowrap" }}>
                  {new Date(r.occurred_at).toLocaleTimeString()}
                </td>
                <td><span className="tag">{r.event_type}</span></td>
                <td>{r.action || "—"}</td>
                <td><span className={`tag ${outcomeTone(r.outcome)}`}>{r.outcome || "—"}</span></td>
                <td className="muted log-detail">{r.message || "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
