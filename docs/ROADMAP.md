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

- QR session creation/expiry/revocation.
- Multi-file upload, validation, preview and document analysis.
- Print configuration constrained by printer capabilities.
- Server-calculated quote and customer approval.
- Shop-configured payment/counter-payment flow.
- Tracking, receipt, collection and issue reporting.

Acceptance: full customer journey works end-to-end with local mode and test payment provider/sandbox; no client-trusted price or payment state.

## Workstream 4 — local Windows edge and printer integration

- Windows service, tray companion and authenticated local endpoint.
- Durable SQLite queue and recovery journal.
- Windows spooler adapter and IPP adapter where supported.
- Capability discovery, status freshness and diagnostics.
- Cloud agent registration, secure polling and sync.

Acceptance: tests cover restart, disconnect, duplicate submission prevention and uncertain output. Physical hardware matrix is documented and tested before compatibility claims.

## Workstream 5 — cloud-assisted printing and payments

- Protected cloud upload and retention.
- Tenant/station-bound job routing and agent retrieval.
- Payment provider adapter boundary and verified webhook flow.
- Separate shop print and platform subscription financial ledgers.
- Reconciliation and refund lifecycle.

Acceptance: cross-tenant retrieval is impossible; payment replay/idempotency and failed transfer cases are tested.

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
