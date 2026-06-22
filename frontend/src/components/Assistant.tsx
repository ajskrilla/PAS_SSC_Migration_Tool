import { useState, useRef, useEffect, useCallback } from "react";
import { api, Engagement } from "../lib/api";

interface Turn {
  question: string;
  answer: string;
  toolUsed: string | null;
  isLoading?: boolean;
}

// ── Persist chat history in sessionStorage so navigation doesn't wipe it ──────
const STORAGE_KEY = "assistant_state";
interface PersistedState { engId: string; history: Turn[] }

function loadState(): PersistedState {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    return raw ? JSON.parse(raw) : { engId: "", history: [] };
  } catch { return { engId: "", history: [] }; }
}

function saveState(s: PersistedState) {
  try { sessionStorage.setItem(STORAGE_KEY, JSON.stringify(s)); } catch { /* quota */ }
}

const STARTERS = [
  "Please verify that my API accounts have the necessary permissions and that unlimited vault access is on",
  "What percentage of my vault has migrated, how many secrets, and what days did we migrate data?",
  "Take an overall look at my environment and suggest how I should approach the migration",
  "What failed in my last migration run and why?",
  "Recent activity — what has been happening in this engagement?",
  "Help",
];

export function Assistant() {
  const [engagements, setEngagements] = useState<Engagement[]>([]);
  const persisted = loadState();
  const [engId, setEngId] = useState<string>(persisted.engId);
  const [history, setHistory] = useState<Turn[]>(persisted.history);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const bottomRef = useRef<HTMLDivElement>(null);
  const abortRef = useRef<AbortController | null>(null);

  // Persist state whenever it changes
  useEffect(() => {
    saveState({ engId, history: history.filter(t => !t.isLoading) });
  }, [engId, history]);

  useEffect(() => {
    api.listEngagements().then((e) => {
      setEngagements(e);
      if (!persisted.engId && e.length === 1) setEngId(e[0].id);
    }).catch(() => {});
  }, []);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [history]);

  const abort = useCallback(() => {
    if (abortRef.current) {
      abortRef.current.abort();
      abortRef.current = null;
    }
    setBusy(false);
    // Mark any loading turn as cancelled
    setHistory(h => h.map(t =>
      t.isLoading ? { ...t, isLoading: false, answer: t.answer || "(cancelled)" } : t
    ));
  }, []);

  async function ask(question: string) {
    if (!engId || !question.trim() || busy) return;

    const controller = new AbortController();
    abortRef.current = controller;

    setHistory(h => [...h, { question, answer: "", toolUsed: null, isLoading: true }]);
    setInput("");
    setBusy(true);

    let toolUsed: string | null = null;
    let accumulated = "";

    try {
      const conversationHistory = history
        .filter(t => !t.isLoading)
        .slice(-4)
        .map(t => ({ question: t.question, answer: t.answer }));

      const res = await fetch(`/api/engagements/${engId}/assistant`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ question, history: conversationHistory }),
        signal: controller.signal,
      });

      if (!res.ok || !res.body) throw new Error(`${res.status} ${res.statusText}`);

      const reader = res.body.getReader();
      const decoder = new TextDecoder();

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        const chunk = decoder.decode(value, { stream: true });
        const lines = chunk.split("\n");

        for (const line of lines) {
          if (!line.startsWith("data: ")) continue;
          const payload = line.slice(6).trim();
          if (!payload) continue;

          let msg: Record<string, string>;
          try { msg = JSON.parse(payload); } catch { continue; }

          if (msg.type === "tool") {
            toolUsed = msg.tool;
            setHistory(h => [...h.slice(0, -1), {
              question, answer: "", toolUsed, isLoading: true
            }]);
          } else if (msg.type === "token") {
            accumulated += msg.text;
            setHistory(h => [...h.slice(0, -1), {
              question, answer: accumulated, toolUsed, isLoading: true
            }]);
          } else if (msg.type === "done") {
            setHistory(h => [...h.slice(0, -1), {
              question, answer: accumulated, toolUsed, isLoading: false
            }]);
          } else if (msg.type === "error") {
            setHistory(h => [...h.slice(0, -1), {
              question, answer: `Error: ${msg.message}`, toolUsed: null, isLoading: false
            }]);
          }
        }
      }
    } catch (err: unknown) {
      if ((err as Error)?.name === "AbortError") return; // user cancelled
      const msg = err instanceof Error ? err.message : "Unknown error";
      setHistory(h => [...h.slice(0, -1), {
        question, answer: `Error: ${msg}`, toolUsed: null, isLoading: false
      }]);
    } finally {
      abortRef.current = null;
      setBusy(false);
    }
  }

  function handleKey(e: React.KeyboardEvent) {
    if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); ask(input); }
  }

  const selectedEng = engagements.find((e) => e.id === engId);

  return (
    <div className="assistant-page">
      <div className="assistant-header">
        <div>
          <h2>Migration Assistant</h2>
          <p className="muted">Read-only AI advisor · powered by local Ollama · never writes to tenants</p>
        </div>
        <div className="assistant-header-right">
          <select
            className="assistant-eng-select"
            value={engId}
            onChange={(e) => { setEngId(e.target.value); setHistory([]); }}
          >
            <option value="">— select engagement —</option>
            {engagements.map((e) => (
              <option key={e.id} value={e.id}>{e.name} · {e.customer_name}</option>
            ))}
          </select>
          {history.length > 0 && (
            <button className="btn-ghost" onClick={() => { setHistory([]); saveState({ engId, history: [] }); }}>
              Clear
            </button>
          )}
        </div>
      </div>

      {!engId ? (
        <div className="assistant-empty">
          <div className="assistant-empty-icon">🤖</div>
          <p>Select an engagement above to start asking questions about your migration.</p>
        </div>
      ) : (
        <>
          {history.length === 0 && (
            <div className="assistant-starters">
              <p className="muted">Try one of these for <strong>{selectedEng?.name}</strong>:</p>
              <div className="starter-grid">
                {STARTERS.map((s) => (
                  <button key={s} className="starter-btn" onClick={() => ask(s)} disabled={busy}>
                    {s}
                  </button>
                ))}
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
                <div className={`chat-bubble ai-bubble${turn.isLoading ? " loading" : ""}`}>
                  <span className="bubble-label">
                    Assistant
                    {turn.toolUsed && (
                      <span className="tool-tag">via {turn.toolUsed.replace(/_/g, " ")}</span>
                    )}
                    {turn.isLoading && <span className="streaming-dot" />}
                  </span>
                  {turn.answer ? (
                    <div className="ai-answer">
                      {turn.answer.split("\n").map((line, j) =>
                        line.trim() === "" ? <br key={j} /> : <p key={j}>{line}</p>
                      )}
                    </div>
                  ) : turn.isLoading ? (
                    <p className="thinking">
                      {turn.toolUsed ? "Analysing data…" : "Thinking…"}
                      <span className="thinking-note"> (CPU inference — tokens appear as they generate)</span>
                    </p>
                  ) : null}
                </div>
              </div>
            ))}
            <div ref={bottomRef} />
          </div>

          <div className="assistant-input-row">
            <textarea
              className="assistant-input"
              rows={2}
              placeholder="Ask about this migration…  (Enter to send, Shift+Enter for newline)"
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={handleKey}
              disabled={busy}
            />
            {busy ? (
              <button className="btn-danger assistant-send" onClick={abort}>
                ■ Stop
              </button>
            ) : (
              <button
                className="btn-primary assistant-send"
                onClick={() => ask(input)}
                disabled={!input.trim()}
              >
                Ask
              </button>
            )}
          </div>
        </>
      )}
    </div>
  );
}
