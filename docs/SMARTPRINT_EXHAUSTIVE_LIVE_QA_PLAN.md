# SmartPrint — existing Playwright coverage and exhaustive live acceptance plan

Target under test: `https://smartprint.quantaedge.in` only.

## 1. Existing tests found in `Manav326/QRtoPrint`

### A. Existing live-production Playwright suite

File: `scripts/qa/live-environment.mjs`  
Workflow: `.github/workflows/live-environment-qa.yml`  
Workflow name: **SmartPrint Live Environment QA**

This is the current Playwright suite that targets the configured HTTPS deployment. It runs these checks:

1. API liveness (`/health/live`) and SmartPrint API identity/version.
2. API readiness and PostgreSQL connection (`/health/ready`).
3. Platform API rejects unauthenticated access.
4. An unknown API route returns 404.
5. Release metadata (`/downloads/status`) is healthy or reports the documented retryable 503.
6. Public landing page renders in Chromium and uses HTTPS resources.
7. Shop sign-in screen renders.
8. Legacy `/login` route remains compatible.
9. Shop registration screen renders.
10. Platform-admin sign-in screen renders.
11. Optional: dedicated QA shop account signs in and visits its 11 workspace sections (read-only).
12. Optional: dedicated platform-admin account signs in and visits its 5 sections (read-only).
13. Optional: dedicated station route creates one short-lived customer session.
14. Application and health routes remain on the configured HTTPS origin.

Important limits: the current live suite does not submit registration, upload documents, create print orders, verify payments, refund, dispatch to a printer or confirm physical output. Its optional station test creates a temporary session only. It launches headless Chromium and uses Node's built-in `fetch` for API probes. The Security Events you shared show `HeadlessChrome` and `node` being challenged by Bot Fight Mode, so this existing CI-oriented script is not the preferred runner for the Free-plan production zone. A local Windows headed-browser runner is provided below; it uses Edge/Chromium page-context requests and pauses for a normal interactive Cloudflare challenge if one is shown.

### B. Existing real-browser UI/visual suite (not live production)

File: `apps/portal/tests/theme-responsive.browser.mjs`

This launches the portal and desktop frontends locally. It deliberately intercepts API requests and uses fixture data / synthetic browser sessions. It is not a production-data test. It covers:

- Public landing, shop login, admin login, customer QR entry, shop workspace and admin workspace routes.
- Five viewports: 1440×1000, 1024×900, 768×1024, 520×900 and 390×844.
- Light/dark theme, theme toggle, refresh persistence, route continuity and preservation across sign-out/sign-in.
- Layout overflow, clipped controls, top-bar overlap and route title layout.
- Theme contrast checks for landing, authentication, customer QR and workspace design tokens.
- Shop navigation and admin navigation, including order-journey fixture checks.
- Photo Studio browser component at phone/tablet/desktop sizes.
- Windows desktop frontend at its declared 1000×680 minimum window size.

Because the API is mocked, use it for visual/regression coverage, not for authentication or data correctness against production.

### C. Existing portal logic tests (Node test runner, not Playwright)

The CI workflow runs these 10 files with Node's test runner:

- `apps/portal/tests/portal-routing.test.ts`
- `apps/portal/tests/order-journey.test.ts`
- `apps/portal/tests/customer-session-state.test.ts`
- `apps/portal/tests/table-layout.test.ts`
- `apps/portal/tests/theme-responsive.test.ts`
- `apps/portal/tests/batch-checkout-state.test.ts`
- `apps/portal/tests/local-upload-routing.test.ts`
- `apps/portal/tests/print-fulfilment-flow.test.ts`
- `apps/portal/tests/photo-studio.test.ts`
- `apps/portal/tests/upi-payment-intent.test.ts`

### D. Existing companion suites (not Playwright)

