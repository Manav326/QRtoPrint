# Windows Print Agent

This is a Windows Service. It pairs with one shop using a single-use enrollment code, stores the returned credential with Windows DPAPI (LocalMachine), heartbeats to the Smart Print API, and synchronizes Windows-installed printer queue inventory every 30 seconds.

## Local and production portal URL

The portal, QR links and API use one canonical HTTPS origin: PUBLIC_BASE_URL in .env or .env.local. Portal API requests use the same origin under /api, reverse-proxied to the API by Nginx.

Run .\scripts\docker-up-local.ps1 for local end-to-end testing. Create a named Cloudflare Tunnel and publish the chosen hostname to http://portal:80. The helper stores the tunnel token in .secrets/cloudflare/tunnel-token, which is ignored by Git. Do not put the token in source control or share it with customers.

Pair the Agent with the same PUBLIC_BASE_URL. After an authenticated heartbeat, the Agent synchronises the canonical URL and its Local API CORS origin from the API without changing its DPAPI-protected identity. Keep any previous Cloudflare hostname route active during a migration so existing Agents can receive the new address and previously printed QR codes remain usable.

## Pair and install

### Shop-owner setup (recommended)

1. In the shop workspace, open **Print Agent** and generate a one-time pairing code. It expires after 10 minutes.
2. On the Windows 10/11 computer connected to the shop printers, download and run the official **SmartPrint Windows Setup** installer from the QRtoPrint Downloads page or the latest GitHub release.
3. Approve the Windows administrator prompt. In the setup wizard, enter the pairing code from the shop workspace. The wizard pairs the computer, installs the Windows Service, configures its local endpoint and starts the service.
4. Return to **Print Agent** in the shop workspace. Wait for the computer heartbeat and discovered printer queues; do not treat installation alone as proof that a printer is ready.

The installer enables the local status and direct-upload endpoint and opens TCP 17842 through Windows Firewall on Private network profiles only. The authenticated heartbeat keeps the runtime browser CORS origin aligned with PUBLIC_BASE_URL.

### Advanced / developer setup

For a release ZIP or manual recovery, an administrator can pair and install from elevated PowerShell with the scripts in the package:

   .\scripts\pair-agent.ps1 -ApiBaseUrl "https://YOUR_PUBLIC_BASE_URL" -AgentExePath ".\SmartPrint.Agent.exe"

   .\scripts\install-agent-service.ps1 -AgentExePath ".\SmartPrint.Agent.exe" -CustomerPortalOrigin "https://YOUR_PUBLIC_BASE_URL"

To remove the Windows Service, run scripts\uninstall-agent-service.ps1 from an elevated PowerShell. This leaves the encrypted machine identity intact; revoke the agent in the shop workspace before disposing of or replacing the computer.

## Current capabilities

Direct customer document upload over shop Wi-Fi is implemented. The customer portal obtains a one-use upload grant from the API, sends document bytes to this paired Agent, and the Agent validates the hash and document structure before storing the file in its DPAPI-protected local queue. The API retains only job/document metadata, SHA-256, page analysis and quote/payment/dispatch state. The same PrintJob is later authorised by the shop and claimed by this Agent after manual payment verification; the cloud is not asked to download a LOCAL_AGENT document.

The cloud-assisted upload path remains available as a customer-selected fallback. Browser uploads require a secure HTTPS customer portal origin, any Local Network Access permission required by the browser, and a Windows Private network profile. The current HTTPS origin is learned from the Agent's authenticated heartbeat. Do not use an insecure HTTP portal for direct local uploads.

- Queries Win32_Printer through WMI for installed queue name, driver, port, status and default queue.
- Sends printer inventory to the paired shop tenant using agent-specific authentication headers.
- Exposes a read-only local status endpoint at `http://<shop-computer-LAN-IP>:17842/api/v1/local/status`. It returns only the paired agent ID, protocol version, readiness state and configured API origin; it does not expose the agent secret, queue or documents. The Windows desktop uses the separate local-only, ACL-protected named pipe for status fallback and enrollment.
- Reports up to eight RFC1918 IPv4 endpoint addresses in the authenticated cloud heartbeat. The customer session receives endpoints only for its assigned printer's recently online, non-revoked agent.
- The installer adds a Windows Firewall inbound rule for TCP 17842 on the Private network profile only. Keep shop computers on a trusted Private network profile. The Agent synchronises its current public origin through its authenticated heartbeat.
- Refreshes agent heartbeat and printer status every 30 seconds (configurable from 10 to 300 seconds).
- Marks previously discovered queues unavailable when they disappear from that computer's WMI inventory.
- Stores the agent secret and locally received print documents encrypted using DPAPI LocalMachine. The protected queue directory is restricted to SYSTEM and local Administrators.
- Claims only paid/authorised jobs assigned to one of this agent's live printer queues. The claim has a three-minute lease and a tenant/agent ownership check.
- Downloads documents only while the lease is active, verifies the source SHA-256 and size, writes an encrypted local document plus durable manifest, and then acknowledges local receipt to the cloud.
- Retries ACK_PENDING acknowledgements after restart and reuses an already stored job rather than downloading a second local copy.
- Executes only shop-authorised jobs after proving the matching protected local document. Physical printers rasterise PDF/JPEG/PNG and submit once through the Windows spooler. Microsoft Print to PDF is detected as a separate file-export destination and bypasses the interactive Windows Save As dialog.
- Persists SUBMITTING before the first spooler call, reconciles the stable document name after restart, and never automatically resubmits an uncertain attempt.
- Microsoft Print to PDF exports are written atomically to `C:\\Users\\Public\\Documents\\QRtoPrint Exports` by default. The shop operator can open the saved PDF directly; the API records its file name, path, size and SHA-256. The destination can be changed with `Agent:PdfExportDirectory`. Interrupted exports are reconciled from the final file before any retry.
- Reports spooler acceptance and queue status separately from physical completion. Only the shop operator can confirm that physical pages actually came out. PDF exports are complete only after the saved file is verified.
- Runs a six-hour local retention sweep. Expired, unfinalised uploads are removed; confirmed physical-print and reported PDF-export source documents and managed PDF exports are removed after `Agent:CompletedDocumentRetentionDays` (default 30, configurable from 7 to 3650 days). Output-unconfirmed jobs remain available for operator recovery.
