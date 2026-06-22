import { useState, useRef, useEffect, useCallback } from "react";
import { api, Engagement } from "../lib/api";

interface Phase { label: string; detail: string; done: boolean; }

interface Turn {
  question: string;
  answer: string;
  toolUsed: string | null;
  directData: Record<string, unknown> | null;
  isLoading?: boolean;
  phases?: Phase[];
}

// ── Persist across navigation ──────────────────────────────────────────────────
const STORAGE_KEY = "assistant_state";
function loadState(): { engId: string; history: Turn[] } {
  try { const r = sessionStorage.getItem(STORAGE_KEY); return r ? JSON.parse(r) : { engId: "", history: [] }; }
  catch { return { engId: "", history: [] }; }
}
function saveState(s: { engId: string; history: Turn[] }) {
  try { sessionStorage.setItem(STORAGE_KEY, JSON.stringify(s)); } catch { /* quota */ }
}

// ── Inline markdown renderer ───────────────────────────────────────────────────
function renderMarkdown(text: string) {
  return text.split("\n").map((line, i) => {
    const trimmed = line.trim();
    if (!trimmed) return <br key={i} />;

    // Bold **text**
    const parts = trimmed.split(/(\*\*[^*]+\*\*)/g).map((p, j) =>
      p.startsWith("**") && p.endsWith("**")
        ? <strong key={j}>{p.slice(2, -2)}</strong>
        : p
    );

    // Bullet
    if (trimmed.startsWith("* ") || trimmed.startsWith("- ") || trimmed.startsWith("+ "))
      return <li key={i}>{parts.slice(1)}{/* drop bullet char */}
               {trimmed.slice(2).split(/(\*\*[^*]+\*\*)/g).map((p, j) =>
                 p.startsWith("**") && p.endsWith("**") ? <strong key={j}>{p.slice(2,-2)}</strong> : p
               )}
             </li>;

    // Header
    if (trimmed.startsWith("## ")) return <h4 key={i}>{trimmed.slice(3)}</h4>;
    if (trimmed.startsWith("# "))  return <h3 key={i}>{trimmed.slice(2)}</h3>;

    return <p key={i}>{parts}</p>;
  });
}

// ── Direct data cards ─────────────────────────────────────────────────────────
function PrereqCard({ data }: { data: Record<string, unknown> }) {
  const entries = Object.entries(data);
  return (
    <div className="direct-card">
      {entries.map(([key, val]) => {
        const v = String(val);
        const isOk = v.startsWith("ok") || v.startsWith("implied");
        const isUnknown = v.startsWith("unknown");
        const label = key.replace(/_/g, " ").replace(/\b\w/g, c => c.toUpperCase());
        return (
          <div key={key} className={"prereq-row " + (isOk ? "ok" : isUnknown ? "unknown" : "warn")}>
            <span className="prereq-icon">{isOk ? "✓" : isUnknown ? "?" : "!"}</span>
            <div>
              <span className="prereq-key">{label}</span>
              <span className="prereq-val">{v}</span>
            </div>
          </div>
        );
      })}
    </div>
  );
}

