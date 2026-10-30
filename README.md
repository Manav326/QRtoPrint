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

This repository is being initialized from an empty baseline. The architecture and delivery plan are documented under docs/. Implementation must proceed as one integrated product, not disconnected demos.

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
