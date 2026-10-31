# Windows Print Agent

This is a Windows Service. It pairs with one shop using a single-use enrollment code, stores the returned credential with Windows DPAPI (LocalMachine), heartbeats to the Smart Print API, and synchronizes Windows-installed printer queue inventory every 30 seconds.

## Local and production portal URL

The portal, QR links and API use one canonical HTTPS origin: PUBLIC_BASE_URL in .env or .env.local. Portal API requests use the same origin under /api, reverse-proxied to the API by Nginx.

Run .\scripts\docker-up-local.ps1 for local end-to-end testing. Create a named Cloudflare Tunnel and publish the chosen hostname to http://portal:80. The helper stores the tunnel token in .secrets/cloudflare/tunnel-token, which is ignored by Git. Do not put the token in source control or share it with customers.

Pair the Agent with the same PUBLIC_BASE_URL. After an authenticated heartbeat, the Agent synchronises the canonical URL and its Local API CORS origin from the API without changing its DPAPI-protected identity. Keep any previous Cloudflare hostname route active during a migration so existing Agents can receive the new address and previously printed QR codes remain usable.

## Pair and install

1. In the shop workspace, open Print Agent and generate a one-time pairing code. It expires after 10 minutes.
2. Build or extract the SmartPrint.Agent Windows release on the shop computer.
3. Open PowerShell as Administrator and pair the computer:

   .\scripts\pair-agent.ps1 -ApiBaseUrl "https://YOUR_PUBLIC_BASE_URL" -AgentExePath ".\SmartPrint.Agent.exe"

4. Install and start the Windows Service using the same canonical customer portal HTTPS origin:

   .\scripts\install-agent-service.ps1 -AgentExePath ".\SmartPrint.Agent.exe" -CustomerPortalOrigin "https://YOUR_PUBLIC_BASE_URL"

The installer enables the local status and direct-upload endpoint and opens TCP 17842 through Windows Firewall on Private network profiles only. The authenticated heartbeat keeps the runtime browser CORS origin aligned with PUBLIC_BASE_URL.

5. Return to the Print Agent screen in the shop workspace. The computer should report its next heartbeat and installed printer queues.

To remove the Windows Service, run scripts\uninstall-agent-service.ps1 from an elevated PowerShell. This leaves the encrypted machine identity intact; revoke the agent in the shop workspace before disposing of or replacing the computer.

## Current capabilities

Direct customer document upload over shop Wi-Fi is implemented. The customer portal obtains a one-use upload grant from the API, sends document bytes to this paired Agent, and the Agent validates the hash and document structure before storing the file in its DPAPI-protected local queue. The API retains only job/document metadata, SHA-256, page analysis and quote/payment/dispatch state. The same PrintJob is later authorised by the shop and claimed by this Agent after manual payment verification; the cloud is not asked to download a LOCAL_AGENT document.

The cloud-assisted upload path remains available as a customer-selected fallback. Browser uploads require a secure HTTPS customer portal origin, any Local Network Access permission required by the browser, and a Windows Private network profile. The current HTTPS origin is learned from the Agent's authenticated heartbeat. Do not use an insecure HTTP portal for direct local uploads.

- Queries Win32_Printer through WMI for installed queue name, driver, port, status and default queue.
- Sends printer inventory to the paired shop tenant using agent-specific authentication headers.
- Exposes a read-only local status endpoint at `http://<shop-computer-LAN-IP>:17842/api/v1/local/status`. It returns only the paired agent ID, protocol version and readiness state; it does not expose the agent secret, queue or documents.
- Reports up to eight RFC1918 IPv4 endpoint addresses in the authenticated cloud heartbeat. The customer session receives endpoints only for its assigned printer's recently online, non-revoked agent.
- The installer adds a Windows Firewall inbound rule for TCP 17842 on the Private network profile only. Keep shop computers on a trusted Private network profile. The Agent synchronises its current public origin through its authenticated heartbeat.
- Refreshes agent heartbeat and printer status every 30 seconds (configurable from 10 to 300 seconds).
- Marks previously discovered queues unavailable when they disappear from that computer's WMI inventory.
- Stores the agent secret and locally received print documents encrypted using DPAPI LocalMachine. The protected queue directory is restricted to SYSTEM and local Administrators.
- Claims only paid/authorised jobs assigned to one of this agent's live printer queues. The claim has a three-minute lease and a tenant/agent ownership check.
- Downloads documents only while the lease is active, verifies the source SHA-256 and size, writes an encrypted local document plus durable manifest, and then acknowledges local receipt to the cloud.
- Retries ACK_PENDING acknowledgements after restart and reuses an already stored job rather than downloading a second local copy.
- Executes only shop-authorised jobs after proving the matching protected local document. It rasterises PDF/JPEG/PNG, applies supported paper/colour/duplex settings through the installed driver and submits a single job to the Windows spooler.
- Persists SUBMITTING before the first spooler call, reconciles the stable document name after restart, and never automatically resubmits an uncertain attempt.
- Reports spooler acceptance and queue status separately from physical completion. Only the shop operator can confirm that the requested pages actually came out.