function StatsCard({ data }: { data: Record<string, unknown> }) {
  const pct = data.overall_percent_migrated as number ?? 0;
  const byType = data.by_type as { item_type: string; total: number; migrated: number; failed: number; pending: number }[] ?? [];
  return (
    <div className="direct-card">
      <div className="stats-pct-row">
        <span className="stats-pct-num">{pct}%</span>
        <span className="muted"> migrated overall</span>
      </div>
      <div className="progress-bar-wrap">
        <div className="progress-bar-fill" style={{ width: pct + "%" }} />
      </div>
      {byType.length > 0 && (
        <table className="stats-table">
          <thead><tr><th>Type</th><th>Total</th><th>Migrated</th><th>Failed</th><th>Pending</th></tr></thead>
          <tbody>
            {byType.map((r, i) => (
              <tr key={i}>
                <td>{r.item_type?.replace(/_/g, " ")}</td>
                <td>{r.total}</td>
                <td className="ok-text">{r.migrated}</td>
                <td className={r.failed > 0 ? "warn-text" : ""}>{r.failed}</td>
                <td>{r.pending}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

function ReconCard({ data }: { data: Record<string, unknown> }) {
  const rows = data.reconciliation as { item_type: string; match_status: string; n: number }[] ?? [];
  return (
    <div className="direct-card">
      <table className="stats-table">
        <thead><tr><th>Type</th><th>Status</th><th>Count</th></tr></thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i}>
              <td>{r.item_type?.replace(/_/g, " ")}</td>
              <td className={r.match_status === "matched" ? "ok-text" : "warn-text"}>
                {r.match_status?.replace(/_/g, " ")}
              </td>
              <td>{r.n}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}


function FailureCard({ data }: { data: Record<string, unknown> }) {
  if (data.message) return <div className="direct-card ok-text">{String(data.message)}</div>;
  const groups = data.error_groups as {
    error_pattern: string; count: number; title: string;
    explanation: string; fix: string;
    affected_items: { name: string; type: string; raw_error: string }[];
    has_more: boolean;
  }[] ?? [];
  return (
    <div className="direct-card">
      <div className="failure-summary">
        <span className="warn-text" style={{fontWeight:700, fontSize:"1.1rem"}}>{String(data.total_failed)}</span>
        <span className="muted"> failed item(s)</span>
      </div>
      {groups.map((g, i) => (
        <div key={i} className="failure-group">
          <div className="failure-group-header">
            <span className="warn-text failure-count">{g.count}×</span>
            <span className="failure-title">{g.title}</span>
          </div>
          <p className="failure-explain">{g.explanation}</p>
          <div className="failure-fix">
            <span className="fix-label">Fix: </span>{g.fix}
          </div>
          <div className="failure-items">
            {g.affected_items.map((item, j) => (
              <div key={j} className="failure-item">
                <span className="failure-item-name">{item.name}</span>
                <span className="failure-item-err muted">{item.raw_error?.slice(0, 120)}</span>
              </div>
            ))}
            {g.has_more && <span className="muted">…and more</span>}
          </div>
        </div>
      ))}
    </div>
  );
}

function RiskCard({ data }: { data: Record<string, unknown> }) {
  const risks = data.risks as {
    risk: string; severity: string; title: string;
    description: string; advice: string;
    items?: { name: string; size_mb?: number }[];
  }[] ?? [];
  const overall = String(data.overall ?? "");
  const isClean = risks.length === 0;
  return (
    <div className="direct-card">
      <div className={"risk-overall " + (isClean ? "ok-text" : overall.includes("High") ? "warn-text" : "")}>
        {isClean ? "✓ " : "⚠ "}{overall}
      </div>
      <div className="risk-stats muted" style={{fontSize:".8rem", marginBottom:".5rem"}}>
        {String(data.total_items ?? 0)} total items · {String(data.managed_accounts ?? 0)} managed accounts
      </div>
      {risks.map((r, i) => (
        <div key={i} className={"risk-item severity-" + r.severity}>
          <div className="risk-item-header">
            <span className={"risk-badge " + r.severity}>{r.severity}</span>
            <span className="risk-title">{r.title}</span>
          </div>
          <p className="risk-desc">{r.description}</p>
          <p className="risk-advice"><span className="fix-label">Advice: </span>{r.advice}</p>
          {r.items && r.items.length > 0 && (
            <div className="risk-items">
              {r.items.slice(0, 5).map((item, j) => (
                <span key={j} className="risk-item-name muted">
                  {item.name}{item.size_mb ? ` (${item.size_mb}MB)` : ""}
                </span>
              ))}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}

function DirectCard({ tool, data }: { tool: string | null; data: Record<string, unknown> }) {
  if (data.error) return <div className="direct-card warn-text">{String(data.error)}</div>;
  if (tool === "check_prerequisites")   return <PrereqCard data={data} />;
  if (tool === "migration_stats")       return <StatsCard data={data} />;
  if (tool === "reconciliation_status") return <ReconCard data={data} />;
  if (tool === "explain_failures")      return <FailureCard data={data} />;
  if (tool === "risk_scan")             return <RiskCard data={data} />;
  return <pre className="direct-raw">{JSON.stringify(data, null, 2)}</pre>;
}

// ── Starter prompts ───────────────────────────────────────────────────────────
const STARTERS = [
  "Please verify that my API accounts have the necessary permissions and that unlimited vault access is on",
  "What percentage of my vault has migrated, how many secrets, and what days did we migrate data?",
  "Take an overall look at my environment and suggest how I should approach the migration",
  "What failed in my last migration run and why?",
  "Scan my environment for risks before I start migrating",
  "Recent activity — what has been happening in this engagement?",
  "Help",
];

// ── Main component ────────────────────────────────────────────────────────────
export function Assistant() {
  const [engagements, setEngagements] = useState<Engagement[]>([]);
  const persisted = loadState();
  const [engId, setEngId]   = useState<string>(persisted.engId);
  const [history, setHistory] = useState<Turn[]>(persisted.history);
  const [input, setInput]   = useState("");
  const [busy, setBusy]     = useState(false);
  const bottomRef = useRef<HTMLDivElement>(null);
  const abortRef  = useRef<AbortController | null>(null);

  useEffect(() => {
    saveState({ engId, history: history.filter(t => !t.isLoading) });
  }, [engId, history]);

  useEffect(() => {
    api.listEngagements().then(e => {
      setEngagements(e);
      if (!persisted.engId && e.length === 1) setEngId(e[0].id);
    }).catch(() => {});
  }, []);

  useEffect(() => { bottomRef.current?.scrollIntoView({ behavior: "smooth" }); }, [history]);

  const abort = useCallback(() => {
    abortRef.current?.abort();
    abortRef.current = null;
    setBusy(false);
    setHistory(h => h.map(t => t.isLoading ? { ...t, isLoading: false, answer: t.answer || "(cancelled)" } : t));
  }, []);

  async function ask(question: string) {
    if (!engId || !question.trim() || busy) return;
    const controller = new AbortController();
    abortRef.current = controller;
    setHistory(h => [...h, { question, answer: "", toolUsed: null, directData: null, isLoading: true }]);
    setInput("");
    setBusy(true);

    let toolUsed: string | null = null;
    let accumulated = "";
    let directData: Record<string, unknown> | null = null;

    try {
      const convHistory = history.filter(t => !t.isLoading).slice(-2)
        .map(t => ({ question: t.question, answer: t.answer }));

      const res = await fetch(`/api/engagements/${engId}/assistant`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ question, history: convHistory }),
        signal: controller.signal,
      });
      if (!res.ok || !res.body) throw new Error(`${res.status} ${res.statusText}`);

      const reader  = res.body.getReader();
      const decoder = new TextDecoder();
      let   buffer  = "";

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split("\n");
        buffer = lines.pop() ?? "";   // keep incomplete last line

        for (const line of lines) {
          if (!line.startsWith("data: ")) continue;
          const payload = line.slice(6).trim();
          if (!payload) continue;
          let msg: Record<string, string>;
          try { msg = JSON.parse(payload); } catch { continue; }

          if (msg.type === "phase") {
            setHistory(h => {
              const prev = h[h.length - 1];
              const old  = prev?.phases ?? [];
              return [...h.slice(0, -1), {
                ...prev,
                phases: [...old.map(p => ({ ...p, done: true })),
                         { label: msg.phase, detail: msg.detail, done: false }],
              }];
            });
          } else if (msg.type === "tool") {
            toolUsed = msg.tool;
            setHistory(h => [...h.slice(0, -1), { ...h[h.length-1], toolUsed }]);
          } else if (msg.type === "direct") {
            // msg.data is already a parsed object (embedded JSON in the SSE payload)
            const raw = msg.data as unknown;
            directData = (raw && typeof raw === "object")
              ? raw as Record<string, unknown>
              : (() => { try { return JSON.parse(String(raw)); } catch { return null; } })();
            setHistory(h => [...h.slice(0, -1), { ...h[h.length-1], directData, isLoading: false,
              phases: (h[h.length-1]?.phases ?? []).map(p => ({ ...p, done: true })) }]);
          } else if (msg.type === "token") {
            accumulated += msg.text;
            setHistory(h => [...h.slice(0, -1), { ...h[h.length-1], answer: accumulated }]);
          } else if (msg.type === "done") {
            setHistory(h => [...h.slice(0, -1), { ...h[h.length-1],
              answer: accumulated, isLoading: false,
              phases: (h[h.length-1]?.phases ?? []).map(p => ({ ...p, done: true })) }]);
          } else if (msg.type === "error") {
            setHistory(h => [...h.slice(0, -1), { ...h[h.length-1],
              answer: "Error: " + msg.message, isLoading: false }]);
          }
        }
      }
    } catch (err: unknown) {
      if ((err as Error)?.name === "AbortError") return;
      const msg = err instanceof Error ? err.message : "Unknown error";
      setHistory(h => [...h.slice(0, -1), { ...h[h.length-1], answer: "Error: " + msg, isLoading: false }]);
    } finally {
      abortRef.current = null;
      setBusy(false);
    }
  }

  function handleKey(e: React.KeyboardEvent) {
    if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); ask(input); }
  }

  const selectedEng = engagements.find(e => e.id === engId);

  return (
    <div className="assistant-page">
      <div className="assistant-header">
        <div>
          <h2>Migration Assistant</h2>
          <p className="muted">Read-only AI advisor · powered by local Ollama · never writes to tenants</p>
        </div>
        <div className="assistant-header-right">
          <select className="assistant-eng-select" value={engId}
            onChange={e => { setEngId(e.target.value); setHistory([]); }}>
            <option value="">— select engagement —</option>
            {engagements.map(e => <option key={e.id} value={e.id}>{e.name} · {e.customer_name}</option>)}
          </select>
          {history.length > 0 &&
            <button className="btn-ghost" onClick={() => { setHistory([]); saveState({ engId, history: [] }); }}>Clear</button>}
        </div>
      </div>

      {!engId ? (
        <div className="assistant-empty">
          <div className="assistant-empty-icon">🤖</div>
          <p>Select an engagement above to start asking questions.</p>
        </div>
      ) : (
        <>
          {history.length === 0 && (
            <div className="assistant-starters">
              <p className="muted">Try one of these for <strong>{selectedEng?.name}</strong>:</p>
              <div className="starter-grid">
                {STARTERS.map(s => <button key={s} className="starter-btn" onClick={() => ask(s)} disabled={busy}>{s}</button>)}
              </div>
            </div>
          )}

          <div className="assistant-chat">
            {history.map((turn, i) => (
              <div key={i} className="chat-pair">
                <div className="chat-bubble user-bubble">
                  <span className="bubble-label">You</span>
                  <p>{turn.question}</p>
                </div>
                <div className={"chat-bubble ai-bubble" + (turn.isLoading ? " loading" : "")}>
                  <span className="bubble-label">
                    Assistant
                    {turn.toolUsed && <span className="tool-tag">via {turn.toolUsed.replace(/_/g, " ")}</span>}
                    {turn.isLoading && <span className="streaming-dot" />}
                  </span>

                  {/* Phase timeline */}
                  {turn.phases && turn.phases.length > 0 && (
                    <div className="phase-timeline">
                      {turn.phases.map((ph, pi) => (
                        <div key={pi} className={"phase-step " + (ph.done ? "done" : "active")}>
                          <span className="phase-icon">{ph.done ? "✓" : "●"}</span>
                          <span className="phase-label">{ph.detail}</span>
                          {!ph.done && <span className="streaming-dot" />}
                        </div>
                      ))}
                    </div>
                  )}

                  {/* Direct structured card — instant, no LLM */}
                  {turn.directData && <DirectCard tool={turn.toolUsed} data={turn.directData} />}

                  {/* LLM streamed answer with markdown */}
                  {turn.answer && (
                    <div className="ai-answer">
                      <ul className="ai-bullets">
                        {renderMarkdown(turn.answer)}
                      </ul>
                    </div>
                  )}

                  {/* Waiting state */}
                  {turn.isLoading && !turn.answer && !turn.directData && (!turn.phases || turn.phases.length === 0) && (
                    <p className="thinking">Starting up…</p>
                  )}
                </div>
              </div>
            ))}
            <div ref={bottomRef} />
          </div>

          <div className="assistant-input-row">
            <textarea className="assistant-input" rows={2}
              placeholder="Ask about this migration…  (Enter to send, Shift+Enter for newline)"
              value={input} onChange={e => setInput(e.target.value)}
              onKeyDown={handleKey} disabled={busy} />
            {busy
              ? <button className="btn-danger assistant-send" onClick={abort}>■ Stop</button>
              : <button className="btn-primary assistant-send" onClick={() => ask(input)} disabled={!input.trim()}>Ask</button>}
          </div>
        </>
      )}
    </div>
  );
}
