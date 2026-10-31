# QRtoPrint — Smart Print Platform

QRtoPrint is a multi-tenant SaaS platform for QR-initiated document printing, combining customer web experiences, shop operations, a local Windows Print Agent, and platform-owner administration.

## Product principles

- Support independent printing shops as the primary SaaS customer.
- Support both local shop-Wi-Fi printing and secure cloud-assisted printing.
- Integrate with multiple printer manufacturers through capability discovery and replaceable adapters.
- Keep print execution local to the shop; the cloud coordinates identity, tenancy, subscriptions, secure job routing, and optional cloud transfer.
- Keep each shop's customers, documents, jobs, payment records, devices, users, and branding isolated.
- Route customer print payments to the shop's own payment account. Route Smart Print SaaS subscription payments to the platform owner. These are separate financial flows.
- Deliver a premium, coherent, accessible UI across customer PWA, shop desktop/web, platform administration, and the Windows agent.

## Preferred domain

thinkwithsujeet.in is the preferred domain. Proposed subdomains (subject to DNS and hosting verification):

- print.thinkwithsujeet.in — customer portal
- shops.thinkwithsujeet.in — shop portal
- admin.thinkwithsujeet.in — platform administration
- api.thinkwithsujeet.in — API
- downloads.thinkwithsujeet.in — desktop/agent releases

## Repository status

The repository contains an integrated customer/shop portal, tenant-scoped ASP.NET Core API, PostgreSQL schema and migrations, paired Windows Print Agent, encrypted local print queue, spooler execution, automated tests and CI. Supported single-file PDF/JPG/JPEG/PNG jobs (20 MB hard limit, PDFs up to 500 pages) can be uploaded either through protected cloud storage or directly from the customer phone to the paired shop Agent over the shop LAN. Both modes create the same cloud-coordinated PrintJob and use the existing server quote, shop approval, manual shop payment verification, explicit dispatch and output-confirmation lifecycle. In direct LAN mode, document bytes remain on the shop network; the API retains only document metadata, SHA-256 and page analysis. The cloud remains required for QR-session validation, upload grants, metadata, quotes, payment and dispatch authorisation, so this is not offline printing. Platform-admin identity, read-only operations overview, tenant directory, versioned plans, entitlement enforcement and audited trial/free/operator-confirmed paid subscription assignment are implemented. Automated payment gateways, SaaS subscription invoices, refunds/reconciliation, multi-file/Office upload, Agent tray/auto-update and the physical-printer compatibility matrix remain future work. See docs/ROADMAP.md for scope and validation boundaries.

## Android companion (initial implementation)

A native Android client is located in `apps/android`. It complements the customer PWA rather than replacing the shared print workflow.

- Scan the existing shop QR code (`/s/{publicCode}`) in a branded CameraX preview using bundled on-device ML Kit. Release builds accept only HTTPS station URLs; debug builds additionally allow HTTP only for RFC1918 private IPv4 hosts on port 3000 for local LAN development. Camera frames stay on the device.
- Keep documents in app-private local storage with folders and subfolders. Import multiple files or an entire folder through Android's Storage Access Framework; export a file or folder back to a user-selected file-manager destination.
- Select a saved PDF/JPG/JPEG/PNG from the native library when the customer portal requests a file. The existing API limits printable documents to 20 MB and remains responsible for document analysis, quotes, shop approval, payment and job status.
- The embedded customer portal uses the same station-bound session and upload APIs as the web PWA. It does not introduce a second print-job lifecycle or claim offline printing.
- The local Agent upload exception permits only RFC1918 IPv4 addresses on TCP 17842 and the Agent upload route over cleartext HTTP; all other HTTP WebView requests are blocked. HTTPS remains required for the shop portal.
- General library imports are capped at 100 MB per file. The app does not request broad storage access; Android file/folder pickers control import and export.

CI builds the Android debug APK and runs JVM unit tests. A real-device matrix (QR scanning, file-provider interoperability, Android version coverage and shop-Wi-Fi Agent upload) is still required before production or Play Store release. The debug APK is published as the `qrtoprint-android-debug` workflow artifact.

## Download and install the Android debug APK

On Windows with GitHub CLI authenticated and Android Debug Bridge (ADB) installed:

```powershell
.\scripts\download-android-apk.ps1
.\scripts\download-android-apk.ps1 -Install
.\scripts\download-android-apk.ps1 -Install -DeviceSerial "YOUR_DEVICE_SERIAL"
```

The script selects the latest successful main-branch CI run, downloads the `qrtoprint-android-debug` artifact, validates the APK and reports its SHA-256. Use `-Install` only after enabling USB debugging and authorising the device. This is a development debug build, not a Play Store release.

The same CI run publishes `qrtoprint-android-test-reports` with JVM and emulator test evidence.

## Run locally over HTTPS (no public URL or deployment)

