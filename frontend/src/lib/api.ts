// Thin API client. Same-origin in prod (nginx proxies /api), proxied in dev.
export interface Engagement {
  id: string;
  name: string;
  customer_name: string;
  status: "planning" | "active" | "completed";
  created_at: string;
}

export interface TenantConnection {
  id: string;
  role: "source" | "target";
  system_type: "pas" | "secret_server";
  base_url: string | null;
  platform_tenant: string | null;
  auth_mode: "platform_client_credentials" | "legacy_password";
  credential_ref: string | null;
}

export interface TestConnectionResult {
  success: boolean;
  message: string;
}

// Sent to /api/connections/test. Credentials are used in-memory by the server and never stored.
export interface TestConnectionInput {
  systemType: "pas" | "secret_server";
  authMode: "platform_client_credentials" | "legacy_password";
  baseUrl?: string;
  platformBaseUrl?: string;
  secretServerBaseUrl?: string;
  appId?: string;
  clientId: string;
  clientSecret: string;
  username?: string;
  scope?: string;
  engagementId?: string;          // when set, a green test caches creds in the session vault
  role?: "source" | "target";
}

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    // Try to surface a structured error body if present.
    let detail = `${res.status} ${res.statusText}`;
    try {
      const body = await res.json();
      if (body?.message) detail = body.message;
    } catch { /* non-JSON error body */ }
    throw new Error(detail);
  }
  return res.json() as Promise<T>;
}

export const api = {
  ready: () => fetch("/health/ready").then((r) => r.ok),

  listEngagements: () => fetch("/api/engagements").then(json<Engagement[]>),
  createEngagement: (name: string, customerName: string) =>
    fetch("/api/engagements", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name, customerName }),
    }).then(json<{ id: string }>),

  listConnections: (engagementId: string) =>
    fetch(`/api/engagements/${engagementId}/connections`).then(json<TenantConnection[]>),

  saveConnection: (
    engagementId: string,
    body: {
      role: "source" | "target";
      systemType: "pas" | "secret_server";
      baseUrl?: string;
      platformTenant?: string;
      authMode: "platform_client_credentials" | "legacy_password";
    },
  ) =>
    fetch(`/api/engagements/${engagementId}/connections`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }).then(json<{ id: string }>),

  // Always resolves to a result object; rejects only on network failure.
  testConnection: async (input: TestConnectionInput): Promise<TestConnectionResult> => {
    const res = await fetch("/api/connections/test", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(input),
    });
    return res.json() as Promise<TestConnectionResult>;
  },

  // Run a full inventory capture for one tenant role (reuses session credentials).
  runInventory: (engagementId: string, input: TestConnectionInput & { role: "source" | "target" }) =>
    fetch(`/api/engagements/${engagementId}/inventory/run`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(input),
    }).then(json<InventoryRunResult>),

  reconcile: (engagementId: string) =>
    fetch(`/api/engagements/${engagementId}/reconcile`, { method: "POST" })
      .then(json<{ count: number }>),

  inventorySummary: (engagementId: string) =>
    fetch(`/api/engagements/${engagementId}/inventory/summary`).then(json<SnapshotSummary[]>),

  snapshotItems: (snapshotId: string, type?: string) =>
    fetch(`/api/snapshots/${snapshotId}/items${type ? `?type=${type}` : ""}`)
      .then(json<InventoryItem[]>),

  reconciliation: (engagementId: string) =>
    fetch(`/api/engagements/${engagementId}/reconciliation`).then(json<ReconRow[]>),

  metrics: (engagementId: string) =>
    fetch(`/api/engagements/${engagementId}/metrics`).then(json<Metrics>),

  sourceItems: (engagementId: string, type?: string, scope?: string) => {
    const params = new URLSearchParams();
    if (type) params.set("type", type);
    if (scope) params.set("scope", scope);
    const qs = params.toString();
    return fetch(`/api/engagements/${engagementId}/source-items${qs ? `?${qs}` : ""}`)
      .then(json<SourceItemRow[]>);
  },

  migrate: (engagementId: string, input: MigrateInput) =>
    fetch(`/api/engagements/${engagementId}/migrate`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(input),
    }).then(json<MigrationJobResult>),

  revert: (engagementId: string, connection: MigrateConnection) =>
    fetch(`/api/engagements/${engagementId}/revert`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ confirm: true, connection }),
    }).then(json<{ deleted: number; failed: number }>),

  migrationReport: (engagementId: string) =>
    fetch(`/api/engagements/${engagementId}/migration/report`).then(json<MigrationReport>),

  credentialStatus: (engagementId: string) =>
    fetch(`/api/engagements/${engagementId}/credentials/status`)
      .then(json<{ source: boolean; target: boolean }>),

  clearCredentials: (engagementId: string) =>
    fetch(`/api/engagements/${engagementId}/credentials/clear`, { method: "POST" })
      .then(json<{ cleared: boolean }>),

  logs: (engagementId: string, limit = 200) =>
    fetch(`/api/engagements/${engagementId}/logs?limit=${limit}`).then(json<LogRow[]>),

  runningJob: (engagementId: string) =>
    fetch(`/api/engagements/${engagementId}/running-job`)
      .then(json<{ running: boolean; job?: { id: string; job_type: string } }>),

  cancelJob: (jobId: string) =>
    fetch(`/api/jobs/${jobId}/cancel`, { method: "POST" })
      .then((r) => r.json() as Promise<{ cancelled?: boolean; message?: string }>),

  migrationStatus: (engagementId: string) =>
    fetch(`/api/engagements/${engagementId}/migration-status`)
      .then(json<MigrationStatus>),

  listTemplates: (conn: Record<string, unknown>) =>
    fetch(`/api/templates`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        systemType: "secret_server",
        authMode: "platform_client_credentials",
        baseUrl: conn.ssBaseUrl,
        platformBaseUrl: conn.ssPlatformBaseUrl,
        secretServerBaseUrl: conn.ssSecretServerBaseUrl,
        clientId: conn.ssClientId,
        clientSecret: conn.ssClientSecret,
      }),
    }).then(json<TemplateOption[]>),

  createFileTemplate: (conn: Record<string, unknown>, name: string) =>
    fetch(`/api/templates/create-file`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        connection: {
          systemType: "secret_server",
          authMode: "platform_client_credentials",
          baseUrl: conn.ssBaseUrl,
          platformBaseUrl: conn.ssPlatformBaseUrl,
          secretServerBaseUrl: conn.ssSecretServerBaseUrl,
          clientId: conn.ssClientId,
          clientSecret: conn.ssClientSecret,
        },
        name,
      }),
    }).then(json<TemplateOption>),
};

