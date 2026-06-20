import type { InventoryItem, SnapshotSummary } from "./api";

/**
 * Client-side inventory exports, built from the captured snapshot (no backend change).
 * - CSV: full per-secret list (scales to 20k+ rows; it's just text).
 * - Summary report: one-page PDF via a print window, built from snapshot counts only.
 */

function csvCell(v: string | number | boolean | null): string {
  if (v === null || v === undefined) return "";
  const s = String(v);
  // Quote if it contains comma, quote, or newline; double internal quotes.
  if (/[",\n]/.test(s)) return `"${s.replace(/"/g, '""')}"`;
  return s;
}

export function downloadInventoryCsv(
  items: InventoryItem[],
  filenameBase: string,
): void {
  const header = ["Type", "Name", "Folder path", "Managed", "Size (bytes)", "Source ID"];
  const lines = [header.join(",")];
  for (const it of items) {
    lines.push([
      csvCell(it.item_type),
      csvCell(it.name),
      csvCell(it.folder_path ?? ""),
      csvCell(it.is_managed === null ? "" : it.is_managed ? "yes" : "no"),
      csvCell(it.size_bytes ?? ""),
      csvCell(it.source_native_id),
    ].join(","));
  }
  const blob = new Blob([lines.join("\n")], { type: "text/csv;charset=utf-8;" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = `${filenameBase}.csv`;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

/**
 * Open a print window with a one-page summary report (counts only). The user picks
 * "Save as PDF" in the print dialog. Self-contained HTML so it doesn't depend on app CSS.
 */
export function printInventorySummary(
  summary: SnapshotSummary,
  meta: { engagementName: string; customerName: string },
): void {
  const s = summary.summary;
  const captured = new Date(summary.captured_at).toLocaleString();
  const generated = new Date().toLocaleString();

  const row = (label: string, value: number | string) =>
    `<tr><td>${label}</td><td style="text-align:right;font-variant-numeric:tabular-nums">${value}</td></tr>`;

  const html = `<!doctype html><html><head><meta charset="utf-8">
<title>Inventory Summary — ${meta.customerName}</title>
<style>
  @page { margin: 16mm; }
  body { font-family: Arial, system-ui, sans-serif; color: #16112e; margin: 0; }
  .head { border-bottom: 3px solid #1f9d57; padding-bottom: 10px; margin-bottom: 18px; }
  .brand { color: #1f9d57; font-weight: 700; font-size: 13px; letter-spacing: .5px; }
  h1 { color: #2d2160; font-size: 24px; margin: 6px 0 2px; }
  .sub { color: #5b5478; font-size: 13px; margin: 0; }
  h2 { color: #2d2160; font-size: 15px; margin: 22px 0 8px; }
  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  td { padding: 7px 10px; border-bottom: 1px solid #e2dff0; }
  tr td:first-child { color: #3a3357; }
  .total td { font-weight: 700; border-top: 2px solid #2d2160; }
  .foot { margin-top: 28px; color: #8a83a8; font-size: 11px; }
</style></head><body>
  <div class="head">
    <div class="brand">DELINEA · PAS → SECRET SERVER MIGRATION</div>
    <h1>Vault Inventory Summary</h1>
    <p class="sub">${meta.engagementName} — ${meta.customerName} · Source snapshot captured ${captured}</p>
  </div>

  <h2>Secrets by type</h2>
  <table>
    ${row("Accounts", s.accounts)}
    ${row("Text secrets", s.text_secrets)}
    ${row("File secrets", s.file_secrets)}
    ${row("Folders", s.folders)}
    <tr class="total"><td>Total items</td><td style="text-align:right">${s.total}</td></tr>
  </table>

  <h2>Account management</h2>
  <table>
    ${row("Managed", s.managed)}
    ${row("Unmanaged", s.unmanaged)}
  </table>

  <p class="foot">Generated ${generated}. Counts are from the captured source inventory snapshot.
  Full per-secret detail is available in the companion CSV export.</p>

  <script>window.onload = function(){ window.print(); };</script>
</body></html>`;

  const w = window.open("", "_blank", "width=900,height=1100");
  if (!w) {
    alert("Pop-up blocked. Allow pop-ups for this site to export the summary report.");
    return;
  }
  w.document.open();
  w.document.write(html);
  w.document.close();
}
