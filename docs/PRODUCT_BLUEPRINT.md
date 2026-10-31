# Product Blueprint

## 1. Product definition

QRtoPrint is a commercial QR-based printing platform. Independent print shops subscribe to Smart Print SaaS; their walk-in and remote customers use a mobile-first web experience to submit and pay for print jobs. A local Windows Print Agent executes authorised work through the shop's installed printer drivers and supported network protocols.

## 2. Commercial model

- Primary paying customer: an individual printing shop subscribing monthly or annually.
- Standard edition: Smart Print platform identity plus the shop's own name, logo, colours, and contact information.
- White-label edition: paid entitlement that removes or replaces platform branding, subject to plan and contract.
- Shop printing revenue: customer print payments settle to the shop's own payment account.
- Platform revenue: SaaS subscriptions, white-label upgrades, additional printer/device entitlements, custom domains, integrations, onboarding, premium support, and agreed enterprise services settle to the platform owner.
- These two payment flows, ledgers, invoices, refunds, and reconciliation processes must remain separate.
- Previously discussed plan prices are provisional only. Pricing and entitlements must be configurable by the platform owner, not hard-coded.

## 3. Product surfaces

1. Customer PWA: QR session, upload, document validation/preview, print configuration, quote, payment, tracking, receipt, collection and issue reporting.
2. Shop portal/desktop: dashboard, print queue, printers, QR stations, pricing, payment configuration, customers, reports, users, settings and diagnostics.
3. Windows Print Agent: Windows service plus tray companion, local durable queue, printer discovery, job execution, spooler monitoring, restart recovery, secure cloud connection and diagnostics.
4. Platform administration: tenant lifecycle, plans, subscriptions, entitlements, platform billing, licences/devices, releases, support, audit and aggregate analytics.
5. Shared backend: tenant-aware identity, policy, pricing, job orchestration, payment records, document metadata, secure cloud transfer and audit.

## Android companion

The native Android app complements the customer PWA for walk-in customers who prepare documents before reaching a shop. It provides QR scanning, an on-device folder-based document library, Android File Manager import/export and a native saved-file chooser inside the existing customer portal. It reuses the same `/s/{publicCode}` route, station-bound QR sessions, API upload grants, quote, approval, payment and job tracking. The Android app does not replace the Windows Print Agent or provide offline printing.

## 4. Customer access modes

### Local Wi-Fi

The intended end-state local mode keeps document transfer and print execution on the shop network. **Currently implemented slice:** the phone obtains a one-use grant from the cloud API using the live QR session, sends the document directly to the paired Agent, and the Agent validates and stores the file locally. The cloud retains job/document metadata and remains authoritative for quote, approval, payment verification and explicit dispatch. Therefore current LAN upload avoids cloud document storage but does not provide offline quote/payment/dispatch.

### Cloud-assisted

A customer scans a QR or opens the shop portal over mobile data or any internet connection. Files are uploaded to a protected cloud transfer area. The shop's authenticated agent retrieves only jobs authorised for its tenant and station. Cloud transfer/storage must have explicit quotas, retention, encryption, and metering.

Both modes share one job model, server-side price engine, approval policy, payment state machine and audit trail. The local mode must not become a separate product.

## 5. Document intelligence

Use deterministic processing first; no paid AI service is required for core printing.

- Validate extension, MIME type, file signature, integrity, size, page count and malware-scanning result where configured.
- Convert supported Office/image documents to a stable printable representation before final approval.
- Render page previews and inspect page dimensions, orientation, blank-page likelihood and colour content.
- Detect likely duplicate uploads but never silently delete.
- Recommend paper, orientation, scaling, duplex and colour mode based on document and printer capabilities.
- Preserve source bytes and page order. Any derived print rendition must be traceable to the original.
- Show the customer the final settings and price before authorisation.
- Do not silently alter content or increase the quote.
- Mixed colour documents require a deliberate page/sub-job strategy; preserve ordering, collation and copy semantics.

Initial candidate formats: PDF, JPG, PNG, and common Office formats, subject to tested conversion support.

## 6. Print configuration

Support only capabilities actually advertised by the selected printer/driver: page ranges, copies, paper size/type, orientation, colour/mono, single/duplex, pages per sheet, scaling, quality, and collation. The UI must hide or disable unsupported settings with an explanation.

## 7. Quote and payment

