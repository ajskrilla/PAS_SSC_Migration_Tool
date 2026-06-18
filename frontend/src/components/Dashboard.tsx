import { useEffect, useState } from "react";
import {
  api, type Engagement, type SnapshotSummary, type InventoryItem, type ReconRow,
} from "../lib/api";

export function Dashboard() {
  const [ready, setReady] = useState<boolean | null>(null);
  const [engagements, setEngagements] = useState<Engagement[]>([]);
  const [engagementId, setEngagementId] = useState("");
  const [summaries, setSummaries] = useState<SnapshotSummary[]>([]);
  const [recon, setRecon] = useState<ReconRow[]>([]);
  const [drillSnapshot, setDrillSnapshot] = useState<string | null>(null);
  const [items, setItems] = useState<InventoryItem[]>([]);
  const [typeFilter, setTypeFilter] = useState<string>("");
  const [busy, setBusy] = useState(false);

  useEffect(() => { api.ready().then(setReady).catch(() => setReady(false)); }, []);

  useEffect(() => {
    api.listEngagements().then((rows) => {
      setEngagements(rows);
      if (rows.length && !engagementId) setEngagementId(rows[0].id);
    }).catch(() => { /* shown elsewhere */ });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const loadSummary = (id: string) => {
    if (!id) return;
    api.inventorySummary(id).then(setSummaries).catch(() => setSummaries([]));
    api.reconciliation(id).then(setRecon).catch(() => setRecon([]));
  };

  useEffect(() => { loadSummary(engagementId); }, [engagementId]);

  const reconcile = async () => {
    setBusy(true);
    try { await api.reconcile(engagementId); loadSummary(engagementId); }
    finally { setBusy(false); }
  };

  const drill = (snapshotId: string, type: string) => {
    setDrillSnapshot(snapshotId);
    setTypeFilter(type);
    api.snapshotItems(snapshotId, type || undefined).then(setItems).catch(() => setItems([]));
  };

  const source = summaries.find((s) => s.role === "source");
  const target = summaries.find((s) => s.role === "target");

  const reconCounts = recon.reduce<Record<string, number>>((acc, r) => {
    acc[r.match_status] = (acc[r.match_status] || 0) + 1; return acc;
  }, {});

  return (
    <div>
      <header className="page-head">
        <h1>Migration overview</h1>
        <span className={`pill ${ready ? "ok" : ready === false ? "bad" : ""}`}>
          API {ready === null ? "checking…" : ready ? "ready" : "unreachable"}
        </span>
      </header>

      <div className="panel" style={{ marginBottom: 16 }}>
        <label className="field">
          <span>Engagement</span>
          {engagements.length ? (
            <select value={engagementId} onChange={(e) => setEngagementId(e.target.value)}>
              {engagements.map((e) => (
                <option key={e.id} value={e.id}>{e.name} — {e.customer_name}</option>
              ))}
            </select>
          ) : <span className="muted">No engagements yet.</span>}
        </label>
      </div>

      {!source && !target ? (
        <div className="panel">
          <p className="muted">
            No inventory captured yet. Go to Pre-migration, test a connection, then Run inventory.
          </p>
        </div>
      ) : (
        <>
          <div className="grid">
            <SummaryCard title="Source · PAS" s={source} onDrill={drill} />
            <SummaryCard title="Target · Secret Server" s={target} onDrill={drill} />
            <section className="panel">
              <div className="phase-no">RECONCILIATION</div>
              <h2>Source vs target</h2>
              {recon.length === 0 ? (
                <p className="muted">Not computed yet.</p>
              ) : (
                <div className="recon-stats">
                  <Stat label="Matched" value={reconCounts.matched || 0} tone="ok" />
                  <Stat label="Source only" value={reconCounts.source_only || 0} tone="warn" />
                  <Stat label="Target only" value={reconCounts.target_only || 0} tone="muted" />
                </div>
              )}
              <button className="btn small" onClick={reconcile} disabled={busy || !engagementId}
                style={{ marginTop: 12 }}>
                {busy ? "Computing…" : "Recompute diff"}
              </button>
            </section>
          </div>

          {drillSnapshot && (
            <div className="panel" style={{ marginTop: 16 }}>
              <header className="page-head" style={{ marginBottom: 12 }}>
                <h2 style={{ margin: 0 }}>Items {typeFilter && `· ${typeFilter}`}</h2>
                <span className="muted">{items.length} shown</span>
              </header>
              <table className="data">
                <thead>
                  <tr><th>Type</th><th>Name</th><th>Folder path</th><th>Managed</th><th>Size</th></tr>
                </thead>
                <tbody>
                  {items.map((it) => (
                    <tr key={`${it.item_type}-${it.source_native_id}`}>
                      <td><span className="tag">{it.item_type}</span></td>
                      <td>{it.name}</td>
                      <td className="muted">{it.folder_path || "—"}</td>
                      <td>{it.is_managed === null ? "—" : it.is_managed ? "yes" : "no"}</td>
                      <td className="muted">{it.size_bytes ? `${it.size_bytes} B` : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </div>
  );
}

function SummaryCard({
  title, s, onDrill,
}: {
  title: string; s?: SnapshotSummary; onDrill: (snap: string, type: string) => void;
}) {
  if (!s) return (
    <section className="panel"><h2>{title}</h2><p className="muted">No snapshot.</p></section>
  );
  const m = s.summary;
  const rows: [string, number, string][] = [
    ["Accounts", m.accounts, "account"],
    ["Text secrets", m.text_secrets, "text_secret"],
    ["File secrets", m.file_secrets, "file_secret"],
    ["Folders", m.folders, "folder"],
  ];
  return (
    <section className="panel">
      <div className="phase-no">{title.includes("Source") ? "SOURCE" : "TARGET"}</div>
      <h2>{title}</h2>
      <div className="count-list">
        {rows.map(([label, n, type]) => (
          <button key={type} className="count-row" onClick={() => onDrill(s.snapshot_id, type)}>
            <span>{label}</span><strong>{n}</strong>
          </button>
        ))}
      </div>
      <p className="muted note">
        {m.managed} managed · {m.unmanaged} unmanaged · captured {new Date(s.captured_at).toLocaleString()}
      </p>
    </section>
  );
}

function Stat({ label, value, tone }: { label: string; value: number; tone: string }) {
  return (
    <div className="stat">
      <div className={`stat-value ${tone}`}>{value}</div>
      <div className="stat-label">{label}</div>
    </div>
  );
}
