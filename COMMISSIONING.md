# PLC Data Logger — Commissioning Runbook

Step-by-step procedure for **commissioning a logger at a site** and for **updating** an existing
install. Work top to bottom and tick each box. For reference tables (ports, troubleshooting) and
architecture, see [DEPLOYMENT.md](DEPLOYMENT.md) and [README.md](README.md).

- **Distribution:** one zip, `PLCDataLogger-v<version>-win-x64.zip` — self-contained, **no .NET
  install needed**.
- **Time on site:** ~30–45 min for a single-PLC site once prep is done.
- **You will need local administrator rights** on the logger PC.

---

## 1. Before you go (prep — do this off-site)

Gather and confirm, so you are not blocked on site:

- [ ] **Site name** to use (labels the dashboard and every export file, e.g. `Planta-Norte`).
- [ ] **PLC list:** for each PLC, its **IP address**, **OPC UA port** (default `4840`), and intended
      **security policy** (`None` only on a trusted/commissioning network, otherwise
      `Basic256Sha256`).
- [ ] **Online or offline site?** Decide whether readings upload to Google Drive or stay local for
      manual pickup. Offline (`None` provider) is fully supported — nothing else is required.
- [ ] **If uploading:** the **Google OAuth client JSON** (Desktop-app type, Drive API enabled,
      consent screen **published**). This is the same file for every site; it is **never** in the
      distribution zip — carry it separately (USB/secure transfer). See the upload setup in
      [README](README.md#export-upload--retention).
- [ ] **Retention window** (days of readings to keep locally) and **upload schedule** (every N
      minutes, or daily at a fixed time) agreed with the customer.
- [ ] The build zip for the version you intend to install.

Build the zip (on a dev machine, if you don't already have it):

```powershell
.\scripts\package.ps1                 # or: .\scripts\package.ps1 -Version 1.1.0
```

This produces `dist\PLCDataLogger-v<version>-win-x64.zip` with the app, the install/uninstall
scripts, and the docs — and scrubs any secrets/runtime data so nothing sensitive ships.

---

## 2. Prepare the PC and network

On the logger PC:

- [ ] Windows 10/11 or Windows Server, **x64**. Confirm you can log in with a **local admin**
      account.
- [ ] Copy the zip over and **unzip** it (e.g. to `C:\PLCDataLogger`).
- [ ] Confirm the PC can reach each PLC's OPC UA endpoint:

  ```powershell
  Test-NetConnection <plc-ip> -Port 4840
  ```

  `TcpTestSucceeded : True` for every PLC before continuing.

- [ ] **If uploading:** confirm outbound HTTPS (TCP 443) to `*.googleapis.com` /
      `accounts.google.com` is allowed (share the port table in
      [DEPLOYMENT.md §2](DEPLOYMENT.md#2-network-requirements-share-with-site-it) with site IT).

---

## 3. Install the service

From an **elevated PowerShell** prompt, in the unzipped folder:

```powershell
.\scripts\install-service.ps1 -InstallDir (Get-Location)
```

This registers the `PLCDataLogger` service with auto-start and auto-restart-on-failure, then starts
it.

- [ ] Service is running: `Get-Service PLCDataLogger` shows **Running**.
- [ ] Web UI reachable: browse to `http://localhost:5198/` on the PC (it binds to **localhost only**
      by design).

> To install to a fixed path instead, copy the files there first and pass
> `-InstallDir C:\PLCDataLogger`.

---

## 4. Configure the site (web UI)

All configuration is done in the browser at `http://localhost:5198/`. Changes apply **live** — no
service restart needed.

1. **Settings** — set:
   - [ ] **Site name** (used in export filenames and the dashboard header).
   - [ ] **Upload schedule** — _every N minutes_ or _daily at HH:mm_ (off-peak times avoid competing
         with production traffic).
   - [ ] **Retention window** — days of readings to keep locally (`0` = keep everything). With upload
         enabled, data is only pruned once it has been uploaded.

2. **PLCs** (or **Scan**) —
   - [ ] Add each PLC by endpoint, or use **Scan** to discover OPC UA servers on the subnet and add
         one with a click.
   - [ ] Set the correct **security policy** per PLC (accept the PLC's certificate if using a secure
         policy).

3. **Upload** _(online sites only; skip for offline)_ —
   - [ ] Choose **Google Drive**, set the **destination folder**, and point the credentials path at
         the OAuth client JSON you placed on the PC (e.g. in a `secrets\` folder next to the exe).
   - [ ] Authorize Google (see below), then **Test connection**.

   **Authorizing Google on a service install.** The **Connect Google…** button can't open a browser
   from a Windows Service (no desktop). From a desktop session on the PC, in the install folder:

   ```powershell
   .\PLCDataLogger.exe --authorize
   ```

   This opens the sign-in browser once, stores the refresh token encrypted (DPAPI, machine-bound),
   and exits. The running service then picks it up (restart it if it doesn't within a minute).

Per-site settings live in two files next to the exe: `appsettings.json` (static seed defaults) and
`config.local.json` (site name, schedule, retention, PLCs, upload — written by the UI). You normally
only touch the UI.

---

## 5. Verify and commission

- [ ] **Dashboard** (`http://localhost:5198/`): every PLC shows **Connected**, a non-zero **tag
      count**, and a recent **Last sample**; **Readings written** is climbing.
- [ ] **No red/amber banner** at the top of the dashboard (configuration issues show there). Empty
      result confirms clean config:

  ```powershell
  curl http://localhost:5198/api/config/validate
  ```

- [ ] **Health snapshot** looks right: `curl http://localhost:5198/api/health`
- [ ] **Force one export/upload cycle** now instead of waiting for the schedule: click **Export &
      upload now** on the dashboard. Then confirm:
  - [ ] A rolling file per PLC appears in `<install>\exports\` named `<Site>-<PLC>.csv`.
  - [ ] _(online)_ the file appears in the Google Drive destination folder, and the log shows
        `Uploaded <Site>-<PLC>.csv`.
- [ ] _(optional)_ On the **Backup** page, take a **database backup** to confirm the raw-DB path
      works end to end (`<Site>-backup-<timestamp>.db`).
- [ ] **Logs** are clean: `<install>\logs\plcdatalogger-*.log` (startup prints a config-validation
      summary).

### Sign-off

| Item | Value / result |
| --- | --- |
| Site name | |
| Logger version (dashboard footer) | |
| PLC(s) connected (name → tags) | |
| Upload provider | `None` / `GoogleDrive` |
| First export file(s) verified | ☐ local ☐ uploaded |
| Retention / schedule set | |
| Commissioned by / date | |

---

## 6. Handover

Leave with the site / record centrally:

- The **install folder path** and that it runs as the `PLCDataLogger` Windows Service.
- The web UI URL (`http://localhost:5198/`, on the PC only).
- Where data lives: `data\plcdata.db` (source of truth) and `exports\` (CSV pickup).
- **Do not delete** `data\`, `config.local.json`, `secrets\`, `google_token\`, or `pki\` — these are
  per-machine state and secrets.
- How to pull data off an **offline** machine: copy the latest `exports\<Site>-<PLC>.csv`, use the
  **Backup** page's windowed export / DB backup, or copy `data\plcdata.db` (+ `-wal`/`-shm`, or stop
  the service first for a clean single-file copy).

---

## 7. Update an existing install

In-place upgrade; **data, config, and secrets are preserved**.

1. From an **elevated PowerShell** prompt in the install folder, remove the service (leaves files in
   place):

   ```powershell
   .\scripts\uninstall-service.ps1
   ```

2. Replace the **binary and bundled files** with the new version. **Keep** these per-site items:
   `appsettings.json` _(only if you customized it)_, `config.local.json`, `data\`, `logs\`,
   `exports\`, `secrets\`, `google_token\`, `pki\`.

   > Safest method: unzip the new build to a **temp folder**, then copy its files **over** the
   > install folder without deleting the folders listed above. Or unzip fresh and copy the preserved
   > items back in.

3. Re-register and start the service:

   ```powershell
   .\scripts\install-service.ps1 -InstallDir (Get-Location)
   ```

4. **Confirm** the update: the **dashboard footer** shows the new version, PLCs reconnect, and
   **Readings written** resumes climbing.

### Rollback

If the new version misbehaves: `uninstall-service.ps1`, restore the previous binary (keeping the same
preserved data/config folders), then `install-service.ps1` again. The database schema is
backward-compatible within a major version; take a **DB backup** (Backup page) before a major upgrade
if in doubt.

---

## 8. Quick reference

| Thing | Value |
| --- | --- |
| Service name | `PLCDataLogger` (auto-start, auto-restart) |
| Web UI | `http://localhost:5198/` (localhost only) |
| Install | `scripts\install-service.ps1 -InstallDir <folder>` (elevated) |
| Uninstall | `scripts\uninstall-service.ps1` (elevated) |
| Google consent | `.\PLCDataLogger.exe --authorize` (desktop session) |
| Data | `<install>\data\plcdata.db` |
| Exports | `<install>\exports\<Site>-<PLC>.csv` |
| Logs | `<install>\logs\plcdatalogger-*.log` |
| Config (UI-written) | `<install>\config.local.json` |
| Ports | PLC OPC UA `4840/tcp` out · Google `443/tcp` out (if enabled) · UI `5198/tcp` local |

Troubleshooting table: [DEPLOYMENT.md §9](DEPLOYMENT.md#9-troubleshooting).