Install Docker Desktop and connect the Windows laptop and Android phone to the same trusted Wi-Fi network. Local Agent access from browsers requires a secure HTTPS page; current Chrome versions block local-network requests from an HTTP page without showing a permission prompt.

From an elevated or normal PowerShell session in the repository root, run:

```powershell
.\scripts\docker-up-local.ps1 -PortalHost "192.168.31.47"
```

The helper reuses the existing `.env.local` database and encryption secrets, preserves the database and document volumes, builds PostgreSQL/API/portal, adds a local Caddy HTTPS reverse proxy, exports Caddy's public root certificate and attempts to trust it in the current Windows user store. It does not delete or recreate the database. Local-only secrets must never be reused for production.

- Secure customer/shop portal: `https://192.168.31.47:3443`
- API health through the same secure origin: `https://192.168.31.47:3443/health/ready`
- Agent pairing API (on the laptop): `http://localhost:8080`
- Agent LAN listener: TCP `17842`
- Caddy local CA certificate: `certs/qrtoprint-local-root.crt`

### Trust the local HTTPS certificate on the phone

The local HTTPS certificate is issued by a private Caddy development CA, not a public certificate authority. The phone must trust the exported root certificate before Chrome will treat the portal as a secure context.

1. Transfer `certs/qrtoprint-local-root.crt` from the laptop to the phone (USB or another trusted transfer method).
2. On Android, open **Settings → Security / Security & privacy → Encryption & credentials → Install a certificate → CA certificate**. Menu names vary by Android manufacturer. Select `qrtoprint-local-root.crt` and complete the Android confirmation. Install it as a CA certificate, not as a Wi-Fi or VPN user certificate.
3. On the laptop's Chrome, if the secure portal still shows a certificate warning, open **Settings → Privacy and security → Security → Manage certificates → Authorities → Import** and import the same root certificate, trusting it to identify websites.
4. Open `https://192.168.31.47:3443` on the phone. It should load without a certificate warning. Do not use the legacy `http://192.168.31.47:3000` URL for direct Agent access.

The Caddy root CA is persisted in Docker volume `qrtoprint_local_caddy_data`. Keep that volume: deleting it creates a new CA and requires trusting the replacement certificate again. The CA private key stays in the Docker volume; only its public root certificate is exported.

### Pair / update the Windows Agent's allowed portal origin

Pairing remains against the local API on the same laptop. The pairing you already completed is preserved. From Administrator PowerShell, rerun the installer to update the Agent's exact allowed browser origin to HTTPS:

```powershell
$agentDir = Join-Path $env:USERPROFILE "Downloads\QRtoPrintAgent"
$agentExe = Join-Path $agentDir "SmartPrint.Agent.exe"

& "$agentDir\install-agent-service.ps1" -AgentExePath $agentExe -CustomerPortalOrigin "https://192.168.31.47:3443"
Get-Service QRtoPrintSmartPrintAgent
```

The Agent continues listening on TCP `17842`; the installer configures its CORS allowlist for the new HTTPS portal origin and opens the Agent port on the Windows Private firewall profile. The local HTTPS proxy also needs inbound TCP `3443` on the Private profile. Keep the Windows Wi-Fi network set to **Private** and allow device-to-device traffic on the router (AP/client isolation must be disabled).

### Android customer flow

Use the station QR link generated from the updated local shop portal. If an existing printed QR still opens the old HTTP URL, open its **secure HTTPS shop link** on the page or rotate the station code in the shop portal and print the replacement QR. Rotating a station code invalidates the previous QR.

Once the phone opens the trusted HTTPS page, tap **Check shop Wi-Fi**. A public HTTPS portal may trigger Chrome's Local network access prompt; allow it. For this local HTTPS portal, both the portal and Agent are on the same private network, so Chrome may not show a separate permission prompt; the status check should simply connect. If Chrome shows a permission as blocked, use the site information icon beside the address bar → Permissions → Local network access → Allow, then reload.

Direct LAN uploads keep document bytes on the shop network. The cloud API still coordinates QR sessions, upload grants, metadata, quotes, payment and dispatch; this is not offline printing. Cloud-assisted upload remains available as a fallback.

### Stop / restart

Stop containers without deleting local data:

```powershell
docker compose --env-file .env.local -f compose.local.yml down
```

Restart through the helper:

```powershell
.\scripts\docker-up-local.ps1 -PortalHost "192.168.31.47"
```

Do not use `down -v`; that would erase the local database, documents and Caddy CA identity.
 
## Docker deployment

The production Docker stack runs PostgreSQL, the ASP.NET Core API, the React portal and Caddy (automatic HTTPS). Database migrations are applied at API startup only when the compose setting `Database__ApplyMigrationsOnStartup=true` is enabled. PostgreSQL and cloud document storage use persistent Docker volumes.

### First start