- API: `tests/SmartPrint.Api.Tests/` — .NET tests for tenant boundaries, auth, quotas, batch capacity, quote/pricing, refunds, dispatch, downloads, documents, retention and PostgreSQL migration upgrades.
- Agent: `tests/SmartPrint.Agent.Tests/` — .NET tests for pairing, credential store, local queue, local endpoint discovery, printer capabilities and Windows print execution. CI separately enables its real Microsoft Print to PDF queue integration.
- Android: Gradle unit tests plus API 36 emulator UI tests via `scripts/qa/android-emulator-ci.sh`.
- Windows installer: `scripts/qa/windows-installer-lifecycle.ps1` exercises install/reinstall/upgrade/rollback/uninstall in CI.
- Physical acceptance: `docs/ANDROID_PHYSICAL_ACCEPTANCE.md` has PHY-01 through PHY-12 and a required owner-to-customer test sequence. These are not proven by emulator or Playwright.

## 2. Run the existing and new test suites from Windows

The repository now provides one Windows orchestrator. Pull `Manav326/QRtoPrint` main, open PowerShell in the QRtoPrint repository root, and run:

```powershell
git pull --ff-only origin main
.\scripts\qa\run-smartprint-qa.ps1
```

The default run executes these suites in sequence:

1. **Existing portal logic tests** — the same 10 Node test files listed above, using Node 22's built-in test runner.
2. **Existing responsive/theme Playwright suite** — starts the local portal and desktop frontends with isolated API fixtures. It covers five viewport sizes, light/dark mode, layout overflow, contrast, navigation and Photo Studio. It does not contact production APIs or mutate production data.
3. **New live headed-browser suite** — opens the production SmartPrint site in Microsoft Edge. If Edge is unavailable, it uses headed Playwright Chromium. It checks the public site, API liveness/readiness, PostgreSQL readiness, unauthenticated platform authorization, unknown route behavior, release metadata, cache policy, one-byte range support for available downloads, login/registration/admin entry screens and HTTPS origin consistency.

The orchestrator installs the pinned Playwright 1.56.1 Chromium runtime in npm's temporary cache and runs `pnpm install --frozen-lockfile` for the local visual suite. It does not modify `package.json` or `pnpm-lock.yaml`. On Windows it starts and terminates the local Vite process trees. Results, logs, screenshots, and JSON summaries are placed in a timestamped directory under `artifacts\smartprint-qa\`.

The live headed suite sends API probes from the browser page context. If Cloudflare shows a normal interactive challenge, it waits for you to complete it in the visible browser and press Enter in PowerShell. It does not spoof browser fingerprints, hide automation, or bypass Cloudflare protection.

### Optional authenticated read-only suites

Use dedicated QA identities—not personal or customer accounts. The script prompts for email and securely prompts for the password; credentials are not written to the report.

```powershell
.\scripts\qa\run-smartprint-qa.ps1 -RunShopWorkspace -RunPlatformAdmin
```

This signs in and opens all 12 shop sections (`Order Journey`, `Overview`, `Print Queue`, `Printers`, `QR Stations`, `Print Agent`, `Pricing`, `Subscription`, `Payments`, `Customers`, `Reports`, `Settings`) and all 5 platform sections. It also verifies that a shop token cannot access the platform overview and a platform-admin token cannot access a shop profile. These checks are read-only.

### Optional disposable customer-station check

Only use an enabled **dedicated QA station**. The check creates one temporary customer session; it does not upload a document or create an order.

```powershell
.\scripts\qa\run-smartprint-qa.ps1 -RunCustomerStation -StationPath "/s/QA_STATION_PUBLIC_CODE"
```

You can combine all three options. Do not use an ordinary customer station for this test.

### Run suites separately when investigating a failure

```powershell
# Only the existing portal logic tests and live browser test:
.\scripts\qa\run-smartprint-qa.ps1 -SkipResponsiveBrowserTests

# Only the local responsive/visual suite and live browser test:
.\scripts\qa\run-smartprint-qa.ps1 -SkipPortalLogicTests

# Only the live headed-browser suite:
.\scripts\qa\run-smartprint-qa.ps1 -SkipPortalLogicTests -SkipResponsiveBrowserTests
```

