# PLC Data Logger

A site-deployed service that reads tag data from one or more **Codesys V3 (Eaton) PLCs**
over **OPC UA**, persists it locally to **SQLite**, and syncs it to cloud storage as compressed,
time-partitioned **Parquet**. One reusable binary per site — behaviour is driven entirely by
configuration, not code.

The full design rationale lives in the architecture document; this README covers what is
currently built and how to run it.

## Design principles

- **Read-only by design** — the logger is a passive observer. The OPC UA session uses only
  read/subscribe service calls; there is no write code path.
- **Store-and-forward** — everything is written to durable local storage first. Cloud upload
  (when added) is a secondary, retryable step; the system is fully useful with no internet.
- **Configuration over code** — endpoints, schedule, tag selection, etc. live in per-site
  configuration (`appsettings.json` seeds plus the web-UI-managed `config.local.json`). The binary
  does not change between sites.

## Status

**Built and validated against a live PLC** (see [Roadmap](#roadmap) for the phase breakdown):

- Connects to one or more Codesys OPC UA servers (one session per PLC, independent reconnect).
- Auto-discovers tags by browsing the address space (with continuation-point paging), filtered
  to the tags that matter (see [Tag discovery & filtering](#tag-discovery--filtering)).
- Subscribes with OPC UA report-by-exception (value-change notifications, not polling).
- Buffers readings in memory and writes them to SQLite (WAL mode) in batched transactions.
- Re-runs discovery on a schedule to pick up tags added/removed after a Codesys project update;
  tags that disappear are marked inactive, never deleted, so history is preserved.
- Runs as a **Windows Service** (auto-start, auto-restart on failure) for unattended operation,
  or as a console app for development — see [Run as a Windows Service](#run-as-a-windows-service).
- Uploads **time-partitioned Parquet** (zstd) on a configurable schedule and prunes old data under a
  retention policy; CSV and full-database backups remain as on-demand options — see
  [Export, upload & retention](#export-upload--retention).
- Serves a **localhost-only web UI** (status dashboard + configuration) plus JSON endpoints. The
  config pages edit PLCs, the tag-logging selection, the schedule and upload settings **live,
  without a restart**, and a **network scan** discovers OPC UA servers on the LAN for setup — see
  [Web UI](#web-ui) and [Status dashboard & health](#status-dashboard--health).

Google Drive upload (incl. the OAuth consent flow) has been validated end-to-end. Not yet
end-to-end tested: certificate-trust acceptance for secured OPC UA policies (needs a secured
endpoint).

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
| `Storage.RetentionDays` | Seeds the retention window (runtime-editable on the Settings page). |
| `Export.DailyAtLocalTime` | Seeds the initial upload-schedule time, `HH:mm` (runtime-editable). |
| `Export.RunOnStartup` | Run one upload cycle shortly after startup (dev/verification). |
| `Upload.Provider` | `None` (default) or `GoogleDrive`. |
| `Upload.DestinationFolder` | Remote folder name files are uploaded into. |
| `WebUi.Port` | Localhost port for the status dashboard and `/api/health`. |

> **Static defaults vs. runtime config.** `appsettings.json` holds the static seed defaults. The
> **site name, upload schedule, partition size, retention window, PLC connections, upload settings**
> and **per-tag logging selection** are edited **live via the web UI** and persisted to
> `config.local.json` next to the executable — which then takes precedence.

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
> commissioning; explicit certificate-trust handling for secured endpoints is wired in but not yet
> end-to-end tested.

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

This comfortably covers the design target of ~50–150 change-events/sec (tens of GB per quarter). For
long-term/off-machine storage the scheduled upload already emits a compressed columnar format
(zstd **Parquet**, see [Export, upload & retention](#export-upload--retention)); if sustained
hundreds/sec ever pressured the local store, the next step would be monthly table partitioning (instant
drop instead of delete) behind the same `IReadingStore` seam, keeping the embedded, server-less model.

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
`plcs`, `tags` (incl. `active` and user-selected `enabled` flags), `readings` (`ts_utc` epoch-ms,
`value`/`value_text`, `quality` code), `export_state` (per-PLC CSV export state), `parquet_partitions`
(scheduled Parquet upload state + retention watermark), and `settings`. Open `data/plcdata.db` with any
SQLite tool — timestamps are epoch-ms, e.g.:

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

**Scheduled Parquet upload.** On the configured schedule the logger uploads **time-partitioned Parquet
files** (zstd, columnar) — one file per time window of the configured size, named
`{SiteName}-{yyyyMMddThhmm}Z.parquet` and covering all PLCs (`plc_name` is a column). Only fully-elapsed
windows are written; each is a small **incremental** file uploaded **once**, so per-cycle bandwidth
scales with _new_ data, not the whole retention window. Columnar + zstd is dramatically smaller than
raw SQLite or CSV (≈**28× smaller than CSV** on real data). Set `Export.RunOnStartup: true` to run a
cycle shortly after startup.

After a successful upload each partition file is **deleted locally** by default; enable _keep uploaded
partitions_ on Settings to retain them until they age past the retention window. Files not yet uploaded
(e.g. offline) stay on disk and retry next cycle. Partition state lives in the `parquet_partitions`
table (which also drives the retention watermark); local files live in `exports/partitions/`.

**Schedule.** Set on the **Settings** page — _every N minutes_, _daily_, _weekly_ (day + time), or
_monthly_ (day + time) — plus the **partition size** (hours; use multiples of 24 for days), which must
be **≤ the upload period** (e.g. daily upload ⇒ partition ≤ 24h). Changes take effect immediately (the
scheduler re-plans without a restart). `Export.DailyAtLocalTime` in appsettings seeds the initial value.

**CSV export (on demand).** CSV was found cumbersome for large sites, so it is no longer uploaded on
the schedule — but the option remains on the **Backup** page:

- **All tags** — regenerates one rolling `{SiteName}-{PlcName}.csv` per PLC from the retained readings
  (long format: `timestamp_utc, plc_name, tag_name, value, quality`) and uploads it, overwriting the
  same file. Per-PLC state lives in the `export_state` table.
- **Time window** — a one-off export of an arbitrary range to a uniquely-named
  `{SiteName}-{PlcName}-{start}_{end}.csv`, handy for pulling a specific incident window.

**Timestamped database archive (on demand).** The **Backup** page can also upload a
`{SiteName}-backup-{timestamp}.db` snapshot (`VACUUM INTO`) — a keep-forever full-database archive,
independent of the scheduled Parquet partitions.

**Cloud upload (pluggable).** Uploads go through the configured `ICloudUploadProvider`. Scheduled
Parquet partitions and one-off exports are each uploaded once; the rolling CSV and the DB backups
overwrite the same destination file. Upload runs entirely off the logging hot path: failures are
retried on the next cycle and never slow down or block recording.

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

- **Upload enabled** (a real provider) — only prune readings already **uploaded** (in a Parquet
  partition, or a manual DB backup — whichever reached furthest advances the pruning watermark); data
  not yet in the cloud is never dropped.
- **Upload disabled** (`None`) — prune purely by age. Very old data at offline sites is then only
  retrievable directly from the machine.

## Web UI

The logger hosts a small **Blazor Server** site, bound to **localhost only** (`WebUi.Port`,
default `5198`) — a convenience for whoever is physically at the machine, not a remotely accessible
service (§11). Browse to `http://localhost:5198/`.

| Page | Purpose |
| --- | --- |
| **Dashboard** | Live status, plus an **Upload partitions now** button to run the scheduled Parquet export/upload on demand. |
| **PLCs** | Add / edit / remove PLC connections. Changes apply **live** — sessions are added, removed, or reconnected without a service restart (§5). |
| **Scan** | Discover OPC UA servers on the local subnet (TCP 4840 sweep + FindServers enrichment) and add one as a PLC with one click. |
| **Tags** | Choose which discovered tags are logged, in a **subtree tree** (folders per `GLOBALES` / `fb_BREC1` …). Every tag is logged by default; deselecting one stops new readings for it (history is kept). Saving rebuilds the live subscription without a restart. |
| **Upload** | Choose the cloud provider, edit its settings (with a **Browse…** server-side file/folder picker for the credentials/token paths), test the connection, and run the Google OAuth consent. |
| **Backup** | On-demand: export **all tags** or a **time window** to CSV, or upload a timestamped **raw SQLite database backup** (`VACUUM INTO` snapshot). |
| **Settings** | Set the **site name**, the **upload schedule** (interval / daily / weekly / monthly), the **partition size**, whether to **keep** uploaded partitions, and the **retention window**. |

Editable configuration (PLC connections and upload settings) is stored in `config.local.json`
next to the executable — seeded from `appsettings.json` on first run, then owned by the UI.

The same operations are available as localhost JSON endpoints for scripted provisioning:

```text
GET    /api/health           overall health snapshot
GET    /api/scan             discover OPC UA servers on the LAN
GET    /api/plcs             list configured PLCs
POST   /api/plcs             add/replace a PLC  { name, endpointUrl, securityPolicy }
DELETE /api/plcs/{name}      remove a PLC
POST   /api/backup-now       run the scheduled Parquet partition export + upload immediately
POST   /api/export-now       run a CSV export + upload pass immediately (on-demand option)
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
Configuration/   Options, runtime-editable ConfigStore (config.local.json), validation
OpcUa/           OPC UA app config, per-PLC session, discovery + tag filter, manager, network scanner
Storage/         IReadingStore + optimized SQLite store, buffer, batched writer, CSV + Parquet export, retention
Upload/          Pluggable cloud upload (None + Google Drive, DPAPI token store, provider resolver)
Export/          Scheduled Parquet-partition upload + on-demand CSV/backup orchestration
Health/          Runtime health collector surfaced to the web UI
Components/      Blazor Server UI (Dashboard, PLCs, Scan, Tags, Upload, Backup, Settings)
Program.cs       Web host wiring (Serilog + Windows Service + hosted services + Blazor + APIs)
scripts/         Windows Service install / uninstall + distribution packaging (PowerShell)
```

Built on the OPC Foundation reference stack (`OPCFoundation.NetStandard.Opc.Ua.Client`),
`Microsoft.Data.Sqlite`, `Parquet.Net`, `Google.Apis.Drive.v3`, and `Serilog`.

## Roadmap

| Phase | Scope | State |
| --- | --- | --- |
| 1 | Core logging: OPC UA discovery/subscription → SQLite | ✅ Done |
| 2 | Multi-PLC + Windows Service hosting with auto-restart recovery | ✅ Done |
| 3 | Export + pluggable cloud upload (Google Drive), retention/pruning | ✅ Done |
| 4a | Observability: localhost status dashboard + `/api/health`, health monitor | ✅ Done |
| 4b | Config UI: edit PLCs (live hot-reload), network scan, upload settings + JSON APIs | ✅ Done |
| 4b* | Google Drive upload + OAuth consent (validated); cert-trust acceptance still untested | Mostly done |
| 5 | Multi-site hardening: config validation, packaging/install, field docs | ✅ Done |
| 6 | Data pipeline: per-tag logging selection, scheduled zstd-Parquet time-partition uploads (interval/daily/weekly/monthly), on-demand CSV/DB backups | ✅ Done |

For on-site install/configure/verify/update/troubleshoot steps, see
**[DEPLOYMENT.md](DEPLOYMENT.md)**.
