import { useState } from "react";

interface ChangePasswordProps {
  onChanged: () => void;
  forced?: boolean;
}

export function ChangePassword({ onChanged, forced = false }: ChangePasswordProps) {
  const [current,  setCurrent]  = useState("");
  const [next,     setNext]     = useState("");
  const [confirm,  setConfirm]  = useState("");
  const [error,    setError]    = useState<string | null>(null);
  const [loading,  setLoading]  = useState(false);

  const reqs = [
    { label: "At least 10 characters", ok: next.length >= 10 },
    { label: "Uppercase letter",        ok: /[A-Z]/.test(next) },
    { label: "Lowercase letter",        ok: /[a-z]/.test(next) },
    { label: "Number",                  ok: /\d/.test(next) },
    { label: "Special character",       ok: /[^A-Za-z0-9]/.test(next) },
    { label: "Passwords match",         ok: next === confirm && next.length > 0 },
  ];
  const allOk = reqs.every(r => r.ok);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!allOk) return;
    setError(null);
    setLoading(true);
    try {
      const res = await fetch("/api/auth/change-password", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        credentials: "include",
        body: JSON.stringify({ currentPassword: current, newPassword: next }),
      });
      const data = await res.json();
      if (!res.ok) {
        setError(data.error ?? "Failed to change password.");
      } else {
        onChanged();
      }
    } catch {
      setError("Network error.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className={forced ? "modal-overlay" : ""}>
      <div className={forced ? "modal-card" : ""}>
        {forced && (
          <div className="modal-header warn-text">
            ⚠ You must change your password before continuing
          </div>
        )}
        <h3 style={{ marginBottom: "1rem" }}>{forced ? "Set new password" : "Change password"}</h3>

        <form onSubmit={handleSubmit} className="login-form">
          <div className="form-group">
            <label>Current password</label>
            <input type="password" value={current} onChange={e => setCurrent(e.target.value)}
              autoComplete="current-password" required disabled={loading} />
          </div>
          <div className="form-group">
            <label>New password</label>
            <input type="password" value={next} onChange={e => setNext(e.target.value)}
              autoComplete="new-password" required disabled={loading} />
          </div>
          <div className="form-group">
            <label>Confirm new password</label>
            <input type="password" value={confirm} onChange={e => setConfirm(e.target.value)}
              autoComplete="new-password" required disabled={loading} />
          </div>

          <div className="pwd-reqs">
            {reqs.map((r, i) => (
              <div key={i} className={"pwd-req " + (r.ok ? "ok" : "")}>
                <span>{r.ok ? "✓" : "○"}</span> {r.label}
              </div>
            ))}
          </div>

          {error && <div className="login-error">{error}</div>}

          <button type="submit" className="btn-primary login-btn"
            disabled={loading || !allOk}>
            {loading ? "Saving…" : "Change password"}
          </button>
        </form>
      </div>
    </div>
  );
}