export interface TemplateOption { id: number; name: string; }

export interface MigrationStatusRow {
  item_type: string;
  total: number;
  migrated: number;
  failed: number;
  pending: number;
}
export interface MigrationStatusItem {
  item_type: string;
  name: string | null;
  source_native_id: string;
  target_native_id: string | null;
  status: string | null;
}
export interface MigrationStatus {
  summary: MigrationStatusRow[];
  items: MigrationStatusItem[];
}

export interface LogRow {
  occurred_at: string;
  event_type: string;
  action: string | null;
  outcome: string | null;
  message: string | null;
  tenant_role: string | null;
}

export interface SourceItemRow {
  item_type: "account" | "text_secret" | "file_secret" | "folder";
  source_native_id: string;
  name: string;
  folder_path: string | null;
  is_managed: boolean | null;
}

// Connection fields reused for migrate + revert (credentials session-only).
export interface MigrateConnection {
  pasBaseUrl?: string;
  pasAppId?: string;
  pasClientId: string;
  pasClientSecret: string;
  pasScope?: string;
  ssBaseUrl?: string;
  ssPlatformBaseUrl?: string;
  ssSecretServerBaseUrl?: string;
  ssClientId: string;
  ssClientSecret: string;
}

export interface MigrateInput extends MigrateConnection {
  jobType: "text_secret" | "file_secret" | "account_local" | "account_domain" | "full";
  dryRun: boolean;
  stagingFolderName?: string;
  selectedIds?: string[] | null;
  textTemplateId?: number;
  fileTemplateId?: number;
}

export interface MigrationJobResult {
  jobId: string;
  total: number;
  succeeded: number;
  failed: number;
  skipped: number;
  error: string | null;
  excluded: { sourceNativeId: string; name: string | null; reason: string; detail: string }[];
}

export interface MigrationReport {
  jobs: {
    id: string; job_type: string; mode: string; status: string;
    started_at: string; finished_at: string | null;
    total: number; succeeded: number; failed: number; skipped: number;
  }[];
  items: {
    item_type: string; source_name: string; source_folder_path: string | null;
    target_native_id: string | null; status: string; last_error: string | null;
  }[];
}

export interface InventoryRunResult {
  snapshotId: string;
  total: number;
  accounts: number;
  textSecrets: number;
  fileSecrets: number;
  folders: number;
}

export interface SnapshotSummary {
  role: "source" | "target";
  snapshot_id: string;
  captured_at: string;
  summary: {
    accounts: number;
    text_secrets: number;
    file_secrets: number;
    folders: number;
    managed: number;
    unmanaged: number;
    total: number;
  };
}

export interface InventoryItem {
  item_type: "account" | "text_secret" | "file_secret" | "folder";
  source_native_id: string;
  name: string;
  folder_path: string | null;
  is_managed: boolean | null;
  size_bytes: number | null;
}

export interface ReconRow {
  item_type: string;
  match_key: string;
  match_status: "source_only" | "target_only" | "matched" | "conflict";
}

export interface Metrics {
  accounts: { bucket: string; n: number }[];
  managed: { is_managed: boolean; n: number }[];
  progress: { type: string; total: number; migrated: number }[];
  sourceVsTarget: { type: string; source: number; target: number }[];
}
