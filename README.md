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

The repository now contains an integrated first implementation: a React customer/shop portal, a tenant-scoped ASP.NET Core API, PostgreSQL data model and migrations, a paired Windows Print Agent with protected local document queue and spooler execution, automated unit/model tests, and CI. Shop onboarding, QR sessions, document upload and quoting, shop approval, UPI/cash reporting with manual shop verification, agent handoff, explicit print dispatch, and physical-output reconciliation are implemented. The platform-admin identity, protected read-only operations overview, tenant directory, versioned plan catalogue, entitlement enforcement, and audited trial/free/operator-confirmed paid subscription assignment are implemented. SaaS subscription invoicing and payment reconciliation, automated customer print-payment verification, LAN-only edge upload, and the validated physical-printer compatibility matrix remain future work. Platform admins configure versioned plan prices and entitlement fields; the API enforces assigned-plan printer, agent, QR station, monthly-job and document-size limits plus cloud-assisted-printing access. Publishing a plan does not assign it or trigger billing; paid access is granted only after an operator checks receipt and records the bank/UPI reference. See docs/ROADMAP.md for the delivery plan and acceptance boundaries.

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

   The script creates a local `.env` with fresh database, JWT-signing and document-encryption secrets, then pulls the published GHCR images and starts the stack. Keep `.env` private and backed up; losing the document-encryption key makes existing cloud documents unreadable.
4. Open the printed portal URL. The API health endpoint is `https://api.thinkwithsujeet.in/health/ready`.

The container images are published to GHCR by the main-branch workflow. If the first image pull reports an authorization error, sign in with a GitHub account that has `read:packages` access (`docker login ghcr.io`); repository owners can make both GHCR packages public once from Package settings to allow anonymous pulls.

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

The customer phone must open the HTTPS portal while connected to the same shop Wi-Fi/LAN as the Agent and allow the browser's Local Network Access permission. The shop computer must use a Windows Private network profile. Cloud-assisted upload remains available if the local route cannot be reached.

### Backups

Before upgrades or migrations, run an on-demand backup:

```powershell
.\scripts\docker-backup.ps1
```

The script pauses API/portal writes, exports PostgreSQL, archives the encrypted cloud-document volume and copies the exact `.env` secrets required to restore the documents. It restarts the services afterwards. Store all three files together in encrypted off-host storage. The Windows Agent's DPAPI-protected local queue remains on the shop computer and needs its own machine-level backup/recovery policy.

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