The existing GitHub-hosted `scripts/qa/live-environment.mjs` remains available for CI. It uses headless Chromium and Node `fetch`, so Cloudflare Bot Fight Mode may still challenge that hosted runner. Use the new Windows runner for local public-origin testing without weakening zone security.

## 3. Additional live Playwright cases required

The following cases are the missing production-facing E2E coverage to add after the existing suites have produced a baseline report. They must use a dedicated QA shop, test station and synthetic documents. The browser suite must save screenshots, traces, network failures and a redacted JSON report. All mutating cases should be opt-in and carry a `@destructive` / `@controlled-print` tag so a public smoke run cannot execute them accidentally.

### A. Public site, downloads and browser health

| ID | Test | Expected result |
|---|---|---|
| SP-LIVE-001 | Landing page loads in headed Edge/Chromium on the canonical hostname | HTTP 200, correct page identity, no uncaught page errors or same-origin 5xx |
| SP-LIVE-002 | Canonical HTTPS, redirects and host consistency | All app navigation remains on `https://smartprint.quantaedge.in`; no HTTP downgrade |
| SP-LIVE-003 | Static JS/CSS/fonts/images load | No failed critical resources, mixed content or console errors |
| SP-LIVE-004 | `/health/live` response schema | API identity and `live` status are correct |
| SP-LIVE-005 | `/health/ready` response schema | Ready status and database connected |
| SP-LIVE-006 | `/downloads/status` schema and cache policy | Current version and asset availability are internally consistent |
| SP-LIVE-007 | Windows download route | Stable-release installer streams with correct content type/length |
| SP-LIVE-008 | Android download route | Signed Android APK streams only when availability says true |
| SP-LIVE-009 | Range request / resume download | Valid single byte-range returns correct 206 and Content-Range; invalid range is rejected safely |
| SP-LIVE-010 | Download proxy redirect handling | Browser remains on SmartPrint origin; upstream signed GitHub Location is not exposed |
| SP-LIVE-011 | Stable release/version and checksum agreement | Downloaded asset hash matches published SHA256SUMS.txt |
| SP-LIVE-012 | Download upstream unavailable / retryable error | Friendly retryable state; page remains usable; no broken redirect |

### B. Authentication, roles and sessions

| ID | Test | Expected result |
|---|---|---|
| SP-AUTH-001 | Valid dedicated shop-owner login | Authenticated shop workspace opens for the expected tenant |
| SP-AUTH-002 | Incorrect password | Clear error; no authenticated session |
| SP-AUTH-003 | Empty/malformed email and empty password | Inline validation; no request until valid input |
| SP-AUTH-004 | Repeated failed login / rate limit | Server-side limit applies; no account enumeration or lockout bypass |
| SP-AUTH-005 | Refresh while authenticated | Session is restored or safely refreshed without losing route/context |
| SP-AUTH-006 | Expired access token / refresh token | Re-authentication is safe; no protected content leaks |
| SP-AUTH-007 | Logout | Both in-memory and session credentials are cleared; protected route requires login |
| SP-AUTH-008 | Shop/admin session separation | Shop token cannot authorize admin; admin token cannot authorize shop |
| SP-AUTH-009 | Normal shop user attempts platform endpoint | 401/403; no platform data is returned |
| SP-AUTH-010 | Platform admin attempts tenant data without proper scope | Only permitted platform operations are returned; tenant boundaries enforced |
| SP-AUTH-011 | Direct navigation to protected deep links while logged out | User is redirected to correct login route and destination restored safely |
| SP-AUTH-012 | Account email/password update validation | Correct current-password verification, safe email-change handling and no stale session |
| SP-AUTH-013 | Account recovery / email verification availability | Verify documented state; currently tracked as not implemented, not a test failure if explicitly unavailable |

### C. QR station and customer session lifecycle

