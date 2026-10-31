# Delivery Roadmap and Acceptance Criteria

## Workstream 0 — repository and engineering foundation

- Establish monorepo layout, formatting, linting, type checking and CI.
- Add shared UI tokens/components and responsive app shell.
- Establish API contracts, configuration and secret-handling conventions.
- Add architecture decision records and local developer setup.
- Keep main buildable after each integrated increment.

Acceptance: clean checkout can install, build and run the initial web shell; CI executes reproducibly.

## Workstream 1 — SaaS and tenant foundation

- Tenant/shop registration and lifecycle.
- User identity, membership, roles and sessions.
- Plans, subscription state, entitlements and configurable limits.
- Device/agent registration, revocation and signed licence.
- Platform admin tenant and subscription views.

Acceptance: automated tests prove strict tenant isolation and platform/shop permission boundaries.

## Workstream 2 — shop operations

- Shop profile, branding, pricing and payment settings.
- Printer registry, QR stations and operator roles.
- Job queue, status history, reports and audit.
- Responsive shop portal with complete light/dark UI.

Acceptance: an operator can configure a shop, station and pricing, inspect jobs and perform permitted actions.

## Workstream 3 — customer PWA

- [x] QR session creation, expiry, renewal, revocation and customer keepalive.
- [x] Single PDF/JPG/PNG upload with signature checks, SHA-256, page analysis and 20 MB application limit.
- [x] Direct shop-LAN upload to the paired Agent using a short-lived one-use grant; document bytes do not traverse cloud storage.
- [x] Cloud-assisted protected upload remains available as a selectable fallback.
- [x] Server-calculated quote, customer quote acceptance, shop review and customer job tracking.
- [x] Shop-configured UPI/cash reporting with manual shop payment verification.
- Multi-file, Office formats, complete customer receipt/collection and issue-report workflow.

Acceptance: both transfer modes use one server-owned job lifecycle; no client-trusted price or payment state. LAN mode must be verified on representative real Android/iOS browsers and shop Wi-Fi networks before it is treated as universally compatible.

## Workstream 4 — local Windows edge and printer integration

- [x] Windows Service, paired identity, DPAPI LocalMachine credentials and authenticated cloud heartbeat.
- [x] LAN endpoint discovery and private-profile firewall rule on TCP 17842.
- [x] Read-only local health endpoint and direct multipart document upload with exact-origin CORS.
- [x] Shared PDF/JPG/PNG document analysis, SHA-256 verification and DPAPI-protected local document queue.
- [x] Staged-upload reconciliation and expiry cleanup; paid-job claim refreshes settings before queue acknowledgement.
- [x] Windows spooler submission, status reconciliation and explicit shop output confirmation.
- Tray companion, signed installer/auto-updater, additional IPP adapter and richer device diagnostics.

Acceptance: automated tests cover staging, same-printer binding, restart recovery and duplicate submission prevention. Physical hardware matrix and mobile-browser Local Network Access behaviour are documented and tested before compatibility claims.

## Workstream 5 — cloud control plane and cloud-assisted transfer

- [x] Protected cloud upload and retention as an alternative transfer mode.
- [x] Tenant/station-bound job routing and paired-Agent retrieval.
- [x] Server-calculated quote, quote expiry/acceptance and shop approval.
- [x] UPI/cash print-payment attempt with manual shop payment verification before Agent dispatch.
- Separate customer-print and SaaS-subscription payment provider adapters.
- Automated signed gateway webhook verification, subscription invoices, refund and reconciliation lifecycle.

Acceptance: cross-tenant retrieval is impossible; payment replay/idempotency and failed-transfer cases are tested. Manual payment verification must not be described as automated gateway verification.

## Workstream 6 — platform administration and operations

- [x] Dedicated platform-admin identity, offline bootstrap, and separate authorization policy.
- [x] Read-only platform overview and paginated tenant directory with subscription, printer, agent and recent usage summaries.
- [x] Versioned plan catalogue with configurable prices and entitlement fields; published versions remain immutable.
- [x] Enforce assigned-plan printer, agent, QR station, monthly-job, document-size and cloud-assisted-printing entitlements at API boundaries.
- [x] Platform-admin subscription directory and audited assignment of trial/explicitly free published plan versions.
- [x] Manual paid-plan activation after the platform operator confirms funds received and records the bank/UPI transaction reference in the immutable assignment audit.
- Subscription invoices, automated paid-plan checkout/webhook verification, platform payment reconciliation and revenue analytics.
- Device/licence management, white-label upgrades and coupons.
- Support workflow, audit logs and release management.

Acceptance: platform owner can manage SaaS lifecycle without direct database edits. The platform console supports plan metadata, editable drafts, immutable publication history and centrally managed entitlement fields. The API enforces assigned-plan quotas at resource creation and upload boundaries. Trial/free and operator-confirmed paid assignments are audited; automated gateway checkout, subscription invoices and payment reconciliation remain future work.

## Workstream 7 — production readiness

- Security review, dependency scanning and threat-model review.
- Backup/restore drills, retention/deletion and incident procedures.
- Signed Windows installer, update and rollback.
- Performance, accessibility, visual regression and responsive testing.
- End-to-end test matrix and representative physical printer validation.
- Domain/DNS/TLS deployment and monitoring.

Acceptance: all critical workflows pass automated tests; deployment and rollback are rehearsed; hardware/payment claims match actual validation.

## Explicit non-goals for initial release

Play Store apps, universal support for every printer feature, advanced OCR/AI document editing, complex finishing without confirmed devices, franchise management and loyalty programmes.
