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

1. Checks Node.js 22.6+, Docker Desktop and Docker Compose.
2. Reuses the existing local .env.local and persistent volumes. Both scripts explicitly pin Docker Compose to project `qrtoprint-local` with `--project-name`, overriding any inherited `COMPOSE_PROJECT_NAME`; SmartPrint cannot reuse the `quantaedge-site` project namespace. The bootstrap never uses `--remove-orphans` and the runner never runs `docker compose down -v`.
3. Verifies localhost port 3000 is not held by another Docker Compose project (and never stops foreign containers), rebuilds the current portal/API containers under the isolated `qrtoprint-local` project name, checks the portal at `127.0.0.1:3000`, and probes the private API `/health/ready` endpoint from inside the API container. API host port 8080 is not published.
4. **Before starting the long browser suite**, checks that running API and portal container image IDs match the images just built, API liveness succeeds directly and through Nginx, a preflight accepts the exact `http://127.0.0.1:3000` origin, and the compiled portal contains no Google Fonts network references. A failed prerequisite stops before QA account bootstrap or Playwright and records a diagnostic log.
4. Creates or reuses four clearly labelled QA identities: two shop owners, one global customer and one platform administrator. Shop/customer registration uses the real app registration path; the platform administrator uses the existing protected one-time bootstrap command in the local API container. The bootstrap refuses to elevate a shop user.
5. Stores generated passwords in .secrets/smartprint-qa/qa-accounts.dpapi.json, protected with Windows DPAPI for the current Windows user. Passwords are not printed, placed in test reports or committed to Git.
6. Installs/locates the pinned Playwright package and Chromium in the npm cache.
7. Validates the E2E JavaScript syntax and runs the headed Playwright-managed Chromium browser against the real local portal and API. It does not launch or fall back to the system-installed Microsoft Edge.
8. Runs the existing portal logic tests, API/.NET tests and Windows Agent tests when the corresponding SDKs are installed. The destructive PostgreSQL migration test is excluded from the local application database by leaving SMARTPRINT_TEST_POSTGRES unset.
9. If the Microsoft Print to PDF virtual queue is installed, opts in to the Agent's real GDI spooler integration test. That test verifies the Windows virtual printer produced a PDF; it does not send paper to a physical printer.
10. If system Gradle and the Android SDK are installed, runs Android JVM tests and builds a local-portal debug APK. If an Android emulator is already running, it also runs connected instrumentation tests. It never installs the APK on an attached physical phone.

Generated logs, screenshots and JSON/TXT reports are written under `artifacts/smartprint-qa/`; this directory is Git-ignored. QA account passwords remain DPAPI-protected under `.secrets/`.

No extra branch, remote QA secret, Cloudflare rule or manual account setup is required.

The public landing page uses system font fallbacks so local acceptance testing does not fetch Google Fonts or another third-party asset.

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

The suite is scripts/qa/smartprint-local-e2e.mjs. It launches the pinned Playwright-managed Chromium executable in headed mode and records its version and executable path. It never selects the system Edge channel. It saves screenshots and a redacted JSON report.

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

- Generate a real one-time Agent enrollment code and redeem it through the actual `/agent/enroll` endpoint to create a disposable local QA Agent identity with a unique QA fingerprint.
- Authenticate the QA protocol client using the real secret returned by enrollment, send a heartbeat, reject malformed printer discovery metadata, and accept an empty printer-discovery sync. No physical printer is advertised or asserted.
- Verify that an enrollment code cannot be replayed, revoke the disposable QA Agent through the shop endpoint, and prove its credential can no longer heartbeat.
- Probe all 17 protected Agent API routes without credentials and verify they return 401. These are live API auth tests, not mocked routes.
- The runner does not install, pair or reconfigure a Windows Agent service, and it does not touch a physical printer.
- Verify that Shop One cannot access or mutate Shop Two's resources and job history.
- Verify that customer/shop/admin tokens cannot cross their respective scopes.

## Expanded functional acceptance catalogue and 90% gate

The local real-data Playwright runner now carries **255 uniquely identified functional acceptance cases**. Case IDs are grouped under E2E records. At the end of a run, report.json and report.txt show IDs mapped to attempted checks, IDs whose entire owning check completed successfully, IDs associated with failed/incomplete checks, the completion percentage, the pass rate, and IDs not mapped to a reached check.

