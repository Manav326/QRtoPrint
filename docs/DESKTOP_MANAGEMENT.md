# SmartPrint Windows desktop workspace

## Product boundary

The Windows application is the shop owner's full management workspace. It is a native Tauri 2 desktop window with the existing React shop console inside it; it is not a browser tab, installed web shortcut, or tray-only utility. The customer QR/PWA experience remains a web application and continues to use the same cloud API and tenant data.

The desktop app reuses the existing shop routes, business rules, and API client: overview, print queue, printer management, QR stations, pricing, subscription, payments and refunds, customers, reports, and shop settings. There is no second shop database or second print queue.

## Windows components

- **SmartPrint Desktop** is the interactive owner application. Closing its window does not stop printing or the local service.
- **SmartPrint Agent** remains a separate .NET Windows Service. It owns Windows printer discovery, the local document queue, LAN document transfer, spooler execution and cloud heartbeat.
- **SmartPrint API** remains authoritative for user identity, shop membership, device enrollment, orders, payments, refunds, and print state.

The installer places the desktop executable in the SmartPrint application folder and the Agent service files in its `agent` subfolder. The service is installed and started with administrator approval. Its local status endpoint remains restricted to Private Windows network profiles.

## First-run activation

1. Install SmartPrint. The installer does not ask for a pairing code, API URL, or PowerShell command.
2. Sign in with the existing SmartPrint shop account.
3. The desktop app checks the local Agent service. When the service is available and not paired, it asks the authenticated shop API for a short-lived, single-use enrollment authorization.
4. The app passes the authorization directly to the service through the ACL-protected `QRtoPrint.SmartPrint.Agent.Control` Windows named pipe.
5. The Agent exchanges the authorization with the existing `/api/v1/agent/enroll` endpoint and saves its device credentials using DPAPI LocalMachine encryption.
6. The Agent begins its normal heartbeat and printer discovery. The owner continues to manage shop operations in the desktop window.

The one-time authorization is never rendered in the UI, put in a command line, or written to logs. The named pipe grants access to authenticated Windows users and the service identities; possession of the authorization alone is short-lived and single-use. The API remains responsible for verifying the shop owner's permissions and assigning the device to the authenticated tenant.

A computer can have only one active SmartPrint shop pairing across the platform. If it is already connected to another shop, the desktop offers a separate **Connect after revocation** action. The previous shop owner must first revoke the device from that shop's Print Agent page; the API checks this server-side under a per-machine transaction lock, and refuses the new enrollment while any old active record remains. After revocation, the new owner can retry and the Agent replaces its old DPAPI identity only after the API accepts the fresh, single-use authorization. A normal startup never transfers a machine automatically.

## Local session and device secrets

- Owner access/refresh session JSON is stored in Windows Credential Manager under `QRtoPrint.SmartPrint.Session`, not a plain-text file.
- The Agent's device secret remains in the existing DPAPI LocalMachine-protected identity store.
- The desktop UI never receives the Agent secret.
- The Agent service remains independent of the desktop window lifetime.

Credential Manager generic credentials have a bounded payload size. The desktop session writer rejects oversized values rather than falling back to unencrypted storage. On sign-out, the app clears the native saved session.

## Runtime and network

Tauri uses the Microsoft Edge WebView2 Runtime. Windows 11 includes the Evergreen Runtime; some Windows 10 and managed images may not have it installed. The installer must check for the documented WebView2 Runtime registry value and run Microsoft's Evergreen bootstrapper when it is missing. The desktop UI needs an internet connection for sign-in and cloud-managed shop workflows. Local printer discovery and the Windows service continue to run independently, but cloud authorisation, payments, order sync, and dispatch are not represented as offline-capable.

The desktop origin `http://tauri.localhost` is explicitly allowed by the cloud API CORS policy. The local Agent CORS allowlist contains that same exact origin plus the configured customer portal origin; no wildcard origin is used.

## Recovery and removal

- Restart or repair the installation to restore a missing or stopped Agent service.
- Revoke a device from the shop's Print Agent device list if the PC is lost, replaced, or being transferred.
- Uninstall removes the Windows service and firewall rule. The existing Agent identity and locally stored customer documents are not deleted automatically by the service uninstaller; use the shop's revoke workflow before disposal and follow the product's local-data retention policy.
- Do not disable Microsoft Defender, SmartScreen, Smart App Control, or Windows firewall protections to install or run SmartPrint.

## Release validation

The Windows release job builds the React desktop frontend, compiles the Tauri executable, publishes the existing self-contained .NET Agent, then packages both in the existing Inno Setup installer. The package remains intentionally unsigned for the controlled beta channel and must publish a SHA-256 checksum through the existing release workflow.

Before a stable public release, validate on clean supported Windows 10 and Windows 11 machines:

- first install with and without WebView2 already installed;
- owner sign-in, secure session restore, sign-out, and token refresh;
- first-time Agent auto-enrollment and already-paired same-shop startup;
- refusal to transfer an Agent paired to another shop;
- multiple installed printers, test printing, customer QR intake, document preview, accepted/rejected pages, partial refunds, queue dispatch, and explicit physical-output confirmation;
- desktop close/reopen while the Agent continues running;
- upgrade over the previous Agent-only installer and uninstall/reinstall recovery.
