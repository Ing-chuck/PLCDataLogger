# PLC Data Logger — Field Deployment Guide

This is the on-site runbook for installing, configuring, verifying, updating, and troubleshooting
the logger. It assumes the packaged distribution zip (`PLCDataLogger-v<version>-win-x64.zip`). For
architecture and feature detail, see [README.md](README.md).

## 1. Prerequisites

- **Target PC:** Windows 10/11 or Windows Server, x64. **No .NET install required** — the build is
  self-contained.
- **Access:** an account with **local administrator** rights (to register the Windows Service).
- **Network:** the PC must reach each PLC's OPC UA endpoint on the LAN.

## 2. Network requirements (share with site IT)

| Direction | From → To | Port | Purpose |
| --- | --- | --- | --- |
| Outbound (LAN) | Logger PC → each PLC | TCP **4840** (default) | OPC UA data collection |
| Outbound (Internet) | Logger PC → `*.googleapis.com`, `accounts.google.com` | TCP **443** | Google Drive upload *(only if enabled)* |
| Local only | Operator browser → Logger PC | TCP **5198** (default, **localhost only**) | Status/config web UI |

If there is **no internet**, that's fully supported — leave the upload provider as `None`; CSV
exports are still produced locally for manual pickup.

## 3. Install

1. Copy the zip to the target PC and unzip it (e.g. to `C:\PLCDataLogger`).
2. Open **PowerShell as Administrator** in the unzipped folder.
3. Register the service (auto-start, auto-restart on failure):

   ```powershell
   .\scripts\install-service.ps1 -InstallDir (Get-Location)
   ```

   To install to a fixed location instead, pass `-InstallDir C:\PLCDataLogger` (copy the files there
   first, or build on a dev machine with `-Publish`).

The service `PLCDataLogger` is now running and the web UI is at `http://localhost:5198/`.

## 4. Configure the site

Open `http://localhost:5198/` on the PC and use the web UI (the primary configuration method):

1. **PLCs** — add each PLC (or use **Scan** to discover OPC UA servers on the subnet and add with
   one click). Changes apply live, no restart. Use security `None` only on a trusted/commissioning
   network; otherwise `Basic256Sha256` (and accept the PLC's certificate).
2. **Upload** *(optional)* — choose `Google Drive`, set the destination folder, point at the OAuth
   client JSON, and click **Connect Google…** once. See the upload section in
   [README](README.md#export-upload--retention) for the Google Cloud setup. Leave as `None` for
   offline sites.

   > **Credentials are never bundled in the distribution.** Place the OAuth client JSON on the
   > target PC after install (e.g. in a `secrets\` folder next to the exe) and point the Upload page
   > at it. The encrypted token the app then stores (`google_token\`) and the OPC UA certificate
   > store (`pki\`) are likewise per-machine and must stay on the site.

   **Authorizing Google Drive on a service install.** The **Connect Google…** button can't open a
   browser when the logger runs as a Windows Service (it has no desktop), so it will tell you to use
   the command below instead. From a desktop session on the target PC, in the install folder:

   ```powershell
   .\PLCDataLogger.exe --authorize
   ```

   This opens the sign-in browser, stores the token (DPAPI, machine-bound), and exits — the running
   service then picks it up (restart it if needed). The OAuth **client** JSON is the same across all
   sites; only this one-time consent is per-machine.
3. **Tuning** *(in `appsettings.json`, then restart the service)* — `Storage.RetentionDays`,
   `Export.DailyAtLocalTime`, and `Subscription.DefaultDeadband` (raise to cut volume from noisy
   analog tags).

Per-site settings live in two files next to the exe: `appsettings.json` (static defaults) and
`config.local.json` (PLCs + upload, written by the UI).

## 5. Verify

- **Dashboard** (`http://localhost:5198/`): each PLC shows **Connected**, a tag count, and a recent
  **Last sample**; **Readings written** climbs. Any misconfiguration shows as a banner at the top.
- **Health JSON:** `curl http://localhost:5198/api/health`
- **Config check:** `curl http://localhost:5198/api/config/validate` (empty list = OK).
- **Logs:** `<install>\logs\plcdatalogger-*.log` (rolling daily). Startup logs a configuration
  validation summary.
- **Data:** `<install>\data\plcdata.db` (SQLite). Exports land in `<install>\exports\`.

## 6. Update

1. `.\scripts\uninstall-service.ps1` (elevated) — removes the service, leaves data/config in place.
2. Replace the binary with the new version (keep `appsettings.json`, `config.local.json`, `data\`,
   `google_token\`, `secrets\`).
3. Re-run `.\scripts\install-service.ps1 -InstallDir <folder>`.

The dashboard footer shows the running version, so you can confirm the update took.

## 7. Data & retention

- The SQLite database is the source of truth. With upload enabled, data is pruned past
  `RetentionDays` only **after** it's confirmed uploaded; offline, it's pruned purely by age.
- To pull data off an offline machine: copy the latest files from `exports\`, or copy
  `data\plcdata.db` (WAL mode — copy `*.db`, `*.db-wal`, `*.db-shm` together, or stop the service
  first for a clean copy).

## 8. Security checklist

- Use a real OPC UA security policy in production (not `None`); configure certificate trust.
- The web UI binds to **localhost only** — it is not remotely accessible by design.
- Cloud credentials (the Google refresh token) are encrypted at rest with Windows DPAPI; keep the
  `secrets\` and `google_token\` folders out of backups that leave the site.

## 9. Troubleshooting

| Symptom | Check |
| --- | --- |
| PLC shows **Disconnected** | PLC reachable? `Test-NetConnection <ip> -Port 4840`. Endpoint URL/security correct? See the PLC's **Note** column for the error. |
| **0 tags discovered** | PLC in RUN with its symbol configuration active? Tags must have the OPC UA access flag set. Check the discovery filter in `appsettings.json` matches the PLC's node-id pattern. |
| Config banner / won't start a PLC | Read the dashboard banner or `/api/config/validate`; invalid PLCs are skipped (others keep running). |
| Upload "configured but failing" | Internet/firewall to Google? Token still valid? Re-run **Connect Google…**. |
| Service not running | `Get-Service PLCDataLogger`; Windows Event Log; `logs\` for the fatal error. |
| Disk filling | Lower `RetentionDays`, or raise `Subscription.DefaultDeadband` to cut row volume. |
