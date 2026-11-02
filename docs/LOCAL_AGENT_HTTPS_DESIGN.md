# Local Agent HTTPS over shop Wi-Fi

## Decision

Customer document bytes should stay on the shop LAN whenever the paired Windows Agent is reachable. The cloud remains the control plane for station sessions, one-use upload grants, document metadata and hashes, quotes, payment state, shop approval, dispatch authorization and job status. Cloud-assisted upload remains an optional fallback, not the preferred route when local Wi-Fi is usable.

The customer portal now selects **Shop Wi-Fi first** whenever the station session contains a paired Agent endpoint. Cloud-assisted upload is selected when no local endpoint is available and the shop's plan includes it. An explicit customer selection is retained while that route remains eligible.

## Free public-trust HTTPS option

A viable no-certificate-fee design is one publicly trusted Let's Encrypt certificate per Agent, issued using ACME DNS-01. DNS-01 validates domain control by checking a TXT record, so the shop PC does not need an inbound public port or a tunnel. The certificate authority does not charge for issuance; the design does require a domain under our control and an automatable DNS provider API.

Use a dedicated delegated DNS sub-zone for Agent names, for example:

`agent-<agent-guid>.agents.<owned-domain>`

Each hostname must have a DNS-only A record pointing to the Agent's currently selected private shop-Wi-Fi IPv4 address. Use a short TTL and update the record when the selected adapter or DHCP address changes. Do not proxy this hostname through Cloudflare or another CDN: the browser must connect directly to the private address over the shop LAN.

## Credential and key boundaries

- The Cloud API is the only component that receives the DNS-provider API token. It must be scoped to the dedicated Agent sub-zone, stored as a deployment secret, and never written to the database, installer, Windows Agent configuration, Android app or browser.
- The DNS token is used only by the cloud certificate service to create and remove the ACME DNS-01 TXT records and maintain the Agent A records.
- Each Windows Agent generates its own private key locally. It sends only a CSR to the cloud API; the private key never leaves that shop computer.
- The cloud returns the issued certificate chain to the authenticated Agent. The Agent stores the private key/certificate locally using Windows DPAPI protection and serves HTTPS on a dedicated local Agent port.
- Do not distribute one wildcard certificate/private key to every shop. Compromise of one shop must not permit impersonation of another shop.
- Persist ACME account state on the server and automate renewal well before certificate expiry. Test issuance and renewal against the ACME staging directory before enabling production issuance. Rate limits for new certificates must be considered when onboarding many shops.

## Browser and Android behavior

A trusted certificate solves TLS server identity and encryption; it does not grant permission to access a customer's private network. The customer portal must remain HTTPS and the first local connection must continue to be initiated from a clear user action. Chrome's Local Network Access permission is still required for a public website connecting to an RFC1918 address.

Android WebView local-network requests inherit the host app's local-network permission state. The current Android app targets SDK 35; before raising the target to SDK 37, implement Android's `ACCESS_LOCAL_NETWORK` runtime permission flow and test permission denial/revocation. Do not request that future permission on the current target as a speculative change.

## No silent downgrade

Once an Agent has advertised a validated HTTPS endpoint, the browser and Android app must use that endpoint for local status checks and document uploads. A TLS, hostname, certificate or permission failure must not silently retry the same transfer over HTTP. The UI can explain the issue and offer Cloud-assisted HTTPS when the shop is entitled to it.

Until the DNS zone and server-side DNS API credential are configured and the HTTPS endpoint passes real-device acceptance, the existing private-IP HTTP path remains a clearly disclosed compatibility route. It is not described as encrypted.

## Configuration required before implementing/activating issuance

The current repository has a Cloudflare Tunnel token for the public deployment, but that token is not a DNS API credential. Certificate automation needs these deployment-only settings:

- A delegated Agent DNS sub-zone under a domain we control.
- Its Cloudflare Zone ID (or equivalent DNS-provider zone identifier).
- A dedicated API token restricted to DNS edit on that sub-zone.
- The ACME account contact email (optional for protocol operation, recommended for notices).
- An ACME staging/production directory setting.

Do not ask shop owners for any of these values. They are platform deployment configuration.

## Acceptance tests

1. Issue a certificate using the ACME staging directory; confirm the private key is generated and remains on the Windows Agent.
2. Confirm the Agent HTTPS listener presents a valid chain for its exact Agent hostname and rejects the wrong hostname.
3. Confirm the hostname resolves to the selected private LAN IP and is updated after DHCP changes or adapter reselection.
4. Confirm Android Chrome/WebView and desktop Chrome grant Local Network Access from the HTTPS portal and successfully upload a document directly to the shop computer.
5. Confirm a denied/revoked Local Network Access permission gives a useful message and never triggers an HTTP retry.
6. Renew an expiring certificate, restart the Windows service, and verify the new certificate is used without losing the protected local queue.
7. Disconnect the shop Wi-Fi and verify Cloud-assisted HTTPS is offered only when included in the shop's plan.
8. Verify local uploads store document bytes only in the Agent's protected local queue; only metadata and hashes reach the cloud API.
