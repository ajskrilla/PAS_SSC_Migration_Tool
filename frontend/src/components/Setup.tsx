import { useState } from "react";

/**
 * Prerequisites / Setup page.
 *
 * Interactive checklist (state only - no persistence, resets on reload) plus the
 * full procedures for OAuth2 app setup, service-account roles, and UVA mode.
 * The "Download checklist" button fetches the static Word doc shipped in /public.
 */

type Item = { id: string; label: string; detail: React.ReactNode };

const ITEMS: Item[] = [
  {
    id: "unified",
    label: "Customer tenant is Unified (Platform-enabled)",
    detail: (
      <>
        The PAS tenant must be migrated to the Delinea Platform identity model. Migration
        authenticates through the Platform, so a non-unified tenant cannot be used.
      </>
    ),
  },
  {
    id: "service-accounts",
    label: "API service account created in BOTH tenants",
    detail: (
      <>
        <strong>PAS:</strong> the service account must hold the{" "}
        <strong>System Administrator</strong> role.<br />
        <strong>Secret Server:</strong> the API account must hold the{" "}
        <strong>Secret Server Administrator</strong> role.
      </>
    ),
  },
  {
    id: "oauth2",
    label: "OAuth2 application created in PAS",
    detail: (
      <>
        See the OAuth2 App Setup steps below. Note the Application ID, the Client Credentials
        grant, and the scope &mdash; the tool needs all three.
      </>
    ),
  },
  {
    id: "uva",
    label: "Unlimited Vault Access (UVA) mode enabled in Secret Server",
    detail: (
      <>
        Required so the API account can read every secret for migration. See the UVA steps below.
      </>
    ),
  },
];

const OAUTH_STEPS: React.ReactNode[] = [
  <>Log in to your PAS tenant as a System Administrator.</>,
  <>Navigate to <strong>Apps &rarr; Add Web App &rarr; Web &mdash; Other</strong> type.</>,
  <>On the <strong>Settings</strong> tab, note the <strong>Application ID</strong> &mdash; this becomes the tool&rsquo;s <code>App ID</code> value.</>,
  <>On the <strong>Tokens</strong> tab, set the grant type to <strong>Client Credentials</strong>.</>,
  <>On the <strong>Scope</strong> tab, add a scope (for example, <code>all</code> with filter <code>.*</code>) &mdash; this becomes the tool&rsquo;s <code>Scope</code> value.</>,
  <>On the <strong>Permissions</strong> tab, grant the service account <strong>Run</strong> and <strong>View</strong> permissions on the application.</>,
  <>Confirm the application shows the <strong>Deployed</strong> status.</>,
];

export function Setup() {
  const [checked, setChecked] = useState<Record<string, boolean>>({});
  const toggle = (id: string) => setChecked((c) => ({ ...c, [id]: !c[id] }));
  const doneCount = ITEMS.filter((i) => checked[i.id]).length;
  const allDone = doneCount === ITEMS.length;

  return (
    <div>
      <header className="page-head">
        <h1>Setup &amp; prerequisites</h1>
        <a className="btn small ghost" href="/Migration-Prerequisites-Checklist.docx" download>
          Download checklist (Word)
        </a>
      </header>

      <div className="panel" style={{ marginBottom: 16 }}>
        <p className="muted" style={{ marginTop: 0 }}>
          Complete every item below before running the migration tool. These steps establish the
          API access and tenant configuration the tool relies on. Some items are typically performed
          by a tenant administrator &mdash; hand those to your Delinea admin if needed.
        </p>
        <div className="setup-progress">
          <div className="setup-progress-bar">
            <div className="setup-progress-fill"
              style={{ width: `${(doneCount / ITEMS.length) * 100}%` }} />
          </div>
          <span className={`muted ${allDone ? "setup-done" : ""}`}>
            {doneCount} / {ITEMS.length} complete{allDone ? " \u2713" : ""}
          </span>
        </div>
      </div>

      <section className="panel" style={{ marginBottom: 16 }}>
        <div className="phase-no">PREREQUISITES CHECKLIST</div>
        <div className="setup-list">
          {ITEMS.map((it) => (
            <label key={it.id} className={`setup-row ${checked[it.id] ? "checked" : ""}`}>
              <input type="checkbox" checked={!!checked[it.id]} onChange={() => toggle(it.id)} />
              <div>
                <div className="setup-label">{it.label}</div>
                <div className="setup-detail muted">{it.detail}</div>
              </div>
            </label>
          ))}
        </div>
      </section>

      <section className="panel" style={{ marginBottom: 16 }}>
        <div className="phase-no">OAUTH2 APP SETUP IN PAS</div>
        <h2 style={{ marginTop: 0 }}>Create the OAuth2 application</h2>
        <p className="muted">
          Perform these steps in the PAS admin portal while signed in as a System Administrator.
        </p>
        <ol className="setup-steps">
          {OAUTH_STEPS.map((s, i) => <li key={i}>{s}</li>)}
        </ol>
      </section>

      <div className="grid">
        <section className="panel">
          <div className="phase-no">SERVICE ACCOUNT ROLES</div>
          <h2 style={{ marginTop: 0 }}>PAS</h2>
          <ul className="setup-bullets muted">
            <li>
              The Client ID used to authenticate must be a member of the{" "}
              <strong>System Administrator (sysadmin)</strong> role.
            </li>
            <li>Add it under <strong>Roles &rarr; sysadmin &rarr; Members</strong> in the PAS admin portal.</li>
          </ul>
          <h2>Secret Server</h2>
          <ul className="setup-bullets muted">
            <li>
              The API account must hold the <strong>Secret Server Administrator</strong> role so it
              can create folders, templates, and secrets during migration.
            </li>
          </ul>
        </section>

        <section className="panel">
          <div className="phase-no">UNLIMITED VAULT ACCESS</div>
          <h2 style={{ marginTop: 0 }}>Enable UVA mode</h2>
          <p className="muted">
            Unlimited Vault Access (UVA) mode lets the API account read every secret in the vault,
            which the migration requires to copy secrets the account does not individually own.
          </p>
          <ul className="setup-bullets muted">
            <li>
              Follow the official Delinea instructions:{" "}
              <a href="https://docs.delinea.com/online-help/secret-server/admin/uva-mode/index.htm#UnlimitedVaultAccess"
                target="_blank" rel="noreferrer">Unlimited Vault Access Mode &mdash; Delinea docs</a>.
            </li>
          </ul>
          <p className="muted" style={{ fontSize: 12 }}>
            <strong>Note:</strong> UVA is a powerful access mode. Enable it for the migration window
            and review your organization&rsquo;s policy on when to disable it afterward.
          </p>
        </section>
      </div>
    </div>
  );
}
