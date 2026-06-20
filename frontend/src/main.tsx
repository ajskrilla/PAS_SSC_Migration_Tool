import React from "react";
import ReactDOM from "react-dom/client";
import { BrowserRouter, Routes, Route, NavLink } from "react-router-dom";
import { Dashboard } from "./components/Dashboard";
import { Engagements } from "./components/Engagements";
import { Connections } from "./components/Connections";
import { Migration } from "./components/Migration";
import { Logs } from "./components/Logs";
import { Readiness } from "./components/Readiness";
import "./index.css";

function Shell() {
  const phases = [
    { to: "/", label: "Overview", end: true },
    { to: "/readiness", label: "Readiness" },
    { to: "/engagements", label: "Engagements" },
    { to: "/inventory", label: "Pre-migration" },
    { to: "/migrate", label: "Migration" },
    { to: "/logs", label: "Logs" },
    { to: "/assistant", label: "Assistant" },
  ];
  return (
    <div className="app">
      <aside className="rail">
        <div className="brand">PAS<span>→</span>SS</div>
        <nav>
          {phases.map((p) => (
            <NavLink key={p.to} to={p.to} end={p.end}
              className={({ isActive }) => (isActive ? "active" : "")}>
              {p.label}
            </NavLink>
          ))}
        </nav>
        <div className="rail-foot">in-tenant · read-only AI</div>
      </aside>
      <main className="content">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/readiness" element={<Readiness />} />
          <Route path="/setup" element={<Readiness />} />
          <Route path="/verify" element={<Readiness />} />
          <Route path="/engagements" element={<Engagements />} />
          <Route path="/inventory" element={<Connections />} />
          <Route path="/connections" element={<Connections />} />
          <Route path="/migrate" element={<Migration />} />
          <Route path="/logs" element={<Logs />} />
          <Route path="*" element={<Placeholder />} />
        </Routes>
      </main>
    </div>
  );
}

function Placeholder() {
  return <div className="panel"><h2>Not built yet</h2>
    <p className="muted">This phase is scaffolded per the build sequence and comes online next.</p></div>;
}

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode><BrowserRouter><Shell /></BrowserRouter></React.StrictMode>
);
