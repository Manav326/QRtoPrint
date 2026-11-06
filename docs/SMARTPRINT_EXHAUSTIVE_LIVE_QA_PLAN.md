# SmartPrint — local real-data exhaustive QA

This is the execution guide for the Windows QA runner in this repository.

## Testing boundary

The exhaustive acceptance run targets only the local SmartPrint stack:

- Portal: http://127.0.0.1:3000
- API through the portal reverse proxy: http://127.0.0.1:3000/api/...
- Local PostgreSQL and document storage: the persistent volumes attached to compose.local.yml

The test runner refuses any target other than the local loopback portal. Its browser and API requests are same-origin. It does not point the test browser at https://smartprint.quantaedge.in, does not use the production database, and does not use mock API routes, synthetic JWTs or fixture customer sessions.

The Cloudflare Free/Bot Fight Mode 403 issue is therefore irrelevant to this local run. No Cloudflare security changes are required.

## One-command run

From a Windows PowerShell window opened in the local QRtoPrint repository:

~~~powershell
git pull --ff-only origin main
.\scripts\qa\run-smartprint-qa.ps1
~~~

The runner:

1. Checks Node.js 22+, Docker Desktop and Docker Compose.
2. Reuses the existing local .env.local and persistent volumes. If this is the first local setup, it invokes scripts/docker-up-local.ps1 to create local secrets and start the local stack. It never runs docker compose down -v.
3. Rebuilds the current portal/API containers from the source you just pulled and checks /health/ready before tests.
4. Creates or reuses four clearly labelled QA identities: two shop owners, one global customer and one platform administrator. Shop/customer registration uses the real app registration path; the platform administrator uses the existing protected one-time bootstrap command in the local API container. The bootstrap refuses to elevate a shop user.
5. Stores generated passwords in .secrets/smartprint-qa/qa-accounts.dpapi.json, protected with Windows DPAPI for the current Windows user. Passwords are not printed, placed in test reports or committed to Git.
6. Installs/locates the pinned Playwright package and Chromium in the npm cache.
7. Validates the E2E JavaScript syntax and runs the headed Microsoft Edge/Chromium Playwright suite against the real local portal and API.
8. Runs the existing portal logic tests, API/.NET tests and Windows Agent tests when the corresponding SDKs are installed. The destructive PostgreSQL migration test is excluded from the local application database by leaving SMARTPRINT_TEST_POSTGRES unset.
9. If the Microsoft Print to PDF virtual queue is installed, opts in to the Agent's real GDI spooler integration test. That test verifies the Windows virtual printer produced a PDF; it does not send paper to a physical printer.
10. If system Gradle and the Android SDK are installed, runs Android JVM tests and builds a local-portal debug APK. If an Android emulator is already running, it also runs connected instrumentation tests. It never installs the APK on an attached physical phone.

No extra branch, remote QA secret, Cloudflare rule or manual account setup is required.

## Existing test coverage retained

### Portal logic regression tests

The runner executes these repository tests with the Node test runner:

- apps/portal/tests/portal-routing.test.ts
- apps/portal/tests/order-journey.test.ts
- apps/portal/tests/customer-session-state.test.ts
- apps/portal/tests/table-layout.test.ts
- apps/portal/tests/theme-responsive.test.ts
- apps/portal/tests/batch-checkout-state.test.ts
- apps/portal/tests/local-upload-routing.test.ts
- apps/portal/tests/print-fulfilment-flow.test.ts
- apps/portal/tests/photo-studio.test.ts
- apps/portal/tests/upi-payment-intent.test.ts

These are isolated logic tests. They are reported separately from the real-data E2E suite and are not presented as live API tests.

### Existing Playwright UI fixture suite

apps/portal/tests/theme-responsive.browser.mjs remains available for UI component development and mock-fixture regression. It deliberately intercepts API calls; it is not executed by the exhaustive real-data runner and does not count as a real-data acceptance pass.

### Existing live-production smoke suite

scripts/qa/live-environment.mjs remains available for a separate production-origin smoke check. It is not the database-mutating local acceptance suite. GitHub-hosted headless requests may still receive Cloudflare Bot Fight Mode challenges on the Free zone.

## Real-data Playwright journey

The suite is scripts/qa/smartprint-local-e2e.mjs. It launches headed Edge, falling back to headed Playwright Chromium. It saves screenshots and a redacted JSON report.

### Account creation and authentication

- Register two new shops through the real shop registration UI, with unique generated business names and example.invalid email identities.
- Sign out and sign back in through the real shop sign-in UI.
- Verify incorrect shop passwords are rejected.
- Create the global customer in local PostgreSQL through the real customer registration API, then verify invalid and successful customer login.
- Bootstrap a separate platform-admin account in the local API container; verify incorrect password rejection and sign in through the platform-admin UI.
- Capture real access/refresh tokens from the browser's authenticated session storage; never create test JWTs.

The customer QR-entry screen does not have a separate customer sign-in page. The suite creates a real customer identity through the supported registration API, authenticates that identity, then attaches it to the real station-session request made by the customer QR page.

### Platform-admin control centre

- Navigate Overview, Shop tenants, Plans & entitlements, Shop subscriptions and Operations & retention.
- Create or reuse SMARTPRINT_QA_AUTOMATION, a zero-priced, cloud-assisted QA-only plan, through the real platform plan screens.
- Publish a free plan version and assign it to each generated QA shop through the real Grant plan access dialog.
- Read the saved subscriptions and append-only assignment audit history back from the local API.
- Verify that a shop cannot access platform endpoints and a platform admin cannot use a shop-scoped endpoint.

### Shop workspace