| ID | Test | Expected result |
|---|---|---|
| SP-QR-001 | Open enabled dedicated station QR | Correct shop branding and customer print form appear |
| SP-QR-002 | Disabled/revoked/nonexistent station code | Safe unavailable message; no session issued |
| SP-QR-003 | Create customer session | Session is station- and tenant-bound, short-lived and returned once |
| SP-QR-004 | Refresh with active session | Same session resumes; no duplicate session/job |
| SP-QR-005 | Expired session | API rejects it; page guides customer to restart |
| SP-QR-006 | Revoked session | Existing token cannot continue upload/quote/dispatch |
| SP-QR-007 | Replay / reuse one-use upload grant | Second use is rejected |
| SP-QR-008 | Cross-station token or grant replay | Rejected without revealing another shop's metadata |
| SP-QR-009 | Session keepalive and renewal | Session extends only within policy and correct station scope |
| SP-QR-010 | Session tab close/reopen | Resume behaviour matches product policy; no session leaks to another browser profile |
| SP-QR-011 | QR code route parsing / unsafe URL payload | Only approved HTTPS station route accepted; invalid schemes/hosts rejected |
| SP-QR-012 | High-rate session creation | Server-side rate limit applies without creating duplicate sessions |

### D. Customer documents, batches, quote and job creation

| ID | Test | Expected result |
|---|---|---|
| SP-DOC-001 | Upload a synthetic valid PDF | Correct analysis, page count, size and SHA-256 recorded |
| SP-DOC-002 | Upload valid JPG/JPEG/PNG | Image job is accepted with correct media type and page handling |
| SP-DOC-003 | Extension/MIME/signature mismatch | Rejected server-side, even when browser accept attribute is bypassed |
| SP-DOC-004 | Zero-byte / corrupt file | Friendly validation; no orphan job/document |
| SP-DOC-005 | Unsupported executable/script/archive/Office file | Rejected under current supported formats; no parser execution |
| SP-DOC-006 | 20 MB boundary | At-limit document accepted if otherwise valid; over-limit rejected |
| SP-DOC-007 | PDF page-count boundary (500) | 500-page maximum enforced; 501-page file rejected |
| SP-DOC-008 | Multi-file batch boundary (20 files / 50 MB) | In-limit batch accepted; file-count and aggregate-size overflow rejected |
| SP-DOC-009 | Same file submitted twice | Product's duplicate policy is respected; no accidental duplicate print job |
| SP-DOC-010 | Batch display | Multiple files appear as one logical batch but independent print jobs as designed |
| SP-DOC-011 | Copies, colour, paper, duplex and orientation inputs | Values persist across steps and match server quote request |
| SP-DOC-012 | Quote calculation | Server-side price, tax/fees if applicable, copies and duplex allocations are authoritative |
| SP-DOC-013 | Customer tampers with price/amount in browser | Server ignores client-supplied amount and returns canonical quote |
| SP-DOC-014 | Quote expiry | Expired quote cannot be accepted/paid/dispatched |
| SP-DOC-015 | Quote refresh after settings change | New quote uses current versioned shop settings; prior quote remains immutable |
| SP-DOC-016 | Browser refresh mid-upload | Recoverable state; no duplicate reservation or lost batch |
| SP-DOC-017 | Interrupted cloud upload | Retry/resume is bounded and idempotent; no partial document exposed |
| SP-DOC-018 | Interrupted local upload | Pending ticket reconciles; retry does not create duplicate work |
| SP-DOC-019 | SHA-256 mismatch / truncated body | Agent/API reject corrupt payload; no print is authorized |
| SP-DOC-020 | Concurrent uploads at quota boundary | Tenant monthly quota is serialized; no over-allocation |

### E. Cloud-assisted and direct shop-Wi-Fi paths

