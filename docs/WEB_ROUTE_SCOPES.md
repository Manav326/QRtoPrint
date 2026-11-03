# Web application route scopes

QRtoPrint serves its public, platform-administration and shop experiences from one origin.

| Scope | URL | Purpose |
| --- | --- | --- |
| Public/customer | `/` | Public landing page and customer station entry routes |
| Platform administration | `/admin` | Platform administrator sign-in and console |
| Shop | `/shop` | Shop owner and shop employee sign-in and workspace |
| Customer station | `/s/{stationCode}` | QR station customer flow |

## Security boundaries

- The URL path selects the application scope. A stored account never redirects a visitor into another scope.
- Platform and shop browser sessions use separate session-storage keys.
- The admin login accepts only an unscoped JWT carrying the existing `platform_role=PlatformAdmin` claim.
- The shop login rejects platform administrator tokens. The shop API and platform API continue to enforce their own server-side authorization.
- This change does not grant platform privileges to tenant users or change backend authorization policy.

## Deep links

The Nginx portal serves the SPA entry document for `/admin/*`, `/shop/*`, legacy `/login` and `/signup`, and `/s/*`. Built assets remain rooted at `/`, so the same compiled portal bundle can serve all scopes without embedding a deployment hostname.

Shop workspace sections have stable paths:

- `/shop` — Overview
- `/shop/queue` — Print Queue
- `/shop/printers` — Printers
- `/shop/qr-stations` — QR Stations
- `/shop/print-agent` — Print Agent
- `/shop/pricing` — Pricing
- `/shop/subscription` — Subscription
- `/shop/payments` — Payments
- `/shop/customers` — Customers
- `/shop/reports` — Reports
- `/shop/settings` — Settings

Platform console sections have stable paths:

- `/admin` — Overview
- `/admin/tenants` — Shop tenants
- `/admin/plans` — Plans and entitlements
- `/admin/subscriptions` — Shop subscriptions
- `/admin/operations` — Operations and retention

## Deployment

The existing portal container, API reverse proxy and Cloudflare Tunnel are retained. No extra web service or hardcoded production domain is required. Deploy the portal image built from this branch using the existing deployment workflow.