The server/local trusted pricing service calculates the quote from shop-configured rules, printer capabilities, pages/impressions, copies, paper, duplex and optional finishing. Never trust client-supplied totals. Persist an immutable quote snapshot with currency, tax treatment, rule version and expiry.

Payment methods are shop-configured. Gateway payment status must be verified server-side using signed webhooks/API verification. A screenshot or client callback is not proof of payment. Cash/counter payment is an explicit shop policy and must be audited.

## 8. Job lifecycle

Suggested state model:

UPLOADED → VALIDATING → ANALYSING → AWAITING_CUSTOMER_APPROVAL → AWAITING_PAYMENT / COUNTER_PAYMENT_PENDING → AUTHORISED → QUEUED → SUBMITTING_TO_SPOOLER → SPOOLER_ACCEPTED → PRINTING (only when reliably observable) → COMPLETED or OUTPUT_UNCONFIRMED.

Exceptional states include REJECTED, CANCELLED, PAUSED, FAILED, REFUND_PENDING, REFUNDED and PARTIALLY_COMPLETED. State transitions must be validated server-side and recorded in an append-only event history.

Spooler acceptance is not proof of physical output. If the printer cannot provide trustworthy completion status, show OUTPUT_UNCONFIRMED and require operator reconciliation. A restart must never automatically submit an uncertain physical job again.

## 9. Shop operations

- Dashboard: job counts, pages, revenue, pending/failed work, printer health and recent activity.
- Queue: search/filter, inspect, approve/reject, pause/resume, cancel, route to another compatible printer before submission, safely retry only when duplicate-print risk is ruled out, refund and mark collection.
- Printers: discover, register, name, inspect capabilities, set default, test, pause/resume and diagnose.
- QR stations: per counter/printer identity, printable QR artwork, enable/disable, rotate secret, session TTL and activity.
- Pricing: configurable rate cards, minimum charge, media, duplex, taxes/fees, payment policy, upload limits and retention.
- Reports: daily/weekly/monthly sales and volume, printer use, customer history, failures, refunds and operator activity.
- Roles: owner, manager, operator and read-only/reporting access, with audited privileged actions.

## 10. SaaS platform administration

Manage shops/tenants, plans, subscription lifecycle, invoices, platform payment reconciliation, entitlements, device/printer limits, white-label upgrades, coupons, support, release channels, aggregate usage and audit logs. Platform operators must not casually access customer documents; support access is time-limited, purpose-bound and audited.

## 11. Domain and branding

Preferred domain: thinkwithsujeet.in. Proposed subdomains, subject to DNS verification:

- print.thinkwithsujeet.in — customer portal
- shops.thinkwithsujeet.in — shop portal
- admin.thinkwithsujeet.in — platform administration
- api.thinkwithsujeet.in — API
- downloads.thinkwithsujeet.in — release delivery

The standard brand combines Smart Print identity and tenant identity. White-label is a separately controlled entitlement. Tenant themes must be constrained by accessibility and contrast rules.

## 12. Initial release boundaries

V1 includes multi-tenancy, SaaS billing foundation, customer flow, shop operations, Windows agent, multi-vendor printer abstraction, local and cloud-assisted access, shop payment integration boundary, platform admin, security, backups, installer/update flow and automated tests.

Defer Play Store apps, advanced AI/OCR, universal manufacturer-specific features, advanced finishing without confirmed hardware, franchise workflows and loyalty features for print customers.


## Implemented V1 boundary

The blueprint above records the intended product, not every feature currently shipped. The present implementation is narrower:

- Customer uploads are single-file PDF/JPG/JPEG/PNG, with a 20 MB hard limit and a 500-page PDF limit. The customer chooses Shop Wi-Fi (direct phone-to-Agent) or Cloud-assisted (encrypted API storage).
- Direct Shop Wi-Fi keeps document bytes on the shop network, but still uses the cloud API for QR-session validation, one-use grant, metadata finalisation, quote, payment and dispatch authorisation. It is not an offline quote/payment/printing mode.
- Both modes share one PrintJob, quote, approval, payment and spooler lifecycle. Shop UPI/cash payment is manually verified; gateway webhooks, automated refunds and payment reconciliation are not implemented.
- The Windows Agent runs as a Windows Service on the printer-connected shop PC. Its current durable queue uses DPAPI-encrypted files and JSON manifests, not SQLite. A tray UI, signed installer and automatic updater remain outstanding.
- Printer compatibility must be verified per model and installed driver. Windows spooler acceptance is not proof of paper completion; shop-side output confirmation remains required.