| ID | Test | Expected result |
|---|---|---|
| SP-NET-001 | Cloud-assisted upload | Upload reaches protected cloud storage and is available only to authorized shop/Agent |
| SP-NET-002 | Local upload with phone on shop LAN | Private Agent route is discovered and upload goes directly to Agent |
| SP-NET-003 | Phone on wrong Wi-Fi | Local path is not falsely marked ready; safe cloud fallback remains available |
| SP-NET-004 | Local Network Access permission denied | Helpful recovery message; no leaked private IP/token |
| SP-NET-005 | Agent offline/unreachable | No silent local success; cloud fallback offered if configured |
| SP-NET-006 | Router client isolation / firewall blocks TCP 17842 | Clear not-connected state; no repeated uncontrolled dispatch |
| SP-NET-007 | Local endpoint from non-RFC1918 or wrong port | Browser/native app refuses the endpoint |
| SP-NET-008 | Wrong Origin / CORS | Agent only accepts exact configured portal origin and approved local requests |
| SP-NET-009 | One-use grant bound to wrong Agent/printer/station | Upload denied |
| SP-NET-010 | Local upload grant expiry/revocation | Expired/revoked grant rejected |
| SP-NET-011 | Local queue disk full / file-system failure | No false success; retry/recovery guidance and integrity maintained |
| SP-NET-012 | Broadband loss during local upload | Existing local transfer recovery policy applies; cloud control-plane dependency is reported honestly |

### F. Shop workspace and fulfilment

| ID | Test | Expected result |
|---|---|---|
| SP-SHOP-001 | Open all shop sections | Order Journey, Overview, Print Queue, Printers, QR Stations, Print Agent, Pricing, Subscription, Payments, Customers, Reports and Settings render |
| SP-SHOP-002 | Deep-link each section and refresh | Correct section restores without broken route |
| SP-SHOP-003 | Job queue search/filter/pagination | Results and counts agree with server; no tenant leakage |
| SP-SHOP-004 | Batch cards and counts | One logical card per batch; status counts correctly reflect underlying jobs |
| SP-SHOP-005 | Open uploaded job/document preview | Only authorized tenant content is available; local-only document stays local |
| SP-SHOP-006 | Approve a QA job | Correct state transition and audit record |
| SP-SHOP-007 | Reject a QA job | Correct terminal/review state; customer sees safe reason |
| SP-SHOP-008 | Select/reassign printer | Only active, compatible printers are offered; choice persists |
| SP-SHOP-009 | Printer offline or stale capability | Dispatch is blocked or guided safely; no optimistic print-complete state |
| SP-SHOP-010 | Review/reject individual batch files | Every file starts accepted; individual rejection excludes only that file |
| SP-SHOP-011 | Undo a file rejection before apply | File returns to accepted and amount/selection recomputes |
| SP-SHOP-012 | Partial page rejection/refund calculation | Allocation respects copies/duplex/minimum charge; cumulative refund never exceeds original payment |
| SP-SHOP-013 | Full rejection/refund calculation | Full eligible amount is derived from immutable quote allocation |
| SP-SHOP-014 | Repeated refund command / idempotency replay | No duplicate refund record or over-refund |
| SP-SHOP-015 | Customer payment attempt appears in shop | Unverified UPI reference is clearly marked reported/pending, not paid |
| SP-SHOP-016 | Shop manually verifies QA cash/UPI test payment | Requires authorized operator and audit; use test-only transaction, never fake production receipts |
| SP-SHOP-017 | Cash/UPI settings and price validation | Invalid configuration rejected; existing jobs retain quote snapshots |
| SP-SHOP-018 | Dispatch only after server-authorized prerequisites | No job dispatch before approval, valid quote and verified payment where policy requires |
| SP-SHOP-019 | Duplicate dispatch click / network retry | One dispatch attempt; uncertain physical output is not auto-resubmitted |
| SP-SHOP-020 | Spooler accepted vs physically completed | UI distinguishes queue acceptance, printing, completed and output-unconfirmed |
| SP-SHOP-021 | Explicit output confirmation | Only authorized shop operator can confirm; report/job state updates once |
| SP-SHOP-022 | Restart after uncertain spooler submission | No automatic duplicate print; recovery asks operator to reconcile output |
| SP-SHOP-023 | Customer directory/history | Tenant-scoped signed-in customer data only; guest jobs remain anonymous as designed |
| SP-SHOP-024 | Reports and payment totals | Collections and refunds appear in their correct accounting periods |

### G. Platform administration