**Coverage definition:** functional case completion coverage = case IDs belonging to records that completed all their assertions successfully ÷ 255. This is deliberately conservative: when a grouped record aborts on a failed assertion, the runner does not count every case ID attached to that record as fully completed because it cannot prove that the remaining assertions ran. The runner exits with a failure when this coverage is below **90%**; any failed record also fails the run. A release-quality run should complete all 255 cases with 100% of their assertions passing.

This is **not source-code line or branch coverage**. No 90% code-coverage claim is made. Measuring line/branch coverage requires a separate instrumented Coverlet/Node coverage run and generated coverage reports; it must not be inferred from the count of functional cases.

### Case catalogue

| Area | Case IDs | Cases | Coverage intent |
|---|---|---:|---|
| Runtime and environment | ENV-001–004 | 4 | Local target, portal/API health, database readiness, expected local stack boundary |
| Public routes | PUB-001–005 | 5 | Landing, shop sign-in, legacy sign-in, registration and admin sign-in render |
| Responsive public routes | RESP-001–020 | 20 | Four public route surfaces across the five declared viewport sizes |
| Shop registration/sign-in | SHOPAUTH-001–006 | 6 | Two independent real shops, registration/sign-in, wrong password and session continuity |
| Customer identity/auth | CUSTAUTH-001–004 | 4 | Global customer registration, duplicate registration and successful/failed customer authentication |
| Platform-admin authentication | ADMINAUTH-001–002 | 2 | Bootstrap-created administrator sign-in and invalid-password rejection |
| Authentication negative/security lifecycle | AUTH-001–008 | 8 | Invalid/duplicate registrations, cross-role login denial, anonymous route denial, refresh rotation, replay prevention and logout revocation |
| Plans and subscriptions | PLAN-001–006 | 6 | QA free-plan creation, draft save/publish, tenant assignment and persisted subscription state |
| Platform UI and operations | PLATFORM-001–014 | 14 | All control-centre areas, directory/plan/subscription/settings reads, settings validation, audit history and safe no-op save |
| Shop setup and validation | SHOP-001–024 | 24 | Printer/station/rate/payment setup in two tenants; invalid pricing, invalid capabilities, unsupported printer type, foreign-printer binding, insecure logo, required-field, branding and unknown-resource validation; verify rejected writes did not mutate saved state |
| Shop navigation/account tools | SHOPUI-001–027 | 27 | Both shop workspaces, all primary sections, account-menu tools and authenticated responsive navigation |
| Shop reports | REPORT-001–004 | 4 | 7/30/90-day summaries, unsupported range rejection and both tenant report scopes |
| Authorization and isolation | RBAC-001–010 | 10 | Shop/admin/customer role separation, customer/shop/platform cross-role denial, foreign tenant resource access and cross-tenant job-history denial |
| QR station/session lifecycle | QR-001–013 | 13 | Session renewal/resume, session-to-station binding, station enable/disable, rotation invalidates old code and new code can create a session |
| Customer account linkage | CUST-001–002 | 2 | QR print session tied to global customer identity and customer job-history endpoint |
| Local upload validation | UPLOAD-001–004 | 4 | Invalid digest and unsupported extension fail closed before a local upload ticket can be issued |
| Document upload | DOC-001–006 | 6 | Malformed PDF rejected without job creation, real synthetic PDF upload, two-page parsing, quote preparation and saved job metadata |
| Job lifecycle | JOB-001–011 | 11 | Customer quote/submit, shop queue, customer history/status, unpaid rejection and rejection guards against approval/dispatch |
| Batch workflow | BATCH-001–012 | 12 | Two real documents, shared Batch ID, distinct PrintJob IDs, queue/customer states, preview, accept-by-default, undo rejection and individual final decisions |
| Payment workflow | PAY-001–013 | 13 | Payment verification/intent guards, one accepted-file total, synthetic reference idempotency, shop rejection and customer-visible rejected state; no actual money receipt |
| Refund safety | REFUND-001–004 | 4 | Refund preview without verified funds, sent-confirmation requirement and refund-screen/list guard checks; no false refund record |
| Agent lifecycle and protocol security | AGENT-001–033 | 33 | Agent presence/dispatch guard, one-time enrollment, ephemeral QA identity, authenticated heartbeat, printer-sync validation, one-time-code replay, revocation and no-credential denial on every protected Agent endpoint |
| Printer/station controls | DEVICE-001–002 | 2 | QA printer and station disable/restore lifecycle |
| Tenant lifecycle | TENANT-001–010 | 10 | Invalid status rejected, QA tenant suspension blocks normal work, restore is audited and idempotent duplicate state is safe |
| Security guard | SEC-001 | 1 | Unpaid/rejected work cannot cross into a financial refund action |
| Browser and resource diagnostics | OBS-001–004 | 4 | Same-origin runtime errors, console errors, failed resources and HTTP 5xx monitoring |
| Photo Studio | PHOTO-001–006 | 6 | Real browser image selection, crop/look/background tools, optional form details, photo size/count and local multi-photo A4 PDF generation without a server print job |

