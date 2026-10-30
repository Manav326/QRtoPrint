# Architecture

## 1. Architectural approach

Use a modular monolith for the cloud control plane and a local edge runtime for print execution. Keep clear module boundaries and contracts; do not introduce microservices until measurable operational needs justify them.

### Proposed technology baseline

- Web/PWA and shop portal: React + TypeScript + Vite, shared component/design packages.
- Cloud API: ASP.NET Core on .NET 10, modular application layers and versioned REST endpoints.
- Cloud database: PostgreSQL with tenant-scoped data access and migrations.
- Local Windows Agent/edge runtime: .NET 10 Worker Service with a local authenticated HTTP endpoint and tray companion.
- Local durable state: SQLite for job leases, event journal, printer snapshots and recovery metadata; files stored outside the database in tenant/shop-scoped protected directories.
- Contracts: versioned OpenAPI plus shared, generated TypeScript client types where practical.
- CI: build, lint, typecheck, unit tests, API integration tests, dependency/security checks and packaging validation.

This is the initial engineering baseline and may be adjusted only with a documented technical reason before implementation spreads across modules.

## 2. Logical components

### Cloud control plane

- Identity and tenant membership
- Tenant provisioning and shop configuration
- Subscription, plans, entitlements and licence issuance
- Device/agent registration and signed heartbeat
- QR station registration and session authorisation
- Job metadata and cloud-assisted upload coordination
- Payment provider integration and webhook verification
- Pricing/quote policy and immutable quote snapshots
- Audit, notifications, support and release metadata

### Local edge runtime

- LAN customer upload endpoint
- QR session validation/cache and rate limits
- Local document staging and preview/analysis
- Durable local job queue and state journal
- Printer discovery and capability registry
- Printer adapter execution through Windows spooler/IPP where supported
- Cloud synchronisation and cloud-assisted job retrieval
- Local diagnostics, backup and retention

### Web clients

- Customer PWA
- Shop operations portal (desktop-first, responsive)
- Platform-owner admin portal
- Shared UI, API client, auth/session and telemetry packages

## 3. Multi-tenancy

Every tenant-owned record must carry a tenant identifier or be reachable only through a tenant-scoped parent. Enforce tenant scope in the API/application layer and database constraints where possible. Never infer tenant from a client-supplied tenant ID alone; derive it from authenticated membership/device claims and validate requested resources against it.

Tenant-owned data includes users/memberships, stations, printers, documents, jobs, quotes, payments, refunds, customers, branding, pricing and audit events. Platform-level records are explicitly marked as platform scope. Cross-tenant reporting uses controlled aggregate queries, not unrestricted tenant table access.

Add automated isolation tests for list, read, update, delete, file download, job retrieval, payment webhook mapping and agent polling.

## 4. Print job orchestration

Use an explicit state machine with optimistic concurrency/version numbers and idempotency keys. Each physical submission receives a unique submission attempt ID and spooler correlation metadata. Persist intent before external side effects. On crash/restart, reconcile with spooler state when possible; if physical output is uncertain, mark OUTPUT_UNCONFIRMED and require operator action. Never blindly replay.

Cloud and local state synchronisation must be event-based and idempotent. Define conflict policy per entity; printer/job execution state is authoritative at the local edge after a job has been leased for physical execution.

## 5. Printer abstraction

Define a provider-neutral capability model and adapter contract:

- Discover and identify device/queue.
- Read capabilities and current status with an explicit source/freshness timestamp.
- Validate requested print settings against capabilities.
- Submit a prepared print artifact with a stable attempt ID.
- Query/correlate spooler job state.
- Pause/resume/cancel only when supported.
- Report unsupported/unknown states without inventing values.

Initial adapters: Windows Print Spooler (installed vendor driver), IPP where supported, and a generic network discovery layer. Add model-specific adapters only when a real device requires them. Do not claim toner, paper, jam or physical completion status unless the device/driver actually exposes it.

## 6. Local and cloud-assisted data flow

Local mode: customer browser → QR-bound local session → local edge upload → local validation/quote → customer approval/payment policy → local durable queue → printer adapter.

Cloud-assisted mode: customer browser → authenticated tenant/station session → protected cloud upload → server-side validation/quote/authorisation → agent lease/pull → verified local staging → local queue → printer adapter.

The agent authenticates with a device credential and short-lived access tokens. It polls/streams only jobs for its tenant and registered station. Cloud files are encrypted in transit and at rest, have short-lived access grants, quotas and retention policies.

## 7. Payment boundaries

Use separate domain modules, credentials, ledgers and reconciliation for:
1. Customer-to-shop print payments.
2. Shop-to-platform SaaS subscription payments.

Never route shop print proceeds through the platform subscription ledger. Gateway account onboarding and provider capabilities must be confirmed before selecting a concrete payment implementation.

## 8. Offline and licence behaviour

Issue signed, time-bounded licence/entitlement documents to registered devices. Define configurable offline grace after product/legal review; initial design target is 7–14 days, not a hard-coded promise. Already-authorised local jobs must be able to finish during an internet outage. Expiry must not delete shop data or terminate a physical job mid-sheet. Restrict new premium operations only according to explicit grace and entitlement policy.

## 9. Deployment and domain

Target thinkwithsujeet.in with separate customer, shop, admin, API and release subdomains. Use TLS, secure cookies, strict CORS, HSTS and environment-specific secrets. DNS/hosting are deployment tasks and are not assumed configured.

## 10. Observability

Use structured logs with correlation IDs, tenant-safe redaction, health endpoints and metrics for queue age, job transitions, agent heartbeat, printer status freshness, upload failures, payment webhook processing and sync lag. Never log document contents, payment secrets or raw access tokens.
