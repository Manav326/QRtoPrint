# API and Tenant Foundation

## Implemented API surface

### Shop registration and authentication

- POST /api/v1/auth/register-shop — atomically provisions a tenant, shop profile, 14-day trial subscription and first shop-owner identity.
- POST /api/v1/auth/login — validates credentials with ASP.NET Core Identity lockout policy and issues short-lived bearer access plus rotating refresh token.
- POST /api/v1/auth/refresh — atomically consumes a refresh token and rotates it.
- POST /api/v1/auth/logout — revokes the supplied refresh token.

Passwords are handled by ASP.NET Core Identity's password hasher and validation policy. Access tokens are signed JWTs. Set a unique signing key in each environment; the development fallback must never be used in production.

### Shop operations reports

- GET /api/v1/shop/reports/summary?days=7|30|90 — returns a summary scoped to the authenticated shop. The default period is 30 days; unsupported period values are rejected.
- Job totals and status breakdown use jobs created inside the selected UTC window. `completedJobs` counts only jobs in `COMPLETED` with an operator-recorded `OutputConfirmedUtc`; `pagesRequested` is page count multiplied by requested copies and is not a claim of physical output.
- Verified collections are grouped by currency and payment method and use `VerifiedUtc` within the selected window. UPI references that have not been independently verified by the shop are excluded. The pending-payment count includes all currently open UPI/cash attempts for the tenant, regardless of creation date, so an old unresolved payment never disappears from the operator's review count.

### Platform administration

Platform administrator accounts are provisioned offline using `scripts/bootstrap-platform-admin.ps1`. After database migrations and production database/signing-key configuration are in place, run `scripts\bootstrap-platform-admin.ps1 -Email "owner@example.com" -DisplayName "Platform Administrator"` from PowerShell. The script prompts for the password as a secure input and passes it to a one-time local API bootstrap command; it does not store the password in the repository. A bootstrap operation refuses to elevate an account that already belongs to a shop. Platform accounts have no `TenantId` and receive a dedicated `platform_role=PlatformAdmin` user claim. Shop routes remain separately tenant-scoped.

- GET /api/v1/platform/overview — returns aggregate tenant, subscription, printer, agent and recent job counts. It does not expose customer documents or payment credentials.
- GET /api/v1/platform/tenants?search=&status=&page=1&pageSize=25 — returns a paginated tenant directory with shop status, latest subscription metadata, printer/agent counts and recent job volume. Search is limited to shop business name and slug.

These routes require the platform-admin authorization policy. A shop user cannot access them, even if a platform claim is mistakenly added to a tenant-scoped token.

### Platform subscription plan catalogue

Platform-admin-only plan management is available under the same unscoped platform authorization policy:

- GET /api/v1/platform/plans — lists plans and their version history.
- POST /api/v1/platform/plans — creates a non-system plan and its first draft version.
- PUT /api/v1/platform/plans/{planId} — updates plan metadata and whether it is available for future assignment.
- PUT /api/v1/platform/plans/{planId}/draft — saves the current draft or starts the next version; published versions are never edited in place.
- POST /api/v1/platform/plans/{planId}/versions/{versionNumber}/publish — publishes a draft only after at least one monthly or annual price is explicitly set. Zero is a valid explicit free price.

Prices are integer minor currency units with a required three-letter currency code. A blank price means that billing interval is not configured; zero explicitly means free. Nullable quotas mean no configured cap; positive integer quotas cover printers, agent computers, QR stations, monthly print jobs and maximum document bytes. Feature flags are centrally stored for cloud-assisted printing, white-label and advanced reports.

Plan versions are immutable after publication. A subscription's nullable PlanVersionId references the exact version used for its terms. The additive migration seeds a protected TRIAL plan/version and maps existing TRIAL subscriptions to it without adding a price or a quota. The TRIAL code and system plan are not editable through the catalogue. Legacy non-trial subscription rows remain valid with a null PlanVersionId pending an explicit migration/assignment policy.

The catalogue is a configuration foundation, not a payment implementation. Publishing a plan does not bill a shop or assign a subscription; plan assignment, runtime quota enforcement, subscription invoices, platform payment reconciliation, renewals, dunning and refunds remain separate work. Do not use shop-owned print-payment settings or records for SaaS subscription charges.

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


## Windows agent pairing and discovery

- POST /api/v1/shop/agent-enrollment-codes — shop owner/manager issues a 10-minute, single-use pairing code.
- GET /api/v1/shop/agent-devices — lists paired agent computers and discovered printer counts.
- PATCH /api/v1/shop/agent-devices/{agentId}/revoke — revokes an agent identity and marks its discovered printers as agent-revoked.
- POST /api/v1/agent/enroll — consumes a single-use code and returns a one-time secret. The server stores only the secret hash.
- POST /api/v1/agent/heartbeat — records a paired agent's last contact.
- POST /api/v1/agent/printers/sync — upserts Windows printer queues for the authenticated agent and marks missing queues unavailable.

Agent authentication uses a per-computer random secret over HTTPS. The Windows client stores the secret using DPAPI LocalMachine encryption. A revoked computer cannot continue to sync. Revoked fingerprints may be enrolled again after revocation; a second active pairing for the same machine/shop is blocked.

The first agent release is inventory and heartbeat only. It does not submit print jobs, capture payments or infer that a spooler-accepted job has physically completed.


## Shop-owned print payments

Print payment records are tenant-scoped and linked to the originating print job through a composite foreign key. Each job may have only one active payment attempt at a time. Shop payment settings contain the shop's own UPI payee ID/name and whether cash at counter is enabled. These are separate from Smart Print platform subscription billing.