### Additional negative and lifecycle assertions

The extra cases are intentionally run against QA-only identities and records:

- **Authentication:** the refresh-token test uses a newly authenticated QA shop session, proves the old refresh token cannot be replayed, and verifies logout revokes the replacement token. This block runs after the longer UI/data journeys to reduce the risk of colliding with the API's authentication rate limit during startup.
- **Shop configuration:** each invalid update is followed by a read-back. Rejected rate/printer/station writes must not change the rate-card version or create unintended rows. A station cannot be attached to another tenant's printer.
- **QR/session:** tests prove the opaque session token cannot be read anonymously or resumed against another tenant's QR station. A disabled or rotated station code cannot create a new session; the new rotated code must work.
- **Reports:** Shop One is checked against each supported 7-, 30- and 90-day summary window, an unsupported 14-day window is rejected, and Shop Two reads its own report under its own token.
- **Agent protocol:** a QA identity is enrolled with a one-time code and later revoked. The heartbeat and printer-discovery routes are called against the real local API, and all protected Agent routes reject missing credentials. No local Windows Agent executable or physical printer is represented as paired.
- **Photo Studio:** a synthetic PNG is processed in a separate browser page. The test opens the actual editor, exercises crop/look/background and optional form controls, generates the prepared A4 photo PDF, verifies the preview, and confirms no server-side customer print job was created. The prepared file is not uploaded or sent to the shop.
- **Payment/refund:** test-only payment references are explicitly marked synthetic. A rejected payment cannot be manually verified, an unpaid/rejected job cannot be approved/dispatched, refund preview without verified money is rejected, and the API refuses a refund request whose `confirmedSent` value is false. No real transfer, payment verification, refund, gateway or printer dispatch occurs.
- **Platform/tenant:** only the generated QA shop is suspended; it is restored to Active in the same run. Operational settings are saved with their existing values only to prove the audit trail, so platform policy values are not changed. A duplicate Active transition must be idempotent.

- **Extended authorization and tenant isolation:** positive and negative access checks cover customer-to-shop, shop-to-customer, shop/customer-to-platform denial, plus printer/station/Agent list isolation and foreign-tenant printer/station mutations. Every rejected mutation is followed by a read-back to prove the other tenant's record remains unchanged.
- **Additional request validation:** the suite rejects invalid public station codes, invalid opaque session tokens, missing idempotency keys, malformed upload batch IDs, non-multipart document posts, unknown customer job IDs, unsupported platform directory bounds, invalid shop branding/device fields and malformed/overlong UPI references.
- **Agent authentication:** a real disposable Agent is probed with an incorrect secret, malformed Agent ID, unknown enrollment code and incomplete enrollment metadata before normal heartbeat/sync and revocation tests. These probes do not create another Agent or printer.

### Coverage boundaries that remain separate

The 255-case gate measures the documented local functional catalogue. It does not replace these additional suites or acceptance requirements:

- The portal Node regression suite and the API/Agent .NET unit suites remain separate test results. They provide domain/logic regression coverage beyond the mapped browser/API acceptance cases.
- The Windows Agent executable's full live machine lifecycle—actual service enrollment, local queue claim/receipt, spooler accepted/failed/unconfirmed states, PDF export and output confirmation—is not exercised by the protocol test client. The E2E does exercise the server's real one-time enrollment, heartbeat, printer-sync validation, revocation and auth protections with a disposable QA identity. The Agent unit suite and optional Microsoft Print to PDF integration remain separate.
- The Android emulator test suite is conditional on an installed SDK and an already running emulator. Physical Android camera, permission, local-network and Wi-Fi readiness tests remain in docs/ANDROID_PHYSICAL_ACCEPTANCE.md.
- Real LAN direct upload, Windows service identity, a physical printer, printed paper, power/network interruption recovery, concurrency/load, backup/restore rehearsal and payment-provider settlement require separate dedicated acceptance. They are not counted as passed by this 255-case browser suite.
- No destructive tenant deactivation, data deletion, storage cleanup, real payment, actual refund, permanent printer pairing or physical print is performed by this runner.

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