1. Point two DNS A/AAAA records at the Docker host:
   - `print.thinkwithsujeet.in` for the portal.
   - `api.thinkwithsujeet.in` for the API used by the Windows Agent.
2. Allow inbound TCP 80 and 443 (and UDP 443 for HTTP/3) through the host firewall.
3. From the repository directory, run:

   ```powershell
   .\scripts\docker-up.ps1 -AcmeEmail "your-operations-email@example.com"
   ```

   The script creates a local `.env` with fresh database, JWT-signing and document-encryption secrets. It first tries to pull the published GHCR images (using an authenticated GitHub CLI session when available); if the packages are not accessible, it builds the same API and portal images from this checkout. It then starts the stack. Keep `.env` private and backed up; losing the document-encryption key makes existing cloud documents unreadable.
4. Open the printed portal URL. The API health endpoint is `https://api.thinkwithsujeet.in/health/ready`.

The container images are built and published to GHCR by the main-branch workflow. GHCR packages are private by default until their owner changes visibility. The deployment script can use an authenticated GitHub CLI session or fall back to a local source build. To use anonymous image pulls, make both packages public once in GitHub → Packages → package settings.

### First administrator

After the first deployment is healthy, bootstrap the separate platform administrator:

```powershell
.\scripts\docker-bootstrap-platform-admin.ps1 -Email "admin@example.com" -DisplayName "Platform Administrator"
```

The script prompts securely for the password and creates or repairs only a tenantless platform-admin identity. It refuses to elevate an existing shop user.

### Windows printer computer

Docker does not run the Windows Print Spooler Agent on a Linux server. Install the Windows Agent as a Windows Service on the shop computer physically connected to the printer. Download the current `qrtoprint-agent-win-x64` CI artifact with `scripts\download-agent.ps1`, extract it, then pair and install:

```powershell
.\scripts\pair-agent.ps1 -ApiBaseUrl "https://api.thinkwithsujeet.in" -AgentExePath ".\SmartPrint.Agent.exe"
.\scripts\install-agent-service.ps1 -AgentExePath ".\SmartPrint.Agent.exe" -CustomerPortalOrigin "https://print.thinkwithsujeet.in"
```

The customer phone must open the HTTPS portal while connected to the same shop Wi-Fi/LAN as the Agent and allow the browser's Local Network Access permission. The shop computer must use a Windows Private network profile and the Agent installer must use the exact customer portal origin. Direct LAN upload keeps the document bytes on the shop network but still needs the online API for QR session, upload grant, quote, payment and dispatch coordination. Cloud-assisted upload remains available if the local route cannot be reached.

### Backups

Before upgrades or migrations, run an on-demand backup:

```powershell
.\scripts\docker-backup.ps1
```

The script pauses API/portal writes, exports PostgreSQL, archives the encrypted cloud-document volume and copies the exact `.env` secrets required to restore the documents. It restricts local backup-file ACLs and restarts the services afterwards. Store all three files together in encrypted off-host storage. To restore the newest complete backup set in a folder, run `.\\scripts\\docker-restore.ps1 -BackupDirectory ".\\backups" -ConfirmRestore`; the script asks you to type the selected backup timestamp before replacing the live database and document volume. Restore requires the original PostgreSQL credentials and preserves a copy of the current `.env` before switching to the backup secrets. The Windows Agent's DPAPI-protected local queue remains on each shop computer and needs its own machine-level backup/recovery policy.

### Operations

```powershell
docker compose --env-file .env -f compose.yml ps
docker compose --env-file .env -f compose.yml logs -f api
docker compose --env-file .env -f compose.yml pull
docker compose --env-file .env -f compose.yml up -d
.\scripts\docker-down.ps1
```

`docker-down.ps1` stops containers but preserves database, documents and TLS certificate volumes. Do not use `docker compose down -v` unless you deliberately intend to delete persisted application data. Back up the PostgreSQL volume and document volume together with the exact encryption key from `.env`.

## Documentation

- [Product blueprint](docs/PRODUCT_BLUEPRINT.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Premium design system](docs/DESIGN_SYSTEM.md)
- [Security and operations](docs/SECURITY_AND_OPERATIONS.md)
- [Delivery roadmap and acceptance criteria](docs/ROADMAP.md)

## Delivery principles

- Audit the current state before changing existing functionality.
- Keep the default branch buildable; use focused, reviewable changes.
- Never claim a physical print completed based only on spooler acceptance.
- Never automatically resubmit an uncertain physical print after restart.
- Never trust client-supplied prices or payment-success screenshots.
- Do not claim hardware compatibility until representative physical devices have been tested.


Platform administrators can manage versioned SaaS plans and grant published trial, free or paid versions to shops. Paid access can be granted after an operator confirms the funds were received and records the bank/UPI transaction reference; the granting actor and amount are kept in the append-only assignment audit. Automated gateway checkout, subscription invoices and payment reconciliation remain future work.