| ID | Test | Expected result |
|---|---|---|
| SP-ADMIN-001 | Admin login and session persistence | Platform identity only; no shop identity confusion |
| SP-ADMIN-002 | Shop owner visits `/admin` | Admin-only login/denial; no platform access |
| SP-ADMIN-003 | Open all five platform sections | Overview, Shop tenants, Plans & entitlements, Shop subscriptions and Operations & retention render |
| SP-ADMIN-004 | Tenant list search/pagination | Correct results; no accidental mutation |
| SP-ADMIN-005 | Tenant detail cross-check | Tenant status/plan/usage agrees with API, without exposing documents or secrets |
| SP-ADMIN-006 | Plan version view | Immutable published versions display correctly |
| SP-ADMIN-007 | Assign trial/free plan to QA tenant | Authorized assignment and audit actor/reason recorded |
| SP-ADMIN-008 | Reject invalid/expired plan assignment | No unintended entitlement change |
| SP-ADMIN-009 | Suspend QA tenant | New sessions/quotes/dispatch are blocked; recovery/history remain available |
| SP-ADMIN-010 | Reactivate suspended QA tenant | Only supported transition allowed and audited |
| SP-ADMIN-011 | Attempt terminal inactive reactivation | Rejected, no state mutation |
| SP-ADMIN-012 | Retention settings change | Requires reason and stores before/after audit; only QA tenant/approved test config |
| SP-ADMIN-013 | Admin logout and token expiry | Platform access removed immediately |
| SP-ADMIN-014 | Cross-role API attempts | Server rejects, regardless of hidden UI navigation |

### H. Desktop, Agent and physical printer

| ID | Test | Expected result |
|---|---|---|
| SP-WIN-001 | Installer install/reinstall/upgrade/rollback/uninstall | Existing lifecycle script passes without losing pairing or queue state |
| SP-WIN-002 | Agent initial pairing | Short-lived authorization, correct tenant and device identity |
| SP-WIN-003 | Re-pair after owner revocation | Requires explicit reconnect after server confirms revocation |
| SP-WIN-004 | Attempt to move Agent from another active shop | Blocked until original shop revokes it |
| SP-WIN-005 | Windows service starts under configured identity | Service, DPAPI and ACL permissions work as intended |
| SP-WIN-006 | Firewall rule scope | Private profile only; TCP 17842 not exposed publicly |
| SP-WIN-007 | Local endpoint discovery | Only valid private IPv4 endpoints discovered |
| SP-WIN-008 | Printer inventory sync | Real queue/driver capabilities sync to shop portal |
| SP-WIN-009 | Disconnect/restart Agent | Queue journal and documents survive restart; heartbeat recovers |
| SP-WIN-010 | Print synthetic PDF on Microsoft Print to PDF | Actual PDF output file exists and is valid |
| SP-WIN-011 | Print on designated physical printer | Correct page count, copies, colour, paper and duplex; operator observes output |
| SP-WIN-012 | Printer offline/out of paper/paper jam | No false completed state; recoverable state and clear operator action |
| SP-WIN-013 | Output uncertain after service/process restart | No auto reprint; explicit reconciliation required |
| SP-WIN-014 | Agent disk loss / local source recovery | Documented recovery path; currently a production-readiness gap to validate |

### I. Android physical acceptance (not browser-only)

Run and record PHY-01 through PHY-12 in `docs/ANDROID_PHYSICAL_ACCEPTANCE.md`. Playwright can test the embedded web page but cannot prove camera hardware, Android document picker behavior, native permission prompts, Local Network Access or phone-to-Agent transfer. These require a real Android device, and the full owner-to-customer sequence requires an actual shop PC and network.

### J. Security, privacy, performance and resilience

