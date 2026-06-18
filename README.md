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

**Phases 1–2 are implemented and validated against a live PLC:**

- Connects to one or more Codesys OPC UA servers (one session per PLC, independent reconnect).
- Auto-discovers tags by browsing the address space (with continuation-point paging), filtered
  to the tags that matter (see [Tag discovery & filtering](#tag-discovery--filtering)).
- Subscribes with OPC UA report-by-exception (value-change notifications, not polling).
- Buffers readings in memory and writes them to SQLite (WAL mode) in batched transactions.
- Re-runs discovery on a schedule to pick up tags added/removed after a Codesys project update;
  tags that disappear are marked inactive, never deleted, so history is preserved.
- Runs as a **Windows Service** (auto-start, auto-restart on failure) for unattended operation,
  or as a console app for development — see [Run as a Windows Service](#run-as-a-windows-service).

Not yet built: CSV export, cloud upload, and the local web UI — see [Roadmap](#roadmap).

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
    ]
  }
}
```

| Setting | Meaning |
| --- | --- |
| `SiteName` | Label for this deployment. |
| `DatabasePath` | Where the SQLite database is written (created if missing). |
| `Discovery.RescanIntervalMinutes` | How often to re-browse each PLC for tag changes. |
| `Discovery.Filter` | Which discovered variables are logged — see below. |
| `Subscription.*` | Publishing/sampling intervals and per-item server queue depth. |
| `Plcs[]` | One entry per PLC: name, OPC UA endpoint, and security policy. |

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

### Inspecting the data

The database uses a long/narrow schema (one row per reading), so adding or removing tags never
requires a schema migration:

- `plcs` — configured PLCs
- `tags` — discovered tags (with `active` flag, `first_seen_at` / `last_seen_at`)
- `readings` — one row per value change (`ts_utc`, `value` / `value_text`, `quality`)
- `upload_log`, `settings` — reserved for later phases

Open `data/plcdata.db` with any SQLite tool, e.g.:

```sql
SELECT t.tag_name, r.ts_utc, r.value, r.value_text, r.quality
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

## Project layout

```text
Configuration/   Strongly-typed options bound from appsettings.json
OpcUa/           OPC UA application config, per-PLC session, discovery + tag filter, manager
Storage/         SQLite database, schema, in-memory buffer, batched writer
Program.cs       Generic Host wiring (Serilog + Windows Service + hosted services)
scripts/         Windows Service install / uninstall (PowerShell)
```

Built on the OPC Foundation reference stack (`OPCFoundation.NetStandard.Opc.Ua.Client`),
`Microsoft.Data.Sqlite`, and `Serilog`.

## Roadmap

| Phase | Scope | State |
| --- | --- | --- |
| 1 | Core logging: OPC UA discovery/subscription → SQLite | ✅ Done |
| 2 | Multi-PLC + Windows Service hosting with auto-restart recovery | ✅ Done |
| 3 | CSV export + pluggable cloud upload (Google Drive first), retention/pruning | Planned |
| 4 | Local web UI (status + configuration, incl. network scan for setup) | Planned |
| 5 | Multi-site hardening: config validation, packaging/install, field docs | Planned |
