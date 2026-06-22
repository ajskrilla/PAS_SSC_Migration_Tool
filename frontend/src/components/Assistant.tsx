import { useState, useRef, useEffect } from "react";
import { api, Engagement } from "../lib/api";

interface Turn {
  question: string;
  answer: string;
  toolUsed: string | null;
  isLoading?: boolean;
}

const STARTERS = [
  "Please verify that my API accounts have the necessary permissions and that unlimited vault access is on",
  "What percentage of my vault has migrated, how many secrets, and what days did we migrate data?",
  "Take an overall look at my environment and suggest how I should approach the migration",
  "Recent activity — what has been happening in this engagement?",
  "Help",
];

export function Assistant() {
  const [engagements, setEngagements] = useState<Engagement[]>([]);
  const [engId, setEngId] = useState<string>("");
  const [history, setHistory] = useState<Turn[]>([]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    api.listEngagements().then((e) => {
      setEngagements(e);
      if (e.length === 1) setEngId(e[0].id);
    }).catch(() => {});
  }, []);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [history]);

  async function ask(question: string) {
    if (!engId || !question.trim() || busy) return;

    const userTurn: Turn = { question, answer: "", toolUsed: null, isLoading: true };
    setHistory((h) => [...h, userTurn]);
    setInput("");
    setBusy(true);

    try {
      const conversationHistory = history.map((t) => ({
        question: t.question,
        answer: t.answer,
      }));

      const reply = await api.assistantAsk(engId, question, conversationHistory);
      setHistory((h) => [
        ...h.slice(0, -1),
        { question, answer: reply.answer, toolUsed: reply.toolUsed, isLoading: false },
      ]);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Unknown error";
      setHistory((h) => [
        ...h.slice(0, -1),
        { question, answer: `Error: ${msg}`, toolUsed: null, isLoading: false },
      ]);
    } finally {
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
              <p className="muted">Try one of these questions for <strong>{selectedEng?.name}</strong>:</p>
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
                  </span>
                  {turn.isLoading ? (
                    <p className="thinking">Thinking… <span className="thinking-note">(local model on CPU, may take 20–40 s)</span></p>
                  ) : (
                    <div className="ai-answer">
                      {turn.answer.split("\n").map((line, j) => (
                        line.trim() === "" ? <br key={j} /> : <p key={j}>{line}</p>
                      ))}
                    </div>
                  )}
                </div>
              </div>
            ))}
            <div ref={bottomRef} />
          </div>

          <div className="assistant-input-row">
            <textarea
              className="assistant-input"
              rows={2}
              placeholder="Ask about this migration…"
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={handleKey}
              disabled={busy}
            />
            <button
              className="btn-primary assistant-send"
              onClick={() => ask(input)}
              disabled={busy || !input.trim()}
            >
              {busy ? "…" : "Ask"}
            </button>
          </div>
        </>
      )}
    </div>
  );
}
