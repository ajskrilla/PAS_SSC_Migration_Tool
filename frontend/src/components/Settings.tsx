import { useEffect, useState } from "react";
import { api } from "../lib/api";

export function Settings() {
  const [hours, setHours] = useState<number | "">("");
  const [bounds, setBounds] = useState<{ min: number; max: number } | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.getSettings()
      .then((s) => {
        setHours(s.sessionTimeoutHours);
        setBounds({ min: s.minSessionTimeoutHours, max: s.maxSessionTimeoutHours });
      })
      .catch((e) => setError(String(e)))
      .finally(() => setLoading(false));
  }, []);

  async function save() {
    if (hours === "") return;
    setSaving(true); setMsg(null); setError(null);
    try {
      const res = await api.updateSettings(hours);
      setMsg(res.message);
    } catch (e) {
      setError(String(e));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div>
      <header className="page-head"><h1>Settings</h1></header>

      <div className="panel" style={{ maxWidth: 480 }}>
        <h2 style={{ marginTop: 0 }}>Login session length</h2>
        <p className="muted note" style={{ marginTop: 0 }}>
          How long a user stays logged in after entering their password, before they need to sign
          in again. Applies to logins from this point forward — anyone already signed in keeps
          the session length they were issued, so shortening this won't immediately log anyone out.
        </p>

        {loading ? (
          <p className="muted">Loading…</p>
        ) : (
          <>
            <label className="field" style={{ maxWidth: 220 }}>
              <span>Session timeout (hours)</span>
              <input
                type="number"
                value={hours}
                min={bounds?.min ?? 1}
                max={bounds?.max ?? 168}
                onChange={(e) => setHours(e.target.value === "" ? "" : Number(e.target.value))}
              />
            </label>
            {bounds && (
              <p className="muted note">
                Allowed range: {bounds.min}–{bounds.max} hours ({Math.round(bounds.max / 24)} days max).
              </p>
            )}

            <div className="conn-actions" style={{ marginTop: 4 }}>
              <button className="btn" onClick={save} disabled={saving || hours === ""}>
                {saving ? "Saving…" : "Save"}
              </button>
            </div>

            {msg && <div className="result ok">{msg}</div>}
            {error && <div className="result bad">{error}</div>}
          </>
        )}
      </div>
    </div>
  );
}
