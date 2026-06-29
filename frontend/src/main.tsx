import React, { useState, useEffect } from "react";
import ReactDOM from "react-dom/client";
import { BrowserRouter, Routes, Route, NavLink } from "react-router-dom";
import { Dashboard } from "./components/Dashboard";
import { Engagements } from "./components/Engagements";
import { Connections } from "./components/Connections";
import { Migration } from "./components/Migration";
import { Logs } from "./components/Logs";
import { Readiness } from "./components/Readiness";
import { Assistant } from "./components/Assistant";
import { Login, AuthUser } from "./components/Login";
import { ChangePassword } from "./components/ChangePassword";
import { Users } from "./components/Users";
import "./index.css";

function Shell() {
  const [user, setUser]           = useState<AuthUser | null>(null);
  const [authChecked, setAuthChecked] = useState(false);

  // Check existing session cookie on mount
  useEffect(() => {
    fetch("/api/auth/me", { credentials: "include" })
      .then(r => r.ok ? r.json() : null)
      .then(u  => { setUser(u); setAuthChecked(true); })
      .catch(() => setAuthChecked(true));
  }, []);

  async function handleLogout() {
    await fetch("/api/auth/logout", { method: "POST", credentials: "include" });
    setUser(null);
  }

  // Loading splash
  if (!authChecked)
    return (
      <div style={{ display: "flex", height: "100vh", alignItems: "center", justifyContent: "center" }}>
        <p className="muted">Loading…</p>
      </div>
    );

  // Not logged in
  if (!user) return <Login onLogin={setUser} />;

  // Force password change
  if (user.forcePasswordChange)
    return <ChangePassword forced onChanged={() => setUser({ ...user, forcePasswordChange: false })} />;

  const isAdmin = user.role === "admin";

  const navLinks = [
    { to: "/",           label: "Overview",      end: true },
    { to: "/readiness",  label: "Readiness" },
    { to: "/engagements",label: "Engagements" },
    { to: "/inventory",  label: "Pre-migration" },
    { to: "/migrate",    label: "Migration" },
    { to: "/logs",       label: "Logs" },
    { to: "/assistant",  label: "Assistant" },
    ...(isAdmin ? [{ to: "/users", label: "Users" }] : []),
  ];

  return (
    <div className="app">
      <aside className="rail">
        <div className="brand">PAS<span>→</span>SS</div>
        <nav>
          {navLinks.map(p => (
            <NavLink key={p.to} to={p.to} end={(p as { end?: boolean }).end}
              className={({ isActive }) => isActive ? "active" : ""}>
              {p.label}
            </NavLink>
          ))}
        </nav>
        <div className="rail-foot">
          <div className="rail-user">
            <div className="rail-user-avatar">{(user.displayName || user.username).charAt(0).toUpperCase()}</div>
            <div className="rail-user-info">
              <span className="rail-user-name">{user.displayName || user.username}</span>
              <span className="rail-user-role">{user.role}</span>
            </div>
          </div>
          <button className="rail-signout" onClick={handleLogout} title="Sign out">
            ⏻
          </button>
        </div>
      </aside>
      <main className="content">
        <Routes>
          <Route path="/"            element={<Dashboard />} />
          <Route path="/readiness"   element={<Readiness />} />
          <Route path="/setup"       element={<Readiness />} />
          <Route path="/verify"      element={<Readiness />} />
          <Route path="/engagements" element={<Engagements />} />
          <Route path="/inventory"   element={<Connections />} />
          <Route path="/connections" element={<Connections />} />
          <Route path="/migrate"     element={<Migration />} />
          <Route path="/logs"        element={<Logs />} />
          <Route path="/assistant"   element={<Assistant />} />
          {isAdmin && <Route path="/users" element={<Users />} />}
          <Route path="*"            element={<Placeholder />} />
        </Routes>
      </main>
    </div>
  );
}

function Placeholder() {
  return (
    <div className="panel">
      <h2>Not built yet</h2>
      <p className="muted">This phase is scaffolded per the build sequence and comes online next.</p>
    </div>
  );
}

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode><BrowserRouter><Shell /></BrowserRouter></React.StrictMode>
);