- GET/PUT /api/v1/shop/payment-settings — read/update shop UPI and cash options.
- GET /api/v1/shop/payments — list the latest 100 payment attempts for the authenticated shop.
- POST /api/v1/shop/payments/{paymentId}/verify — owner/manager marks a UPI reference verified or cash physically received. This is an explicit manual action.
- POST /api/v1/shop/payments/{paymentId}/reject — owner/manager rejects an active attempt with a reason.
- POST /api/v1/public/jobs/{jobId}/payments — customer starts an enabled UPI or cash attempt after shop approval.
- POST /api/v1/public/payments/{paymentId}/report — customer submits a UPI reference/UTR. This never marks the payment as paid.

UPI uses a shop-owned UPI ID and payment-intent URI/QR. The platform does not collect customer funds, call a payment gateway, or trust client screenshots/references as proof. The shop must independently verify receipt in its own bank/UPI app. When verification succeeds, the job becomes PAID_AUTHORISED, but physical printing remains disabled until the durable local queue and spooler reconciliation workflow is implemented. Smart Print SaaS subscription payments are independent.


## Customer session recovery

- POST /api/v1/public/sessions/current/resume — accepts the existing X-Print-Session token plus the QR station's public code. It resumes only an unexpired, active session belonging to that same enabled station and tenant, and returns the same session token, shop identity, payment options and assigned printer.
- If the stored token is expired, revoked, belongs to another station, or the QR station has been disabled, the customer app discards it and creates a fresh session through the normal QR entry route.
- The customer app stores only the opaque active session token and print-job metadata/quote in sessionStorage for the current browser tab. Uploaded document bytes remain on the API's protected storage; they are not copied into browser storage. On reload, the app re-reads job status, timeline and payment status from the API rather than trusting cached payment state.
- Session storage is tab-scoped and is not a permanent customer account or cross-device recovery mechanism. The existing customer session expiry remains enforced.


Payment verification, rejection and UPI reference submission use an optimistic concurrency token on each payment row. If two customer/shop actions race, only the first update can persist; the losing request receives HTTP 409 with `payment_state_changed` and must refresh. Payment state, job state and the corresponding audit event are saved together for shop verification/rejection.


## Agent local queue handoff

After shop-side manual payment verification, an owner/manager assigns the job to a printer queue reported by an online paired Windows agent.

- PATCH /api/v1/shop/print-jobs/{jobId}/printer — assigns a paid/authorised job to an active printer queue on a paired, recently online agent. An expired local-queue claim can be reassigned; a job already secured locally cannot be silently moved.
- POST /api/v1/agent/jobs/claim — an authenticated agent claims up to five paid jobs assigned to one of its own live queues. The server grants a three-minute lease and records the claiming agent.
- GET /api/v1/agent/jobs/{jobId}/document — streams the decrypted original only to the paired agent holding the active job lease. Responses are non-cacheable and carry the source SHA-256 and size.
- POST /api/v1/agent/jobs/{jobId}/received — confirms the local agent has stored the exact original (matching the server SHA-256 and byte count). This acknowledgement is idempotent for the same agent.

The Windows agent stores the document encrypted with DPAPI LocalMachine under its protected ProgramData queue directory. Its manifest tracks ACK_PENDING and ACKNOWLEDGED so an interrupted acknowledgement is retried after restart. Temporary and orphaned files are cleaned during service startup. The cloud status changes to LOCAL_QUEUE_READY only after the agent confirms verified local storage.

A secured local copy is not proof of spooler acceptance or paper output. Physical spooler acceptance is still distinct from actual paper completion; only a shop operator can confirm output after inspecting the sheets.


## Physical printing and output reconciliation

Physical printing is a local Windows Agent action, never a cloud-to-printer command.

- POST /api/v1/shop/print-jobs/{jobId}/dispatch — owner-authorised action available only after verified payment, secure local receipt, and a live assigned queue. It changes LOCAL_QUEUE_READY to PRINT_AUTHORISED and creates a stable per-job spooler document name.
- POST /api/v1/agent/jobs/print-claim — the paired agent proves the locally stored SHA-256/size and claims only explicitly authorised jobs for its own live queue. The five-minute lease is re-claimable by that same agent only when expired and its local manifest still proves no submission started.
- POST /api/v1/agent/jobs/{jobId}/spooler-accepted — records Windows spooler job ID/name after GDI EndDoc succeeds. The stable document name supports restart reconciliation.
- POST /api/v1/agent/jobs/{jobId}/spooler-status — records queue status and page counters. Spooler status never automatically changes the job to COMPLETED.
- POST /api/v1/agent/jobs/{jobId}/print-blocked — returns a failure known to have occurred before StartDoc to LOCAL_QUEUE_READY so the shop can correct the issue and explicitly retry.
- POST /api/v1/agent/jobs/{jobId}/output-unconfirmed — records a post-submission uncertain outcome and permanently blocks automatic resubmission.
- POST /api/v1/shop/print-jobs/{jobId}/confirm-output — owner confirms the actual paper output only after inspecting the physical pages; then the job becomes COMPLETED.

The Windows Agent rasterises PDFs with PDFium and JPEG/PNG with SkiaSharp, validates A4/A5/Letter/Legal paper availability, colour and duplex support against the installed driver, and uses Windows GDI/Print Spooler. Requested copies are expanded in software as collated copies so the driver cannot multiply them a second time. Unsupported settings are blocked before StartDoc.

The durable local manifest is written as SUBMITTING before the first spooler call. After an agent restart it searches the spooler by the stable document name. If a matching spooler job exists it records the existing job; if the outcome cannot be proven, the job is marked OUTPUT_UNCONFIRMED. The system never automatically submits again after a submission may have begun.

Spooler acceptance or disappearance from the Windows queue is not proof of physical paper output. Actual hardware behaviour still requires validation against the shop's specific printer models and installed drivers.
