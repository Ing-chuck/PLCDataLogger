# PLC Data Logger

A site-deployed service that reads tag data from one or more **Codesys V3 (Eaton) PLCs**
over **OPC UA**, persists it locally to **SQLite**, and (later) syncs it to cloud storage.
One reusable binary per site — behaviour is driven entirely by configuration, not code.

The full design rationale lives in the architecture document; this README covers what is
currently built and how to run it.

## Design principles

- **Read-only by design** — the logger is a passive observer. The OPC UA session uses only
  read/subscribe service calls; there is no write code path.
- **Store-and-forward** — everything is written to durable local storage first. Cloud upload
  (when added) is a secondary, retryable step; the system is fully useful with no internet.
- **Configuration over code** — endpoints, tag filters, sample rates, etc. live in
  `appsettings.json` per deployment. The binary does not change between sites.

## Status

**Phases 1–4 (status/observability) are implemented and validated against a live PLC:**

- Connects to one or more Codesys OPC UA servers (one session per PLC, independent reconnect).
- Auto-discovers tags by browsing the address space (with continuation-point paging), filtered
  to the tags that matter (see [Tag discovery & filtering](#tag-discovery--filtering)).
- Subscribes with OPC UA report-by-exception (value-change notifications, not polling).
- Buffers readings in memory and writes them to SQLite (WAL mode) in batched transactions.
- Re-runs discovery on a schedule to pick up tags added/removed after a Codesys project update;
  tags that disappear are marked inactive, never deleted, so history is preserved.
- Runs as a **Windows Service** (auto-start, auto-restart on failure) for unattended operation,
  or as a console app for development — see [Run as a Windows Service](#run-as-a-windows-service).
- Exports readings to **CSV** on a daily schedule and optionally uploads them via a pluggable
  cloud provider, then prunes old data under a retention policy — see
  [Export, upload & retention](#export-upload--retention).
- Serves a **localhost-only web UI** (status dashboard + configuration) plus JSON endpoints. The
  config pages edit PLC connections and upload settings **live, without a restart**, and a
  **network scan** discovers OPC UA servers on the LAN for setup — see
  [Web UI](#web-ui) and [Status dashboard & health](#status-dashboard--health).

Google Drive upload (incl. the OAuth consent flow) has been validated end-to-end. Not yet
end-to-end tested: certificate-trust acceptance for secured OPC UA policies (needs a secured
endpoint). Remaining work: multi-site hardening (Phase 5) — see [Roadmap](#roadmap).

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Network access to the PLC's OPC UA endpoint (default port `4840`)

## Configuration

All settings live under the `Logger` section of [`appsettings.json`](appsettings.json):

```json
{
  "Logger": {
    "SiteName": "Dev",
    "DatabasePath": "data/plcdata.db",
    "Discovery": {
      "RescanIntervalMinutes": 1440,
      "Filter": {
        "ExcludeNamespaceZero": true,
        "IncludeNamespaceIndices": [ 4 ],
        "NodeIdMustContain": [ "|var|", ".Application." ]
      }
    },
    "Subscription": {
      "PublishingIntervalMs": 1000,
      "SamplingIntervalMs": 1000,
      "QueueSize": 10
    },
    "Plcs": [
      {
        "Name": "PLC1",
        "EndpointUrl": "opc.tcp://192.168.162.110:4840",
        "SecurityPolicy": "None"
      }
    ],
    "Storage": {
      "RetentionDays": 90,
      "RetentionCheckIntervalMinutes": 60
    },
    "Export": {
      "DirectoryPath": "exports",
      "DailyAtLocalTime": "02:00",
      "RunOnStartup": false
    },
    "Upload": {
      "Provider": "None",
      "DestinationFolder": "PLCDataLogger",
      "GoogleDrive": {
        "CredentialsPath": "google_client.json",
        "TokenStorePath": "google_token"
      }
    },
    "WebUi": {
      "Port": 5198
    }
  }
}
```

| Setting | Meaning |
| --- | --- |
| `SiteName` | Label for this deployment (also used in export file names). |
| `DatabasePath` | Where the SQLite database is written (created if missing). |
| `Discovery.RescanIntervalMinutes` | How often to re-browse each PLC for tag changes. |
| `Discovery.Filter` | Which discovered variables are logged — see below. |
| `Subscription.*` | Publishing/sampling intervals and per-item server queue depth. |
| `Plcs[]` | One entry per PLC: name, OPC UA endpoint, and security policy. |
| `Storage.RetentionDays` | Prune readings older than this (0 = keep everything). |
| `Export.DailyAtLocalTime` | Local time of day to run the CSV export, `HH:mm`. |
| `Export.RunOnStartup` | Run one export shortly after startup (dev/verification). |
| `Upload.Provider` | `None` (default) or `GoogleDrive`. |
| `Upload.DestinationFolder` | Remote folder name files are uploaded into. |
| `WebUi.Port` | Localhost port for the status dashboard and `/api/health`. |

### Tag discovery & filtering

Discovery browses each PLC's address space and keeps only `Variable` nodes that pass
`Discovery.Filter`:

- `ExcludeNamespaceZero` — drops namespace 0 (standard OPC UA server/diagnostics nodes).
- `IncludeNamespaceIndices` — restricts to these namespaces (empty = any).
- `NodeIdMustContain` — keeps only node-ids containing **all** of these substrings.

The defaults target the known Codesys application-variable pattern for these PLCs:

```text
ns=4;s=|var|{PLC_name}.Application.*
```

This excludes static `|vprop|` device-identity properties (Manufacturer, Model, serial,
revisions) and the noisy `Server` diagnostics subtree. Adjust per site as new patterns emerge —
no code change required.

> **Security note:** `SecurityPolicy: "None"` is for commissioning on a trusted/isolated
> network. Production deployments should use a real policy (e.g. `Basic256Sha256`) with explicit
> certificate trust. The client currently auto-accepts untrusted server certificates for
> commissioning; this will be replaced with explicit trust handling when the config UI is built.

## Running

```bash
dotnet run
```

The service connects, discovers and subscribes, and begins writing to the configured database.
Application logs are written to the console and to rolling files under `logs/`.

### Storage engine

Local storage is **SQLite** (WAL mode) behind an `IReadingStore` abstraction, so the rest of the
system never depends on storage internals and the engine can evolve independently. SQLite is the
right fit for the edge "install-and-forget, no DBA, works offline, single self-contained exe" model.

The high-volume `readings` table is tuned for time-series throughput and footprint:

- Timestamps stored as **INTEGER epoch-milliseconds** (not ISO text) — roughly halves row + index
  size and speeds range scans/prunes. Quality is a small **INTEGER code**.
- **Retention** prunes in bounded batches and reclaims space via `auto_vacuum=INCREMENTAL`, avoiding
  a full-file `VACUUM` lock.

This comfortably covers the design target of ~50–150 change-events/sec (tens of GB per quarter). At
sustained hundreds/sec, the next steps would be monthly partitioning (instant drop instead of
delete) and, if needed, a compressed columnar long-term store (DuckDB/Parquet) — all behind the same
`IReadingStore` seam, keeping the embedded, server-less deployment model.

### Deadbands (volume reduction)

The highest-leverage way to control volume is a **deadband** — a numeric tag only records a new
reading when its value moves by at least `Subscription.DefaultDeadband` from the last stored value
(a per-tag `deadband_override` takes precedence; `0` disables). Booleans/strings and non-Good
quality always pass through, so transitions and quality events are never lost.

The deadband is applied **client-side** (in the logger), not via an OPC UA server-side
`DataChangeFilter`: Codesys's server only accepts server-side deadband on `AnalogItem` nodes and
rejects it on plain IEC numeric variables. Client-side works on any server and directly reduces
stored volume — exactly the bottleneck — at the cost of unchanged PLC→logger network traffic (which
is trivial on a LAN).

### Inspecting the data

Long/narrow schema (one row per reading), so adding/removing tags never needs a migration:
`plcs`, `tags`, `readings` (`ts_utc` epoch-ms, `value`/`value_text`, `quality` code), `upload_log`,
`settings`. Open `data/plcdata.db` with any SQLite tool — timestamps are epoch-ms, e.g.:

```sql
SELECT t.tag_name, datetime(r.ts_utc/1000, 'unixepoch') AS ts, r.value, r.quality
FROM readings r JOIN tags t ON t.tag_id = r.tag_id
ORDER BY r.id DESC LIMIT 20;
```

## Run as a Windows Service

For unattended site deployment the logger installs as a Windows Service that auto-starts on boot
and auto-restarts on failure. The same binary runs as a console app under `dotnet run`; it detects
the service control manager automatically.

The data, log, certificate (`pki/`), and `appsettings.json` paths all resolve next to the
executable, so the service is self-contained in its install directory regardless of the
account it runs under.

Install scripts live in [`scripts/`](scripts/) and must be run from an **elevated** PowerShell
prompt. To publish a fresh self-contained build and register the service in one step:

```powershell
.\scripts\install-service.ps1 -Publish
```

This publishes a single-file, self-contained build (no .NET runtime required on the target PC)
to `C:\PLCDataLogger`, then registers a service named `PLCDataLogger` with:

- **Start type:** Automatic (starts on boot)
- **Recovery:** restart after 5 s, then 10 s, then every 30 s; failure count resets daily

Edit `C:\PLCDataLogger\appsettings.json` for the site, then restart the service
(`Restart-Service PLCDataLogger`). Logs are written under `C:\PLCDataLogger\logs`.

Common parameters (see the script header for all of them):

| Parameter | Default | Purpose |
| --- | --- | --- |
| `-InstallDir` | `C:\PLCDataLogger` | Where the binaries and data live. |
| `-Publish` | _(off)_ | Publish a fresh build before installing. |
| `-ServiceName` | `PLCDataLogger` | Windows Service name. |

To remove the service (leaving the install directory and data in place):

```powershell
.\scripts\uninstall-service.ps1
```

## Export, upload & retention

**CSV export.** On the configured schedule the logger regenerates **one rolling CSV per PLC** in
`Export.DirectoryPath`, named `{SiteName}-{PlcName}.csv` (site name is set on the Settings page). Each
file is overwritten from the readings still in the local store — a long-format snapshot, one row per
reading: `timestamp_utc, plc_name, tag_name, value, quality`. Because the file mirrors the retained
window, rows aged out by retention drop off it too. Per-PLC state (last exported/uploaded reading id)
lives in the `export_state` table. The export always runs, even with no internet, because the files
are useful for manual pickup (USB/RDP). Set `Export.RunOnStartup: true` to verify it without waiting
for the scheduled time.

**Schedule.** The export+upload cadence is set on the **Settings** page — either _every N minutes_ or
_daily at a fixed local time_. Changes take effect immediately (the scheduler re-plans without a
restart). `Export.DailyAtLocalTime` in appsettings seeds the initial value.


**One-off windowed export.** The **Backup** page can export an arbitrary time range to its own
uniquely-named file per PLC (`{SiteName}-{PlcName}-{start}_{end}.csv`) and upload it, independent of
the rolling daily files — handy for pulling a specific incident window for analysis.

**Raw database backup.** The **Backup** page can also upload a consistent single-file snapshot of the
whole SQLite database (via `VACUUM INTO`, safe while logging continues) as a timestamped
`{SiteName}-backup-{timestamp}.db` — no CSV conversion, so it can be opened directly. Each backup is a
new file, keeping a history in the cloud.

**Cloud upload (pluggable).** After exporting, each PLC file whose contents changed since its last
successful upload is re-sent via the configured `ICloudUploadProvider`, **overwriting the same
destination file** rather than accumulating dated copies. Upload runs entirely off the logging hot
path: failures are retried on the next cycle and never slow down or block recording.

- **`None`** (default) — does nothing and is a fully supported permanent state for offline sites.
- **`GoogleDrive`** — uploads to a Drive folder. Authentication uses OAuth with the refresh token
  cached encrypted at rest via Windows DPAPI. The unattended service only refreshes a previously
  stored token; the one-time interactive consent is performed during setup.

  > **Setup:** create an OAuth client (**Desktop app** type) in Google Cloud, enable the Drive
  > API, and **publish** the consent screen so the refresh token doesn't expire. Save the client
  > JSON, point the **Upload** page at it, then click **Connect Google…** once (opens a browser).
  > Because consent needs a browser, do it while running interactively — a LocalSystem service has
  > no desktop; the DPAPI (LocalMachine) token it stores is then readable by the service.

**Retention.** A periodic sweep (`Storage.RetentionCheckIntervalMinutes`) prunes readings older than
the retention window (set on the **Settings** page; `Storage.RetentionDays` seeds it), in one of two
modes:

- **Upload enabled** (a real provider) — only prune old readings that have been _confirmed
  uploaded_; un-uploaded data is never dropped.
- **Upload disabled** (`None`) — prune purely by age. Very old data at offline sites is then only
  retrievable directly from the machine.

## Web UI

The logger hosts a small **Blazor Server** site, bound to **localhost only** (`WebUi.Port`,
default `5198`) — a convenience for whoever is physically at the machine, not a remotely accessible
service (§11). Browse to `http://localhost:5198/`.

| Page | Purpose |
| --- | --- |
| **Dashboard** | Live status, plus an **Export & upload now** button to run the export/upload pass on demand. |
| **PLCs** | Add / edit / remove PLC connections. Changes apply **live** — sessions are added, removed, or reconnected without a service restart (§5). |
| **Scan** | Discover OPC UA servers on the local subnet (TCP 4840 sweep + FindServers enrichment) and add one as a PLC with one click. |
| **Upload** | Choose the cloud provider, edit its settings (with a **Browse…** server-side file/folder picker for the credentials/token paths), test the connection, and run the Google OAuth consent. |
| **Backup** | Export an arbitrary **time window** to a one-off per-PLC CSV, or upload a **raw SQLite database backup** (`VACUUM INTO` snapshot) — both on demand. |
| **Settings** | Set the **site name** (labels the dashboard and export files), the **upload schedule** (interval or daily time), and the **retention window** (days of readings to keep). |

Editable configuration (PLC connections and upload settings) is stored in `config.local.json`
next to the executable — seeded from `appsettings.json` on first run, then owned by the UI.

The same operations are available as localhost JSON endpoints for scripted provisioning:

```text
GET    /api/health           overall health snapshot
GET    /api/scan             discover OPC UA servers on the LAN
GET    /api/plcs             list configured PLCs
POST   /api/plcs             add/replace a PLC  { name, endpointUrl, securityPolicy }
DELETE /api/plcs/{name}      remove a PLC
POST   /api/export-now       run an export + upload pass immediately
```

> Google Drive upload and its OAuth consent flow are validated end-to-end. Certificate-trust
> acceptance for secured OPC UA policies is wired in but **not yet end-to-end tested** (needs a
> secured endpoint).

## Status dashboard & health

The **Dashboard** page shows, refreshing live: per-PLC connection state, discovered/monitored tag
counts, last sample time, total readings written, buffer depth, the active upload provider, last
export/upload times, and free disk space. The same data is at `GET /api/health`.

The health view distinguishes "upload not configured" (neutral — expected for offline sites) from a
configured provider, so a genuinely failing upload stands out (§9).

## Project layout

```text
Configuration/   Options, runtime-editable ConfigStore (config.local.json)
OpcUa/           OPC UA app config, per-PLC session, discovery + tag filter, manager, network scanner
Storage/         IReadingStore + optimized SQLite store, buffer, batched writer, CSV export, retention
Upload/          Pluggable cloud upload (None + Google Drive, DPAPI token store, provider resolver)
Export/          Scheduled export + upload orchestration
Health/          Runtime health collector surfaced to the web UI
Components/      Blazor Server UI (layout/nav, Dashboard, PLCs, Scan, Upload)
Program.cs       Web host wiring (Serilog + Windows Service + hosted services + Blazor + APIs)
scripts/         Windows Service install / uninstall (PowerShell)
```

Built on the OPC Foundation reference stack (`OPCFoundation.NetStandard.Opc.Ua.Client`),
`Microsoft.Data.Sqlite`, `Google.Apis.Drive.v3`, and `Serilog`.

## Roadmap

| Phase | Scope | State |
| --- | --- | --- |
| 1 | Core logging: OPC UA discovery/subscription → SQLite | ✅ Done |
| 2 | Multi-PLC + Windows Service hosting with auto-restart recovery | ✅ Done |
| 3 | CSV export + pluggable cloud upload (Google Drive), retention/pruning | ✅ Done |
| 4a | Observability: localhost status dashboard + `/api/health`, health monitor | ✅ Done |
| 4b | Config UI: edit PLCs (live hot-reload), network scan, upload settings + JSON APIs | ✅ Done |
| 4b* | Google Drive upload + OAuth consent (validated); cert-trust acceptance still untested | Mostly done |
| 5 | Multi-site hardening: config validation, packaging/install, field docs | ✅ Done |

For on-site install/configure/verify/update/troubleshoot steps, see
**[DEPLOYMENT.md](DEPLOYMENT.md)**.
