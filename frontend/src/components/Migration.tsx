import { useEffect, useState } from "react";
import {
  api, type Engagement, type SourceItemRow, type MigrateConnection,
  type MigrationJobResult, type MigrationReport, type MigrationStatus, type TemplateOption,
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
  const [status, setStatus] = useState<MigrationStatus | null>(null);
  const [templates, setTemplates] = useState<TemplateOption[]>([]);
  const [textTemplateId, setTextTemplateId] = useState<number | "">("");
  const [fileTemplateId, setFileTemplateId] = useState<number | "">("");
  const [tplMsg, setTplMsg] = useState<string | null>(null);

  // Pick a sensible default template by name (user can override).
  const pickDefault = (opts: TemplateOption[], wanted: RegExp, exclude?: RegExp) => {
    const m = opts.find((o) => wanted.test(o.name) && (!exclude || !exclude.test(o.name)));
    return m ? m.id : "";
  };

  const loadTemplates = async () => {
    setTplMsg("Loading templates…");
    try {
      const opts = await api.listTemplates(conn as unknown as Record<string, unknown>);
      setTemplates(opts);
      // Defaults: exact "Password" for text; a file-ish template for files.
      setTextTemplateId((cur) => cur || pickDefault(opts, /^password$/i) || pickDefault(opts, /password/i));
      setFileTemplateId((cur) => cur || pickDefault(opts, /^file$/i) || pickDefault(opts, /file/i));
      setTplMsg(`Loaded ${opts.length} templates.`);
    } catch (e) {
      setTplMsg(`Couldn't load templates: ${String(e)}. Enter SS credentials and try again.`);
    }
  };

  const createFileTemplate = async () => {
    setTplMsg("Creating file template…");
    try {
      const created = await api.createFileTemplate(
        conn as unknown as Record<string, unknown>, "Migration File Template");
      setTplMsg(`Created "${created.name}" (#${created.id}).`);
      await loadTemplates();          // refresh the list
      setFileTemplateId(created.id);  // and select the new one
    } catch (e) {
      setTplMsg(`Couldn't create template: ${String(e)}`);
    }
  };

  const loadStatus = (eng: string) => {
    if (!eng) { setStatus(null); return; }
    api.migrationStatus(eng).then(setStatus).catch(() => setStatus(null));
  };
  // Refresh the delta whenever the engagement changes or a run finishes.
  useEffect(() => { loadStatus(engagementId); }, [engagementId]);
  useEffect(() => { if (!running) loadStatus(engagementId); }, [running]);

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
        textTemplateId: textTemplateId === "" ? undefined : textTemplateId,
        fileTemplateId: fileTemplateId === "" ? undefined : fileTemplateId,
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

  const exportReportCsv = () => {
    if (!report) return;
    const esc = (v: string) => `"${String(v ?? "").replace(/"/g, '""')}"`;
    const header = ["Type", "Name", "Folder", "Status", "Target ID", "Error"];
    const lines = [header.map(esc).join(",")];
    for (const it of report.items) {
      lines.push([
        it.item_type, it.source_name, it.source_folder_path || "",
        it.status, it.target_native_id || "", it.last_error || "",
      ].map((v) => esc(String(v))).join(","));
    }
    const blob = new Blob([lines.join("\r\n")], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `migration-report-${new Date().toISOString().slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const escapeHtml = (s: string) =>
    s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

  const exportReportPdf = () => {
    if (!report) return;
    // Dependency-free PDF: open a print-friendly window and let the browser "Save as PDF".
    const rows = report.items.map((it) => `
      <tr>
        <td>${escapeHtml(it.item_type)}</td><td>${escapeHtml(it.source_name)}</td>
        <td>${escapeHtml(it.source_folder_path || "—")}</td>
        <td>${escapeHtml(it.status)}</td><td>${escapeHtml(it.target_native_id || "—")}</td>
        <td>${escapeHtml(it.last_error || "—")}</td>
      </tr>`).join("");
    const job = report.jobs[0];
    const summary = job
      ? `<p>Job ${escapeHtml(job.job_type)} · ${escapeHtml(job.mode)} · ${escapeHtml(job.status)} —
         total ${job.total}, succeeded ${job.succeeded}, failed ${job.failed}, skipped ${job.skipped}</p>`
      : "";
    const w = window.open("", "_blank");
    if (!w) return;
    w.document.write(`<!doctype html><html><head><title>Migration Report</title>
      <style>
        body{font-family:system-ui,sans-serif;padding:24px;color:#111}
        h1{font-size:18px} table{border-collapse:collapse;width:100%;font-size:12px;margin-top:12px}
        th,td{border:1px solid #ccc;padding:6px 8px;text-align:left;vertical-align:top}
        th{background:#f3f3f3}
      </style></head><body>
      <h1>PAS &rarr; Secret Server Migration Report</h1>
      <p>Generated ${new Date().toLocaleString()}</p>${summary}
      <table><thead><tr><th>Type</th><th>Name</th><th>Folder</th><th>Status</th>
      <th>Target ID</th><th>Error</th></tr></thead><tbody>${rows}</tbody></table>
      </body></html>`);
    w.document.close();
    w.focus();
    setTimeout(() => w.print(), 300);
  };

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
            <input value={staging} placeholder="PAS_Migration"
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

      {/* Template picker (text + file secrets) */}
      {(jobType === "text_secret" || jobType === "file_secret" || jobType === "full") && (
        <div className="panel" style={{ marginBottom: 16 }}>
          <header className="page-head" style={{ marginBottom: 10 }}>
            <h2 style={{ margin: 0 }}>Target templates</h2>
            <button className="btn ghost small" onClick={loadTemplates}
              disabled={!conn.ssClientId || !conn.ssClientSecret}>
              Load templates
            </button>
          </header>
          {tplMsg && <p className="muted" style={{ marginTop: 0 }}>{tplMsg}</p>}
          <div className="grid-2">
            {(jobType === "text_secret" || jobType === "full") && (
              <label className="field">
                <span>Text-secret template</span>
                <select value={textTemplateId}
                  onChange={(e) => setTextTemplateId(e.target.value === "" ? "" : Number(e.target.value))}
                  disabled={templates.length === 0}>
                  <option value="">{templates.length ? "— select —" : "Load templates first"}</option>
                  {templates.map((t) => <option key={t.id} value={t.id}>{t.name} (#{t.id})</option>)}
                </select>
              </label>
            )}
            {(jobType === "file_secret" || jobType === "full") && (
              <label className="field">
                <span>File-secret template</span>
                <select value={fileTemplateId}
                  onChange={(e) => setFileTemplateId(e.target.value === "" ? "" : Number(e.target.value))}
                  disabled={templates.length === 0}>
                  <option value="">{templates.length ? "— select —" : "Load templates first"}</option>
                  {templates.map((t) => <option key={t.id} value={t.id}>{t.name} (#{t.id})</option>)}
                </select>
                <button className="btn ghost small" style={{ marginTop: 6 }}
                  onClick={createFileTemplate}
                  disabled={!conn.ssClientId || !conn.ssClientSecret}>
                  Create file template
                </button>
              </label>
            )}
          </div>
          <p className="muted" style={{ marginBottom: 0, fontSize: 12 }}>
            Defaults are auto-selected by name; override if your tenant uses different templates.
            The file field is auto-detected from the chosen template.
          </p>
        </div>
      )}

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

      {/* Migration status / delta */}
      {status && status.summary.length > 0 && (
        <div className="panel">
          <div className="page-head" style={{ marginBottom: 10 }}>
            <h2 style={{ margin: 0 }}>Migration status</h2>
            <button className="btn ghost small" onClick={() => loadStatus(engagementId)}>Refresh</button>
          </div>
          <table className="data">
            <thead>
              <tr><th>Type</th><th>Total</th><th>Migrated</th><th>Pending</th><th>Failed</th><th>Progress</th></tr>
            </thead>
            <tbody>
              {status.summary.map((s) => {
                const pct = s.total > 0 ? Math.round((s.migrated / s.total) * 100) : 0;
                return (
                  <tr key={s.item_type}>
                    <td>{s.item_type}</td>
                    <td>{s.total}</td>
                    <td><span className="tag ok">{s.migrated}</span></td>
                    <td>{s.pending}</td>
                    <td>{s.failed > 0 ? <span className="tag bad">{s.failed}</span> : "0"}</td>
                    <td>
                      <div className="progress-track">
                        <div className="progress-fill" style={{ width: `${pct}%` }} />
                      </div>
                      <span className="muted" style={{ fontSize: 12 }}>{pct}%</span>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {/* Report */}
      {report && report.items.length > 0 && (
        <div className="panel">
          <header className="page-head">
            <h2 style={{ margin: 0 }}>Migration report</h2>
            <div style={{ display: "flex", gap: 8 }}>
              <button className="btn ghost small" onClick={exportReportCsv}>Export CSV</button>
              <button className="btn ghost small" onClick={exportReportPdf}>Export PDF</button>
            </div>
          </header>
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
