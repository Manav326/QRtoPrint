# SmartPrint live environment QA

The manually triggered GitHub Actions workflow is:

- Workflow file: .github/workflows/live-environment-qa.yml
- Runner script: scripts/qa/live-environment.mjs
- Workflow name: **SmartPrint Live Environment QA**

It targets a deployed SmartPrint environment through its public HTTPS origin. It does not deploy code or change the target environment.

## Required repository Actions variable

In GitHub, open **Settings → Secrets and variables → Actions → Variables** and create:

| Name | Example value | Required |
|---|---|---|
| SMARTPRINT_LIVE_QA_BASE_URL | https://qr-test.paintpulp.com | Yes |

Enter only the HTTPS origin. Do not include /shop, /api, a query string, credentials, or a fragment. The workflow fails its preflight checks if the variable is absent or is not a valid HTTPS origin. No production URL is baked into the workflow.

## Optional Actions secrets

Create these only for the optional suites you intend to run. Use dedicated QA identities, not a personal or production administrator account.

| Secret | Used when |
|---|---|
| SMARTPRINT_LIVE_QA_SHOP_EMAIL | check_shop_workspace is enabled |
| SMARTPRINT_LIVE_QA_SHOP_PASSWORD | check_shop_workspace is enabled |
| SMARTPRINT_LIVE_QA_ADMIN_EMAIL | check_platform_admin is enabled |
| SMARTPRINT_LIVE_QA_ADMIN_PASSWORD | check_platform_admin is enabled |
| SMARTPRINT_LIVE_QA_STATION_PATH | check_customer_station is enabled; path only, e.g. /s/QA_STATION_01 |

The station secret must point to a disposable, enabled QA station. Opening that station creates one time-limited customer QR session. It does not upload a document or create a print job.

## Running the workflow

1. Set SMARTPRINT_LIVE_QA_BASE_URL in repository Actions variables.
2. Optionally configure the dedicated QA account/station secrets above.
3. Open **Actions → SmartPrint Live Environment QA → Run workflow**.
4. Select the checks to execute:
   - check_shop_workspace: sign in with the QA shop account and visit all 11 shop navigation sections. This is read-only.
   - check_platform_admin: sign in with the dedicated platform-admin QA account and visit all 5 platform navigation sections. This is read-only.
   - check_customer_station: open the dedicated QR station and verify that a temporary customer session can be created.
5. Run the workflow and inspect its step summary and the smartprint-live-environment-qa-* artifact.

The default run checks the public site, health endpoints, database readiness, unauthenticated API authorization, unknown API route handling, release metadata endpoint, and public sign-in/registration routes. Optional account checks are skipped unless explicitly selected.

## What the workflow verifies

- The API liveness endpoint returns the expected SmartPrint API identity.
- The readiness endpoint confirms that the API can connect to PostgreSQL.
- The platform overview endpoint denies an unauthenticated request with HTTP 401 or 403.
- An unknown API endpoint returns HTTP 404.
- The public release metadata endpoint responds successfully or reports its documented retryable 503; a 503 is surfaced as a warning.
- The landing, shop login, legacy login, registration and platform-admin login routes render in a real Chromium browser.
- Browser navigation stays on the configured HTTPS origin, loaded assets do not use cleartext HTTP, and the tested pages do not produce uncaught browser exceptions or same-origin HTTP 5xx responses.
- When enabled, dedicated QA accounts can authenticate and open every navigation section in their workspace.
- When enabled, a dedicated public QR station can create a time-limited customer session.

The workflow does **not** register a shop, change shop settings, create a print order, upload a customer document, start or verify a payment, record a refund, dispatch a print job, or send anything to a physical printer. The only intentional live write is the temporary QR session when the station check is explicitly enabled.

## Evidence

The workflow uploads an artifact containing:

- report.json: structured pass/fail/skipped results and timings.
- screenshots/: screenshots of public and optional authenticated routes.
- traces/: Playwright traces for optional authenticated shop/admin checks.

The same result summary is written to the GitHub Actions run summary. Passwords and email addresses are not written to the report.

## Important limits before production

A green hosted-runner result is useful deployment evidence, but it is not a production-readiness certificate.

A GitHub-hosted runner is outside the shop's private Wi-Fi. It cannot validate:
- Android browser Local Network Access permission prompts on real phones.
- Phone-to-Agent direct LAN transfer, private IP reachability, router client isolation, or the shop firewall.
- Windows service identity/permissions, local disk recovery, Windows spooler behaviour, or a physical printer's output.
- Real-world upload speed, burst concurrency, power loss, broadband interruption, and recovery at a shop.

Those require a shop-floor test using the actual Android devices, router, Windows Agent and printers. A runner installed inside a shop LAN would be needed to automate private-network tests from GitHub Actions.

The repository's production-readiness documents also track engineering acceptance items that this public live QA cannot prove, including end-to-end encryption of direct-LAN document bytes, least-privilege Windows Agent service identity validation, malware scanning/parser isolation, encrypted off-host backup and full restore rehearsal, PostgreSQL concurrency testing, local-Agent disk-loss recovery, and real physical-device/printer validation. Review docs/ROADMAP.md, docs/IMPLEMENTATION_TRACKER.md and docs/SECURITY_AND_OPERATIONS.md before authorising production launch.

A QA workflow passing means the selected checks passed against the configured target at that time. It does not mean that every production risk has been eliminated.
