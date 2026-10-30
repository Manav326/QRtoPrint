# Security and Operations

## 1. Tenant and identity security

- Derive tenant scope from verified user membership or registered device identity.
- Enforce tenant checks for every API operation, file, job, printer, payment and report.
- Use role-based permissions for owner, manager, operator and reporting roles.
- Require strong authentication for shop owners and platform administrators; support MFA.
- Apply rate limits and abuse controls to QR sessions, uploads, login, quotes and payment callbacks.
- Audit role changes, refunds, overrides, retries, data exports and support access.

## 2. QR session security

A printed QR identifies a shop station; it does not authorise printing by itself. On scan, issue a short-lived, random, scoped session bound to tenant/station and validated by the local edge or cloud. Support expiry, revocation, rotation, abuse limits and replay protection. Do not embed long-lived secrets or customer data in static QR codes.

## 3. Document and file security

- Allowlist file formats and validate signatures, not only extensions.
- Enforce file size, page count, decompression and processing-time limits.
- Store files outside public web roots; use opaque identifiers and short-lived authorised access.
- Encrypt cloud transfer and storage; protect local files with Windows ACLs and shop-scoped directories.
- Scan uploads where configured and isolate conversion/rendering processes.
- Apply explicit retention and deletion policies. Never log document contents or public file URLs.
- Prevent path traversal, archive bombs, malicious Office content and unsafe preview rendering.

## 4. Payments and financial integrity

- Verify payment status server-side using signed webhooks and/or provider APIs.
- Enforce idempotency for order creation, capture, refund and webhook processing.
- Store immutable quote, tax, currency and payment snapshots.
- Separate customer print payment flows from SaaS subscription billing.
- Reconcile gateway transactions and internal ledgers; never accept a client success screen as proof.
- Refunds and manual cash overrides require permission and audit trail.

## 5. Physical print safety

- Persist job intent and submission attempt before spooler side effects.
- Never automatically resubmit a job whose physical output is uncertain.
- Distinguish queued, submitted, spooler accepted, printing, completed and output unconfirmed.
- Operator retry must explain duplicate risk and require explicit confirmation where appropriate.
- Do not claim physical completion when the printer only confirms spooler acceptance.

## 6. Offline, backup and recovery

- Local authorised jobs should continue during temporary internet outages.
- Local queue and event journal must be durable across process and machine restart.
- Define recovery and reconciliation for interrupted uploads, conversion, payment, spooler submission and sync.
- Provide encrypted backup/restore for local configuration and metadata; document customer document retention separately.
- Subscription expiry must not delete tenant data or interrupt a physical job already in progress.
- Offline grace duration and restrictions are configurable product policy, not a hidden constant.

## 7. Privacy and support

Platform support access to tenant records must be least-privilege, time-bound, purpose-bound and audited. Document access should be exceptional and consent/contract governed. Retention, deletion, export and incident response must be documented before production launch.

## 8. Release operations

- Sign Windows installers and agent updates.
- Use staged release channels and rollback-capable updates.
- Verify update signatures before installation.
- Preserve queue state and local configuration during upgrade.
- Publish checksums and release notes.
- Provide diagnostics bundle export with secrets and document contents redacted.
