import { useEffect, useState } from "react";
import { api, type Engagement, type LogRow } from "../lib/api";

const outcomeTone = (o: string | null) => {
  if (!o) return "";
  const s = o.toLowerCase();
  if (s.includes("fail") || s.includes("excluded") || s.includes("error")) return "bad";
  if (s.includes("created") || s.includes("ok") || s.includes("ready") || s.includes("verified")) return "ok";
  return "";
};

const PAGE_SIZE = 50;

export function Logs() {
  const [engagements, setEngagements] = useState<Engagement[]>([]);
  const [engagementId, setEngagementId] = useState("");
  const [rows, setRows] = useState<LogRow[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(0);   // zero-based
  const [auto, setAuto] = useState(false);
  const [search, setSearch] = useState("");
  const [eventType, setEventType] = useState("");
  const [failuresOnly, setFailuresOnly] = useState(false);

  useEffect(() => {
    api.listEngagements().then((r) => {
      setEngagements(r);
      if (r.length && !engagementId) setEngagementId(r[0].id);
    }).catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const load = (p = page) => {
    if (!engagementId) return;
    api.logs(engagementId, PAGE_SIZE, p * PAGE_SIZE, search, eventType, failuresOnly)
      .then((res) => { setRows(res.rows); setTotal(res.total); })
      .catch(() => {});
  };

  // Reset to first page when the engagement changes.
  useEffect(() => { setPage(0); load(0); /* eslint-disable-next-line */ }, [engagementId]);
  // Reload when the page changes.
  useEffect(() => { load(page); /* eslint-disable-next-line */ }, [page]);
  // Any filter change invalidates the current page — go back to page 0. A debounce on the free-
  // text box avoids firing a request on every keystroke.
  useEffect(() => {
    const t = setTimeout(() => { page === 0 ? load(0) : setPage(0); }, 300);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search, eventType, failuresOnly]);

  useEffect(() => {
    if (!auto) return;
    const t = setInterval(() => load(page), 3000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auto, engagementId, page]);

  const lastPage = Math.max(0, Math.ceil(total / PAGE_SIZE) - 1);
  const from = total === 0 ? 0 : page * PAGE_SIZE + 1;
  const to = Math.min(total, (page + 1) * PAGE_SIZE);

  return (
    <div>
      <header className="page-head">
        <h1>Logs</h1>
        <div style={{ display: "flex", gap: 12, alignItems: "center" }}>
          <label className="check-row" style={{ border: "none", padding: 0 }}>
            <input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} />
            <span className="muted">Auto-refresh</span>
          </label>
          <button className="btn ghost small" onClick={() => load(page)}>Refresh</button>
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

      <div className="panel" style={{ marginBottom: 16 }}>
        <div className="create-row">
          <label className="field" style={{ flex: 2 }}>
            <span>Search</span>
            <input type="text" placeholder="Search action, outcome, or message…"
              value={search} onChange={(e) => setSearch(e.target.value)} />
          </label>
          <label className="field">
            <span>Type</span>
            <select value={eventType} onChange={(e) => setEventType(e.target.value)}>
              <option value="">All types</option>
              <option value="api_call">API call</option>
              <option value="user_action">User action</option>
            </select>
          </label>
          <label className="check-row" style={{ alignSelf: "flex-end" }}>
            <input type="checkbox" checked={failuresOnly} onChange={(e) => setFailuresOnly(e.target.checked)} />
            <span>Failures only</span>
          </label>
        </div>
      </div>

      {rows.length === 0 ? (
        <div className="panel">
          <p className="muted">
            {search || eventType || failuresOnly
              ? "No log entries match these filters."
              : "No log entries yet for this engagement."}
          </p>
        </div>
      ) : (
        <>
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

          <div className="page-controls" style={{
            display: "flex", justifyContent: "space-between", alignItems: "center", marginTop: 12,
          }}>
            <span className="muted">{from}–{to} of {total}</span>
            <div style={{ display: "flex", gap: 8 }}>
              <button className="btn ghost small" onClick={() => setPage(0)} disabled={page === 0}>« First</button>
              <button className="btn ghost small" onClick={() => setPage((p) => Math.max(0, p - 1))} disabled={page === 0}>‹ Prev</button>
              <span className="muted" style={{ alignSelf: "center" }}>Page {page + 1} of {lastPage + 1}</span>
              <button className="btn ghost small" onClick={() => setPage((p) => Math.min(lastPage, p + 1))} disabled={page >= lastPage}>Next ›</button>
              <button className="btn ghost small" onClick={() => setPage(lastPage)} disabled={page >= lastPage}>Last »</button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
