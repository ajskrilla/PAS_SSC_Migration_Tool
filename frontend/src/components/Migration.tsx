import { useEffect, useState } from "react";
import {
  api, type Engagement, type SourceItemRow, type MigrateConnection,
  type MigrationJobResult, type MigrationReport,
} from "../lib/api";

type JobType = "text_secret" | "file_secret" | "account_unmanage_export" | "full";

const JOB_LABELS: Record<JobType, string> = {
  text_secret: "Text secrets",
  file_secret: "File secrets",
  account_unmanage_export: "Local accounts",
  full: "Full migration (all types)",
};

const jobTypeToItemType = (j: JobType) =>
  j === "account_unmanage_export" ? "account"
  : j === "full" ? undefined
  : j;

const blankConn = (): MigrateConnection => ({
  pasBaseUrl: "", pasAppId: "", pasClientId: "", pasClientSecret: "", pasScope: "",
  ssBaseUrl: "", ssPlatformBaseUrl: "", ssSecretServerBaseUrl: "", ssClientId: "", ssClientSecret: "",
});

export function Migration() {
  const [engagements, setEngagements] = useState<Engagement[]>([]);
  const [engagementId, setEngagementId] = useState("");
  const [jobType, setJobType] = useState<JobType>("text_secret");
  const [items, setItems] = useState<SourceItemRow[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [conn, setConn] = useState<MigrateConnection>(blankConn());
  const [staging, setStaging] = useState("");
  const [dryRun, setDryRun] = useState(true);
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<MigrationJobResult | null>(null);
  const [report, setReport] = useState<MigrationReport | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [confirmRevert, setConfirmRevert] = useState(false);
  const [runningJobId, setRunningJobId] = useState<string | null>(null);

  // While a migration is in flight, poll for the running job id so we can offer Abort.
  useEffect(() => {
    if (!running || !engagementId) { setRunningJobId(null); return; }
    const t = setInterval(async () => {
      try {
        const r = await api.runningJob(engagementId);
        setRunningJobId(r.running && r.job ? r.job.id : null);
      } catch { /* ignore */ }
    }, 1500);
    return () => clearInterval(t);
  }, [running, engagementId]);

  const abort = async () => {
    if (!runningJobId) return;
    try {
      await api.cancelJob(runningJobId);
      setMsg("Abort requested — the job will stop after the current item.");
    } catch (e) {
      setMsg(`Abort failed: ${String(e)}`);
    }
  };

  useEffect(() => {
    api.listEngagements().then((rows) => {
      setEngagements(rows);
      if (rows.length && !engagementId) setEngagementId(rows[0].id);
    }).catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const loadItems = () => {
    if (!engagementId) return;
    const t = jobTypeToItemType(jobType);
    api.sourceItems(engagementId, t).then((rows) => {
      setItems(rows);
      setSelected(new Set(rows.map((r) => r.source_native_id))); // default: all selected
    }).catch(() => setItems([]));
  };

  useEffect(loadItems, [engagementId, jobType]);

  const loadReport = () => {
    if (engagementId) api.migrationReport(engagementId).then(setReport).catch(() => {});
  };
  useEffect(loadReport, [engagementId]);

  const toggle = (id: string) =>
    setSelected((s) => {
      const n = new Set(s);
      n.has(id) ? n.delete(id) : n.add(id);
      return n;
    });
  const allSelected = items.length > 0 && selected.size === items.length;
  const toggleAll = () =>
    setSelected(allSelected ? new Set() : new Set(items.map((r) => r.source_native_id)));

  const cred = (k: keyof MigrateConnection, v: string) =>
    setConn((c) => ({ ...c, [k]: v }));

  const run = async () => {
    setRunning(true); setResult(null); setMsg(null);
    try {
      const r = await api.migrate(engagementId, {
        ...conn,
        jobType,
        dryRun,
        stagingFolderName: staging || undefined,
        selectedIds: jobType === "full" ? null : Array.from(selected),
      });
      setResult(r);
      loadReport();
    } catch (e) {
      setMsg(`Migration failed: ${String(e)}`);
    } finally {
      setRunning(false);
    }
  };

  const revert = async () => {
    setRunning(true); setMsg(null);
    try {
      const r = await api.revert(engagementId, conn);
      setMsg(`Reverted: deleted ${r.deleted}, failed ${r.failed}.`);
      setConfirmRevert(false);
      loadReport();
    } catch (e) {
      setMsg(`Revert failed: ${String(e)}`);
    } finally {
      setRunning(false);
    }
  };

  const credsReady = conn.pasClientId && conn.pasClientSecret && conn.ssClientId && conn.ssClientSecret;

  return (
    <div>
      <header className="page-head"><h1>Migration</h1></header>

      <div className="panel" style={{ marginBottom: 16 }}>
        <div className="create-row">
          <label className="field">
            <span>Engagement</span>
            <select value={engagementId} onChange={(e) => setEngagementId(e.target.value)}>
              {engagements.map((e) => <option key={e.id} value={e.id}>{e.name} — {e.customer_name}</option>)}
            </select>
          </label>
          <label className="field">
            <span>What to migrate</span>
            <select value={jobType} onChange={(e) => setJobType(e.target.value as JobType)}>
              {(Object.keys(JOB_LABELS) as JobType[]).map((j) => (
                <option key={j} value={j}>{JOB_LABELS[j]}</option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Staging folder name</span>
            <input value={staging} placeholder="PAS_Migration_<date>"
              onChange={(e) => setStaging(e.target.value)} />
          </label>
        </div>
        <p className="muted note">
          Everything is created under a single staging folder in the target, so a lab revert
          deletes only what this tool created.
        </p>
      </div>

      {/* Credentials */}
      <div className="panel" style={{ marginBottom: 16 }}>
        <div className="cred-label">Credentials · session only</div>
        <div className="conn-grid" style={{ marginTop: 10 }}>
          <div>
            <div className="phase-no">SOURCE · PAS</div>
            <Text label="Tenant base URL" v={conn.pasBaseUrl!} on={(x) => cred("pasBaseUrl", x)} />
            <Text label="OAuth2 App ID" v={conn.pasAppId!} on={(x) => cred("pasAppId", x)} />
            <Text label="Client ID" v={conn.pasClientId} on={(x) => cred("pasClientId", x)} />
            <Text label="Client secret" type="password" v={conn.pasClientSecret} on={(x) => cred("pasClientSecret", x)} />
            <Text label="Scope (optional)" v={conn.pasScope!} on={(x) => cred("pasScope", x)} />
          </div>
          <div>
            <div className="phase-no">TARGET · SECRET SERVER</div>
            <Text label="Platform base URL" v={conn.ssPlatformBaseUrl!} on={(x) => cred("ssPlatformBaseUrl", x)} />
            <Text label="Secret Server base URL" v={conn.ssSecretServerBaseUrl!} on={(x) => cred("ssSecretServerBaseUrl", x)} />
            <Text label="Client ID" v={conn.ssClientId} on={(x) => cred("ssClientId", x)} />
            <Text label="Client secret" type="password" v={conn.ssClientSecret} on={(x) => cred("ssClientSecret", x)} />
          </div>
        </div>
      </div>

      {/* Checklist */}
      {jobType !== "full" && (
        <div className="panel" style={{ marginBottom: 16 }}>
          <header className="page-head" style={{ marginBottom: 10 }}>
            <h2 style={{ margin: 0 }}>Select items · {JOB_LABELS[jobType]}</h2>
            <button className="btn ghost small" onClick={toggleAll}>
              {allSelected ? "Deselect all" : "Select all"}
            </button>
          </header>
          {items.length === 0 ? (
            <p className="muted">No items of this type in the latest source inventory.</p>
          ) : (
            <div className="checklist">
              {items.map((it) => (
                <label key={it.source_native_id} className="check-row">
                  <input type="checkbox" checked={selected.has(it.source_native_id)}
                    onChange={() => toggle(it.source_native_id)} />
                  <span className="check-name">{it.name}</span>
                  <span className="muted check-path">{it.folder_path || "—"}</span>
                </label>
              ))}
            </div>
          )}
          <p className="muted note">{selected.size} of {items.length} selected</p>
        </div>
      )}

      {/* Run controls */}
      <div className="panel" style={{ marginBottom: 16 }}>
        <div className="conn-actions" style={{ alignItems: "center" }}>
          <label className="check-row" style={{ border: "none", padding: 0 }}>
            <input type="checkbox" checked={dryRun} onChange={(e) => setDryRun(e.target.checked)} />
            <span>Dry run (no writes to target)</span>
          </label>
          <button className="btn" onClick={run} disabled={running || !credsReady || !engagementId}>
            {running ? "Running…" : dryRun ? "Run dry run" : "Run migration"}
          </button>
          {running && runningJobId && (
            <button className="btn" style={{ background: "var(--bad)", borderColor: "var(--bad)" }}
              onClick={abort}>
              Abort
            </button>
          )}
          {!confirmRevert ? (
            <button className="btn ghost" onClick={() => setConfirmRevert(true)} disabled={running}>
              Revert (lab)
            </button>
          ) : (
            <>
              <span className="result bad" style={{ padding: "6px 10px" }}>
                Delete all migrated target data?
              </span>
              <button className="btn" onClick={revert} disabled={running || !credsReady}>Yes, delete</button>
              <button className="btn ghost" onClick={() => setConfirmRevert(false)}>Cancel</button>
            </>
          )}
        </div>
        {!credsReady && <p className="muted note">Enter source and target credentials to enable run.</p>}
        {msg && <div className={`result ${msg.includes("failed") ? "bad" : "ok"}`}>{msg}</div>}
        {result && (
          <div className={`result ${result.error ? "bad" : "ok"}`}>
            {result.error
              ? `Error: ${result.error}`
              : `Done — ${result.succeeded} succeeded, ${result.failed} failed, ${result.skipped} skipped (of ${result.total}).`}
            {result.excluded.length > 0 && (
              <div style={{ marginTop: 6 }}>
                Excluded (unmanage failed): {result.excluded.map((x) => x.name).join(", ")}
              </div>
            )}
          </div>
        )}
      </div>

      {/* Report */}
      {report && report.items.length > 0 && (
        <div className="panel">
          <h2>Migration report</h2>
          <table className="data" style={{ marginTop: 10 }}>
            <thead>
              <tr><th>Type</th><th>Name</th><th>Folder</th><th>Status</th><th>Target ID</th><th>Error</th></tr>
            </thead>
            <tbody>
              {report.items.map((it, i) => (
                <tr key={i}>
                  <td><span className="tag">{it.item_type}</span></td>
                  <td>{it.source_name}</td>
                  <td className="muted">{it.source_folder_path || "—"}</td>
                  <td><span className={`tag ${it.status}`}>{it.status}</span></td>
                  <td className="muted">{it.target_native_id || "—"}</td>
                  <td className="muted">{it.last_error || "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function Text({ label, v, on, type = "text" }: {
  label: string; v: string; on: (x: string) => void; type?: string;
}) {
  return (
    <label className="field" style={{ marginBottom: 8 }}>
      <span>{label}</span>
      <input type={type} value={v} onChange={(e) => on(e.target.value)} autoComplete="off" spellCheck={false} />
    </label>
  );
}
