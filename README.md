# QRtoPrint — Smart Print Platform

QRtoPrint is a multi-tenant SaaS platform for QR-initiated document printing, combining customer web experiences, shop operations, a local Windows Print Agent, and platform-owner administration.

## Product principles

- Support independent printing shops as the primary SaaS customer.
- Support both local shop-Wi-Fi printing and secure cloud-assisted printing.
- Integrate with multiple printer manufacturers through capability discovery and replaceable adapters.
- Keep print execution local to the shop; the cloud coordinates identity, tenancy, subscriptions, secure job routing, and optional cloud transfer.
- Keep each shop's customers, documents, jobs, payment records, devices, users, and branding isolated.
- Route customer print payments to the shop's own payment account. Route Smart Print SaaS subscription payments to the platform owner. These are separate financial flows.
- Deliver a premium, coherent, accessible UI across customer PWA, shop desktop/web, platform administration, and the Windows agent.

## Preferred domain

thinkwithsujeet.in is the preferred domain. Proposed subdomains (subject to DNS and hosting verification):

- print.thinkwithsujeet.in — customer portal
- shops.thinkwithsujeet.in — shop portal
- admin.thinkwithsujeet.in — platform administration
- api.thinkwithsujeet.in — API
- downloads.thinkwithsujeet.in — desktop/agent releases

## Repository status

The repository now contains an integrated first implementation: a React customer/shop portal, a tenant-scoped ASP.NET Core API, PostgreSQL data model and migrations, a paired Windows Print Agent with protected local document queue and spooler execution, automated unit/model tests, and CI. Shop onboarding, QR sessions, document upload and quoting, shop approval, UPI/cash reporting with manual shop verification, agent handoff, explicit print dispatch, and physical-output reconciliation are implemented. The platform-admin identity, protected read-only operations overview, and tenant directory are now implemented; plan/entitlement editing, SaaS subscription invoicing and reconciliation, automated shop payment-provider verification, LAN-only edge upload, and the validated physical-printer compatibility matrix remain future work. See docs/ROADMAP.md for the delivery plan and acceptance boundaries.

## Documentation

- [Product blueprint](docs/PRODUCT_BLUEPRINT.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Premium design system](docs/DESIGN_SYSTEM.md)
- [Security and operations](docs/SECURITY_AND_OPERATIONS.md)
- [Delivery roadmap and acceptance criteria](docs/ROADMAP.md)

## Delivery principles

- Audit the current state before changing existing functionality.
- Keep the default branch buildable; use focused, reviewable changes.
- Never claim a physical print completed based only on spooler acceptance.
- Never automatically resubmit an uncertain physical print after restart.
- Never trust client-supplied prices or payment-success screenshots.
- Do not claim hardware compatibility until representative physical devices have been tested.