Each generated shop opens the real sidebar sections:

- Order Journey
- Overview
- Print Queue
- Customers
- Printers
- QR Stations
- Print Agent
- Pricing
- Payments
- Reports

The suite also opens the account-menu-only Personal details, Shop profile & branding, Trial & subscription and Help & support screens.

For each shop it creates a no-dispatch QA printer record and a dedicated QR station through the actual shop UI. The QA printer has no Agent device attached and its queue name is SMARTPRINT_QA_NO_DISPATCH. It configures a QA-only rate card and the UPI/cash display methods using the real shop forms, then confirms all persisted values through the local API.

### Responsive and theme coverage

- Public landing, shop sign-in, shop registration and platform-admin sign-in pages.
- Both QA shop workspaces and the platform-admin workspace.
- Viewports: 1440×1000, 1024×900, 768×1024, 520×900 and 390×844.
- Light and dark theme in each authenticated workspace.
- Root/body horizontal overflow and visible interactive controls clipped beyond the viewport.
- JavaScript exceptions, same-origin console errors, HTTP 5xx responses and failed same-origin JavaScript/CSS/font resources.

The responsive checks navigate real pages using real local data; they do not install mocked API routes.

### Customer QR, documents and batch review

- Open the generated QA station route on the local portal and create a real server-issued, customer-bound session.
- Verify the real session API and shop binding.
- Confirm that an invalid PDF is rejected with HTTP 422 before creating a print job.
- Upload a synthetic two-page PDF through the actual customer file picker and upload UI.
- Calculate a quote, send it to the shop, verify the customer status/history and shop queue, and confirm the shop can reject an unpaid job.
- Open a second fresh QR session and upload two different synthetic PDFs in one batch through the actual customer UI.
- Verify distinct PrintJob IDs, one Batch ID, both files in the correct shop queue and customer-visible status.
- Open the actual shop batch review modal and preview the first uploaded PDF through the real document-download API.
- Uncheck the second file, re-check it to prove rejection can be undone before applying, then uncheck it again and apply the review.
- Verify that exactly one file is AWAITING_PAYMENT and one file is REJECTED.
- Verify that the rejected file is excluded from the single batch payment total.

### Payment-intent safety and accounting guards

The suite creates one real UPI batch payment intent for the one accepted file. It submits a clearly synthetic reference prefixed QA-NOT-REAL, verifies duplicate-reference idempotency, then rejects the pending reference from the shop and verifies duplicate rejection idempotency.

It deliberately does not:

- transfer money;
- claim the synthetic reference is a real UTR;
- verify or authorise the payment;
- record a refund or say money was returned;
- enable a live payment gateway;
- dispatch the job to an Agent or printer.

The suite also reads payments/refunds, verifies refund preview is blocked without a verified payment, and checks that dispatch is blocked for a rejected/unpaid job.

### Agent and tenant boundaries

- Generate a short-lived Agent enrollment code for the QA shop, but do not pair or revoke a machine identity.
- Verify that Shop One cannot access or mutate Shop Two's resources and job history.
- Verify that customer/shop/admin tokens cannot cross their respective scopes.

## Android and Windows execution

### Android

When system Gradle and Android SDK are installed, the orchestrator:

- runs :app:testDebugUnitTest;
- builds :app:assembleDebug;
- uses http://10.0.2.2:3000 so the Android app targets the local portal from the Android emulator;
- runs :app:connectedDebugAndroidTest only if an Android emulator is already online.

Physical Android devices are not targeted automatically. Camera permission flows, real camera scanning, the phone's document picker, Android Wi-Fi permissions and actual phone-to-shop LAN upload still require device acceptance.

### Windows virtual printer

If Microsoft Print to PDF is installed, the Agent test run enables only SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION=1. This exercises the real Windows GDI → virtual spooler → generated PDF output path. The secondary queue integration flag remains off. This is not a physical printer acceptance test.

A physical Windows Agent paired to the QA shop, actual printer dispatch, printed paper output and the owner's Android-to-shop-Wi-Fi flow still require a dedicated test machine and printer. The script does not reconfigure a real Agent service, change its identity, install/uninstall the desktop app, or print to an ordinary shop printer.

## Safety and repeatability

- All account names, documents, plans, stations, payment references and jobs are explicitly labelled SmartPrint QA.
- All portal, API and database operations from the E2E suite are local-only.
- No existing shop, customer, printer, Agent identity or existing station is intentionally modified.
- A QA plan is zero-priced. No paid plan assignment is made.
- QA payment settings use a non-live placeholder UPI identity and cash method. No payment is verified.
- The two-file batch creates real local records in the local QA tenant. The pending synthetic UPI reference is rejected before payment verification. No refund is recorded.
- The generated QA account password file is DPAPI-protected for the current Windows user and lives in .secrets/, which is gitignored.
- Do not upload the .secrets directory, .env.local, raw tokens, screenshots containing private content or local database files to GitHub.
- Do not aim this runner at the public production URL. Its local E2E script hard-fails unless the origin is loopback on port 3000.

## Evidence and result interpretation

Each run creates:

~~~text
artifacts/smartprint-qa/<run-id>/
  summary.json
  summary.txt
  logs/
  local-e2e/
    report.json
    report.txt
    screenshots/
~~~

A test is PASS only when the real local UI/API operation and its persisted result meet the assertion. Missing local SDKs/devices are SKIPPED; hardware-only acceptance is reported as not performed. A fully green local web/API run does not certify physical Android or printer output. Production release sign-off still requires all applicable items in docs/ANDROID_PHYSICAL_ACCEPTANCE.md and the security/operations checklist.
