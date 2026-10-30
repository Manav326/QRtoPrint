# API and Tenant Foundation

## Implemented API surface

### Shop registration and authentication

- POST /api/v1/auth/register-shop — atomically provisions a tenant, shop profile, 14-day trial subscription and first shop-owner identity.
- POST /api/v1/auth/login — validates credentials with ASP.NET Core Identity lockout policy and issues short-lived bearer access plus rotating refresh token.
- POST /api/v1/auth/refresh — atomically consumes a refresh token and rotates it.
- POST /api/v1/auth/logout — revokes the supplied refresh token.

Passwords are handled by ASP.NET Core Identity's password hasher and validation policy. Access tokens are signed JWTs. Set a unique signing key in each environment; the development fallback must never be used in production.

### Authenticated shop endpoints

- GET /api/v1/shop/profile
- PUT /api/v1/shop/profile (shop owner/manager)
- GET /api/v1/shop/printers
- POST /api/v1/shop/printers (shop owner/manager)
- PATCH /api/v1/shop/printers/{printerId}/active (shop owner/manager)
- GET /api/v1/shop/qr-stations
- POST /api/v1/shop/qr-stations (shop owner/manager)
- GET /api/v1/shop/pricing
- PUT /api/v1/shop/pricing (shop owner/manager)
- GET /api/v1/shop/print-jobs (latest 100 jobs)

Tenant IDs are derived from the authenticated token. Printer IDs supplied for station assignment are checked against the same tenant. Printer status is server-owned and starts as Unknown.

### Public QR session endpoints

- POST /api/v1/public/stations/{publicCode}/sessions — exchanges a public station identifier for a short-lived customer session.
- GET /api/v1/public/sessions/current — reads session/shop context using the X-Print-Session header.

Static QR codes are identifiers only. The API stores only a hash of the public station code and returns a random short-lived session token once. Session lifetime is currently 15 minutes. QR session creation is rate-limited.

## Data model

The initial PostgreSQL migration creates Identity tables and tenant-scoped tenants, shop profiles, subscriptions, printers, QR stations, customer sessions, refresh tokens and print-job metadata. Tenant-owned queries include an explicit tenant predicate; API resource identifiers are never treated as authorisation.

## Database setup

Set ConnectionStrings:SmartPrint and Authentication:SigningKey using environment variables or a secret store. Apply migrations using the scripts/migrate-database.ps1 PowerShell script.

ASPNETCORE_ENVIRONMENT=Development uses a local PostgreSQL connection fallback for developer convenience. The API does not automatically mutate a production database on startup. /health/live is process health; /health/ready verifies database connectivity.

## Current boundary

This increment provisions shops and secure station sessions and registers printer metadata. It does not yet implement file upload, document conversion, price calculation, payment collection, physical printer discovery or print submission. Those remain separate workstreams and must use this tenant/session foundation.

## Quote acceptance and shop review

- POST /api/v1/public/jobs/{jobId}/accept-quote — accepts a current quote from the same unexpired customer session and advances it to AWAITING_SHOP_APPROVAL.
- GET /api/v1/public/jobs/{jobId}/status — returns status only for the active customer session that created the job.
- POST /api/v1/shop/print-jobs/{jobId}/approve — shop owner/manager approval advances the request to AWAITING_PAYMENT, never directly to printing.
- POST /api/v1/shop/print-jobs/{jobId}/reject — rejects a customer-accepted request awaiting shop review.
- GET /api/v1/shop/print-jobs/{jobId}/document — tenant-authorised document preview. The file is decrypted only after tenant/job access is verified and is served with no-store and nosniff headers.

The payment integration is deliberately not active in this stage. Shop approval does not capture money or authorise physical print execution. Both customer and shop interfaces show that boundary.


## Job event history

Every important job transition is written to the tenant-scoped PrintJobEvents table with previous/new status, actor category, reason code and timestamp. A composite database relationship ensures an event cannot be attached to a job in another tenant.

- GET /api/v1/shop/print-jobs/{jobId}/events — shop-side event history for the authenticated tenant.
- GET /api/v1/public/jobs/{jobId}/status — current job state and a customer-safe timeline for the active session that created the job.

Events currently cover document upload, quote creation, quote expiry, customer quote acceptance, shop approval and shop rejection. Payment and printer transitions are intentionally not emitted until those workflows are implemented and verified.