| ID | Test | Expected result |
|---|---|---|
| SP-SEC-001 | Shop A requests Shop B job/document/customer by ID | 404/403 and no data leakage |
| SP-SEC-002 | Customer session requests different station/tenant | Denied |
| SP-SEC-003 | Forged JWT / changed tenant claim | Signature/scope validation rejects request |
| SP-SEC-004 | Missing/invalid/expired Agent credential | Agent endpoint denies access |
| SP-SEC-005 | Path traversal / encoded traversal in file name | Rejected; storage remains outside web root |
| SP-SEC-006 | Malicious/corrupt PDF and oversized images | Safely rejected/limited; no unbounded parser execution |
| SP-SEC-007 | Stored/reflected HTML payload in names/notes | Rendered as text; no script execution |
| SP-SEC-008 | CSRF/CORS and origin checks | Only approved origins and auth flows work |
| SP-SEC-009 | Login/QR/upload/Agent rate limits | Limits are server-side and not bypassable by untrusted headers |
| SP-SEC-010 | Secret/document leakage in console, logs and artifacts | Tokens, passwords, document bytes and signed asset URLs are absent |
| SP-SEC-011 | Keyboard-only navigation and accessible labels | Primary flows operable; focus and dialogs behave correctly |
| SP-SEC-012 | Slow 3G / high latency / dropped connection | Clear loading, retry, duplicate-safe state transitions |
| SP-SEC-013 | Concurrent upload/claim/refund races on PostgreSQL | Exactly-once reservations/claims/refunds; no over-quota or over-refund |
| SP-SEC-014 | Backup restore rehearsal | Database, document archive and keys restored together and verified |
| SP-SEC-015 | Agent/local queue restart recovery | Durable queue and event reconciliation; no missing/duplicate job |
| SP-SEC-016 | Sustained/burst traffic on a disposable QA tenant | Latency/error/rate-limit behavior within agreed operational thresholds |

## 4. Important features that are not currently implemented

The repository README/roadmap explicitly says some work is not part of the current release. Record these as **NOT IMPLEMENTED / OUT OF SCOPE**, not failed tests:

- Automated customer payment gateway verification and automated refund execution/reconciliation.
- Automated SaaS subscription checkout/invoices/webhooks/reconciliation.
- Office document uploads and multi-file Office conversion.
- Full iOS native app, Play Store production channel and all-printer universal compatibility.
- Automated email verification and account recovery.
- Malware scanning and isolated document parser/converter.
- End-to-end encryption of direct-LAN document bytes (the current LAN path uses private-network HTTP; do not use on guest/public Wi-Fi).
- Dedicated least-privilege Windows Agent service identity validation.
- Encrypted off-host backup and rehearsed full restore.
- Local Agent disk-loss re-upload recovery and full PostgreSQL concurrency scenarios.

## 5. Live test safety and release gates

1. Use one dedicated QA shop tenant, one dedicated enabled QA station, dedicated QA shop/admin users and synthetic documents.
2. Public smoke and login/navigation tests are read-only. Do not run registration or settings/plan changes on a real customer tenant.
3. Any case that creates an order, payment record, refund, quota reservation, customer session, dispatch or print output must be tagged and explicitly enabled for the QA tenant only.
4. Real payment gateway tests are not applicable to this release because automated gateway integration is not implemented. Never use a real customer payment for QA.
5. Physical output tests must use an explicitly selected test printer or Microsoft Print to PDF. Confirm printer, number of copies and synthetic document before dispatch.
6. Never automatically retry an output-uncertain print. Reconcile with the operator first.
7. Capture redacted screenshots, Playwright traces, API status, test ID, build/version and timestamp. Never save password, token, QR session secret, personal document or full payment reference in evidence.
8. Final sign-off requires no open Sev-1/Sev-2 issues, successful real-device Android acceptance PHY-01–PHY-12, successful owner-to-customer physical print sequence, backup/restore evidence and security review.

## 6. Test result states

- **PASS** — expected behavior observed with evidence.
- **FAIL** — reproducible product defect.
- **BLOCKED** — test could not run due environment, access, dependency or unavailable hardware.
- **NOT IMPLEMENTED** — explicitly not in current product scope.
- **SKIPPED** — deliberately not run, with reason recorded.

A successful DNS/HTTP request or page render is never sufficient to mark a customer printing flow passed.