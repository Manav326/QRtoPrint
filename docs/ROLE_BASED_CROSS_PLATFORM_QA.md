# SmartPrint role-based cross-platform acceptance matrix

**Purpose:** Close the role and native-surface gaps around the existing production QA catalogue.  
**Applies to:** Shop owner, customer (guest and signed-in), Android customer app, Windows Desktop owner app, Windows Print Agent, and the shared Web/API lifecycle.  
**Execution target:** Local Windows QA stack only. Do not point this suite at production.

This is a supplement, not a replacement, for:

- [Production QA Master Test Plan](PRODUCTION_QA_TEST_PLAN.md) — 333 detailed baseline cases covering release, identity, shop operations, QR, Android, documents, quotes, jobs, refunds, finance, admin, security, recovery, operations and accessibility.
- [Exhaustive local real-data QA plan](SMARTPRINT_EXHAUSTIVE_LIVE_QA_PLAN.md) — 262 uniquely mapped real-local-data Playwright acceptance cases with a 100% completion gate.
- [Android physical-device acceptance](ANDROID_PHYSICAL_ACCEPTANCE.md) — physical camera, QR, shop-Wi-Fi and Windows-to-Android readiness workflow.
- [Local native application QA](LOCAL_NATIVE_APP_QA.md) — local Windows Desktop build/smoke and side-by-side Android QA app execution.

The 84 cases below add role-focused, cross-surface assertions for the newer Android customer account/document-studio workflows and for the actual Windows Desktop owner experience. They must be recorded independently. They are **not** counted as passed by the 262-case browser run or by a native build.

## 1. Product and role contract

| Role / surface | What must be verified |
|---|---|
| Shop owner — Web | Owns one shop workspace; configures the shop, printer/Agent, stations, pricing and payment methods; reviews jobs; controls manual payment verification, dispatch and output confirmation; sees only that shop's customer and financial information. |
| Shop owner — Windows Desktop | The same shop-owner workspace inside the actual native Tauri window. It must use the same tenant, API, data, permissions and lifecycle as Web; it is not a second shop system. |
| Customer — Web/PWA | Enters through the shop's HTTPS station QR/link as a guest or an explicitly authenticated global customer; uploads, reviews, configures, accepts the server quote, follows payment/job status and sees truthful output status. |
| Customer — Android | Prepares and stores files in the native library, scans the correct QR for the intended purpose, and hands original files to the same Web/API print lifecycle. It does not create a second quote, payment, order or print state machine. |
| Windows Print Agent | Separate Windows background service on the printer-connected computer. It is not the Desktop UI. Pairing, local queue, printer access, service lifecycle and spooler/output uncertainty are tested independently. |
| Platform administrator | Remains a separate control-plane identity. Existing platform-admin cases in the master plan continue to apply; a shop owner or customer must never inherit platform-admin authority. |

**Shop-owner roles:** The owner journey is the current supported acceptance baseline. A complete manager/operator/read-only membership and permission matrix is still documented as incomplete. Do not invent or certify those roles by testing the owner account; record those role capabilities as NOT IMPLEMENTED until the feature and server-side authorization matrix exist.

**One lifecycle rule:** Web, Android and Windows Desktop must observe the same server-owned PrintJob, batch, quote, payment, review, Agent dispatch and output-confirmation records. A screen transition alone is not proof; read back the persisted state and compare the customer and owner views.

## 2. Test statuses, safety and fixtures

### Result definitions

| Result | Use |
|---|---|
| PASS | Every action and expected result was observed on the named build; evidence is retained. |
| FAIL | The observed behaviour differs from the expected result. Capture the first failure and stop only dependent cases. |
| BLOCKED | A required device, printer, service, test account or safe environment is unavailable. A blocked case is not a pass. |
| NOT IMPLEMENTED | The capability is not delivered in the candidate. Keep it out of release claims and record the product/release decision. |
| NOT APPLICABLE | Truly outside the release scope, with a written reason and approver. |
| NOT RUN | No execution evidence exists. |

**Severity:** P0 blocks release for wrong-shop routing, tenant/privacy breach, data loss, financial corruption, unsafe/duplicate/unauthorised print, or a broken core customer-to-shop workflow. P1 is a major user or recovery failure. P2 is a lower-impact usability/visual issue.

### Required local fixtures

Use only synthetic records with clearly labelled names:

- SHOP-A and SHOP-B: two separate active local QA shops with different owners, stations and rate cards.
- OWNER-A and OWNER-B: distinct shop-owner identities; PLATFORM-ADMIN: separate platform identity.
- CUSTOMER-GUEST and CUSTOMER-ACCOUNT: anonymous browser customer and signed-in global customer.
- QA-ANDROID: the selected physical Android phone; QA-TABLET: an Android tablet where available.
- QA-WINDOWS: disposable or designated Windows QA workstation with the local Desktop and Agent.
- QA-PRINTER: Microsoft Print to PDF or a clearly designated test printer; PRINTER-FAULT: a safe simulator/test queue for offline and failure paths.
- DOC-PDF: known-page-count synthetic PDF; DOC-IMAGE: known JPG/JPEG/PNG; DOC-BAD: malformed/renamed/truncated test file; DOC-LARGE: boundary-size synthetic file.
- BATCH-A: three or more distinct synthetic files, with one file deliberately rejected during shop review.
- NETWORK-SHOP and NETWORK-OTHER: isolated test networks; never perform local HTTP document transfer on guest/public Wi-Fi.

### Safety rules

1. Keep the application origin, API, database, document store and Agent in the local QA environment. Verify the configured tunnel routes to the local Compose portal before native-device testing.
2. Never use real customer documents, production accounts, live UPI/bank details or real payment credentials. Use clearly synthetic references such as QA-NOT-REAL.
3. Never claim a payment is verified because a customer entered a reference or uploaded a screenshot. Use a QA-only verification fixture or keep the attempt pending/rejected.
4. Do not record a real refund unless money was actually returned. For automated QA, use test-only records and never tell a real customer that a refund occurred.
5. Do not uninstall, clear data from, re-pair, revoke, overwrite, or reconfigure an existing owner/customer installation or Agent. Use the side-by-side local QA Android package and a disposable Windows profile/VM.
6. Do not send output to an ordinary shop printer by default. Use Microsoft Print to PDF or a specifically designated test printer; a physical test print needs explicit operator selection and synthetic content.
7. Spooler acceptance is not proof of paper output. Do not automatically resubmit an uncertain print after crash/restart.
8. Redact access/refresh tokens, QR/session tokens, Agent secrets, customer email, local document contents and Wi-Fi credentials from evidence.

## 3. Shop-owner Web acceptance

These supplement the SHOP, AGT, JOB, REF, PAY, CUS, RPT and UX cases in the master plan. Run them with OWNER-A and verify persisted state through the owner UI plus the local API/record where appropriate.

| ID | Priority | Scenario and procedure | Expected result |
|---|---:|---|---|
| WOWN-001 | P0 | Register or sign in as OWNER-A through the real local shop Web entry. Reload the page and navigate directly to a protected shop route. | The correct SHOP-A workspace is restored; no platform-admin or SHOP-B scope appears; unauthenticated routes do not expose protected shop data. |
| WOWN-002 | P0 | Open the same SHOP-A owner session in a second tab, then sign out in one tab and revisit a protected route in both. | Logout/revocation is handled consistently; no stale tab can mutate protected shop data or display a false authenticated state. |
| WOWN-003 | P1 | Review Overview, Order Journey, Print Queue, Printers, QR Stations, Print Agent, Pricing, Subscription, Payments, Customers, Reports and Settings using populated and empty QA records. | Every enabled destination renders a meaningful state; no dead control, blank panel, unhandled error, stale cross-page record or hidden horizontal overflow. |
| WOWN-004 | P0 | Change SHOP-A branding and logo, then open the public customer station route in a separate guest browser. Repeat with an unavailable/untrusted QA logo. | Customer view uses the correct tenant branding; invalid logo falls back cleanly; changing SHOP-A never changes SHOP-B branding. |
| WOWN-005 | P0 | Configure SHOP-A's supported price rules and payment methods. Change the price after issuing a quote, then create a new quote. | Existing quote remains an immutable snapshot; new quote uses the new server-side rate; UI totals equal API totals; no client-supplied total is trusted. |
| WOWN-006 | P0 | Register/enable a QA printer and station, set the intended default, then disable the station and re-open its old QR. | Owner sees the correct printer/Agent association; disabled station cannot start a new customer session; old station code cannot route a customer to another shop. |
| WOWN-007 | P0 | From the real owner Web queue, open BATCH-A, preview each file, reject one file, undo it, reject it again, apply review, and inspect the accepted remainder. | Review is per file; undo works only before apply; the rejected file is excluded from payable/printable work; each job and batch relationship remains correct. |
| WOWN-008 | P1 | Compare customer directory, payment list, report totals and job detail for SHOP-A with SHOP-B using the same global customer identity. | SHOP-A sees only its own customer-linked sessions/jobs and masked account information; payment/report totals reconcile to SHOP-A records; SHOP-B remains isolated. |

## 4. Customer Web/PWA acceptance

Use an ordinary browser guest profile for guest flows and a separate customer-authenticated context only where the current build supports it. Do not manufacture a customer Authorization header or session token for a manual test.

| ID | Priority | Scenario and procedure | Expected result |
|---|---:|---|---|
| WCUS-001 | P0 | Open SHOP-A's official HTTPS station link from a desktop browser and from the Android phone's ordinary camera/browser path. | The normal customer station page opens; the API creates the correct SHOP-A-bound guest session; external links are not expected to auto-launch the Android app. |
| WCUS-002 | P0 | Open the same station link in two independent browser profiles/tabs; expire one session and continue using the other. | Sessions are independent and station-bound; expiry of one does not transfer, invalidate or expose the other session's opaque token. |
| WCUS-003 | P0 | Upload DOC-PDF, DOC-IMAGE and DOC-BAD through the visible customer file picker; repeat with per-file and batch boundary values. | Supported formats are accepted within current limits; malformed/mislabelled files fail before job creation; original names, byte integrity, page count and file-to-job mapping are correct. |
| WCUS-004 | P0 | Inspect the uploaded document preview, page analysis, page ranges, copies, orientation and other options offered by the selected printer. | Preview and metadata refer to the selected original; Copies and supported options retain exact values; unsupported options are disabled/explained; no setting is silently changed. |
| WCUS-005 | P0 | Upload BATCH-A with three or more files; interrupt one upload and use the supported retry/recovery path. | Each accepted file has its own PrintJob under one batch; order and filenames remain traceable; retry is idempotent and creates no duplicate job/payment. |
| WCUS-006 | P0 | Follow a batch after owner rejection/undo/apply. Refresh customer status and open the accepted and rejected file details. | The customer sees the correct per-file outcome and revised payable amount; rejected content cannot be dispatched; no false “all accepted” or “all printed” state appears. |
| WCUS-007 | P0 | Submit a synthetic UPI reference and separately test cash where SHOP-A enables it. Do not transfer money. | Reference submission remains pending until the owner verifies it; a screenshot/reference cannot set PAID or enable dispatch; cash follows the explicit shop policy and audit. |
| WCUS-008 | P0 | On NETWORK-SHOP, grant browser Local Network Access and run direct upload; deny the permission and retry using the supported Cloud-assisted path. | Direct mode uses the paired SHOP-A Agent only when authorised/reachable; denial is explained and does not destroy the draft; Cloud-assisted fallback uses the same job/quote lifecycle. |
| WCUS-009 | P1 | Refresh, use Back/Forward, reopen the station tab, background the mobile browser and resume before/after session expiry. | A valid session resumes without duplicate job submission; an expired session gives a clear new-session path and does not silently attach the draft to a different customer/shop. |
| WCUS-010 | P0 | Complete a synthetic customer job through owner review, manual QA payment state, explicit dispatch to QA-PRINTER and owner output confirmation. | Customer status matches the authoritative lifecycle; “spooler accepted” is not presented as confirmed physical output; completion/collection wording is truthful. |

## 5. Android customer app — expanded native acceptance

Run on the side-by-side local QA package against the local stack. Emulator instrumentation is useful for repeatable UI checks, but physical acceptance is mandatory for camera, file-provider, Local Network Access, process-death and shop-Wi-Fi cases. Record device model, Android API level, RAM, Google Play services version and selected file-provider app.

| ID | Priority | Scenario and procedure | Expected result |
|---|---:|---|---|
| ANDX-001 | P0 | Install/update the local QA Android package beside the normal SmartPrint package; launch it from the actual Android launcher. | The local QA package is distinct; the normal app and its data remain installed and unchanged; the correct MainActivity and SmartPrint native shell launch. |
| ANDX-002 | P1 | Visit Home, My Files, Scan & Print and Activity; use system Back from nested folder, profile and print screens; rotate the device. | The four destinations are unique and stable; selected state/back stack is coherent; no duplicate scanner, broken navigation or clipped bottom bar/landscape rail. |
| ANDX-003 | P0 | From a guest state, register a synthetic global customer, sign out, sign in, and attempt invalid credentials/duplicate registration. | Server-backed account states and errors are accurate; no local-only fake login, no shop-owned customer identity, and no credential/token is shown in UI or logs. |
| ANDX-004 | P0 | Sign in, force-stop/reopen the app, allow an expired access token to refresh, then sign out. Inspect the account store without exposing secret bytes. | Tokens persist only through the encrypted Android Keystore-backed store; refresh is safe; logout clears account credentials but does not delete local guest files or shop pairing/profile state. |
| ANDX-005 | P0 | Sign in as CUSTOMER-ACCOUNT, create jobs at SHOP-A and SHOP-B, and open Activity. Separately create a guest job before signing in. | Activity shows only sessions/jobs linked to that global customer; the two shops remain identified correctly; pre-login guest history is not silently claimed or exposed. |
| ANDX-006 | P1 | Import one PDF and one image through Android's system file picker from at least two providers (for example, Downloads and a document provider). | The app copies the original bytes to app-private storage, preserves a safe filename/MIME association and reports success/failure without requesting broad storage access. |
| ANDX-007 | P0 | Import multiple files and a nested folder tree; repeat with mixed supported and unsupported files and compare selected order/path. | Supported files import with their relative folder structure/order where available; unsupported/missing items are reported individually; transfer budgets are enforced before partial corruption. |
| ANDX-008 | P1 | Deny/revoke provider access mid-import, remove a source file before import completes, and test a provider that cannot report a display name or size. | Import fails safely per item with a useful recovery message; no empty/truncated file is represented as successful; other successful imports remain intact. |
| ANDX-009 | P0 | Test filenames with Unicode normalization, emoji, quotes, leading/trailing spaces, path separators, reserved names, very long names and duplicate names from different folders; test the 100 MB local-library boundary. | Unsafe path segments cannot escape app-private storage; names are sanitised without collisions/data overwrite; size limit is enforced; original content is not silently replaced. |
| ANDX-010 | P1 | Create nested folders; rename, move, reorder and delete a synthetic file/folder; navigate away and back. | Operations remain within the library root; parent/child integrity is preserved; moving into self/descendant is rejected; deletion/overwrite is confirmed; ordering is stable. |
| ANDX-011 | P1 | Open a multipage PDF and portrait/landscape/large JPG/JPEG/PNG. Navigate pages, zoom/pan, rotate the device and close/reopen the viewer. | Correct page/image is rendered; memory remains stable; errors are recoverable; the viewer never mutates the stored original. |
| ANDX-012 | P0 | Edit a saved PDF by rotating pages, removing a page and reordering pages; save as a copy, then compare source and output page labels/count. | Source PDF remains byte-for-byte unchanged; saved copy has the exact requested order/rotation/removal; page count is correct; save failure leaves the original usable. |
| ANDX-013 | P1 | Use the separate Combine into one PDF action with multiple PDFs/images, then test output size and page limits. | It produces one explicitly converted PDF only when within supported output constraints; rasterisation/quality limitations are disclosed; source files remain unchanged; it is not confused with multi-file batch printing. |
| ANDX-014 | P0 | From the customer portal's multi-file picker, select original files across nested folders; change selection, navigate folders and confirm selection. | Selection persists in intended order across folders; files are handed back as original content URIs; selected files remain individual print jobs under one batch ID, not silently flattened into one PDF. |
| ANDX-015 | P0 | In the native multi-file picker, select the same file twice, then select a missing, deleted or unsupported file and exceed file-count/size limits. | Duplicate paths are not submitted twice; unavailable/unsupported items are clearly skipped or rejected; limits are enforced before portal submission; no partial phantom job is created. |
| ANDX-016 | P1 | Start ML Kit document scanning for the first time; test multi-page capture, crop/rotation correction, page review, gallery import, cancel and save. | ML Kit owns the document-scan experience; output is saved only after confirmation; cancel preserves prior files; no second CameraX document scanner appears. |
| ANDX-017 | P0 | Scan a normal SHOP-A station QR from Scan & Print, then scan a Wi-Fi-readiness QR from the connected-shop Check shop Wi-Fi action; deliberately swap the two QR types. | Each entry point accepts only its intended QR purpose; wrong-purpose, malformed and wrong-origin codes are rejected before shop/session mutation; no arbitrary URL is opened from QR content. |
| ANDX-018 | P0 | Connect to SHOP-A, prepare unsent work, explicitly switch to SHOP-B, then return to SHOP-A's saved local files/history. | Shop switch is deliberate; station/session/job history is shop-bound; no draft/file/job is reassigned to SHOP-B; previously saved files remain available. |
| ANDX-019 | P0 | Run the Windows owner-generated Wi-Fi readiness QR flow with fresh, expired, reused and NETWORK-OTHER attempts while Android already has a SHOP-A customer session/draft. | Only a fresh one-time code from the selected shop network passes; Windows updates through its existing status mechanism; customer shop/session/draft remain unchanged. |
| ANDX-020 | P0 | On the actual phone, upload one synthetic document over direct Shop Wi-Fi to the paired QA Agent; repeat with local access denied/unreachable and choose Cloud-assisted mode. | Direct transfer reaches only the paired SHOP-A Agent on the approved local endpoint; denial/unreachable state gives safe guidance; fallback uses the same PrintJob, quote and owner review; no duplicate upload/job. |
| ANDX-021 | P0 | Force-stop the app during import, document preview, station session and after upload begins; restore internet/Wi-Fi and reopen. | The app never claims an unpersisted operation succeeded; recoverable files/session state returns or a clear retry is offered; no duplicate job or cross-shop session appears. |
| ANDX-022 | P1 | Test TalkBack, font scale 1.3×/2×, Android display scaling, system/light/dark themes, camera denial, low memory, tablet portrait/landscape and supported Android 8+ baseline where available. | Controls remain named/reachable; text and focus remain legible; navigation adapts; unsupported device/service states explain the limitation; no crash or clipped critical action. |

## 6. Windows Desktop shop-owner acceptance

These cases must be executed through the **actual native SmartPrint Desktop window**, not a Chrome tab. The current native runner builds the executable and checks startup/accessibility landmarks; that smoke is not a full Desktop workflow pass. Use a disposable Windows QA profile or test VM, and do not overwrite an existing owner installation or session.

| ID | Priority | Scenario and procedure | Expected result |
|---|---:|---|---|
| WDSK-001 | P0 | Build/launch the local Desktop executable and sign in as OWNER-A through its real WebView2 sign-in screen. | Native window opens and renders the expected shop sign-in/workspace; it targets the local QA API/origin; no blank WebView, CI-only DevTools endpoint or production origin. |
| WDSK-002 | P0 | Sign in, close/reopen Desktop, expire/revoke the session and sign out. Inspect the Windows Credential Manager entry through a safe test profile. | Session restore uses the protected Windows credential store; expired/revoked credentials return to sign-in; logout clears shop session but does not erase Agent pairing identity or local print queue. |
| WDSK-003 | P1 | Navigate every enabled shop destination inside Desktop, including Order Journey, Overview, Print Queue, Printers, QR Stations, Print Agent, Pricing, Subscription, Payments, Customers, Reports and Settings. | Each native navigation item loads the expected route/data; active selection, fixed navigation/header, scrolling and back behaviour remain coherent; no dead link or browser-only redirect. |
| WDSK-004 | P0 | Update SHOP-A profile, price, payment display method and station in Desktop; open SHOP-A in the Web workspace and read the same records. | Desktop and Web share the same backend tenant state and audit records; no separate/local-only copy or stale overwrite exists. |
| WDSK-005 | P1 | Minimise, restore, resize and maximise Desktop at 100%, 125%, 150% and 200% Windows scaling; test a small laptop-sized window and multiple monitors. | Native title bar/window controls work; content does not clip or become unreachable; only intended inner content scrolls; no second browser window steals the workflow. |
| WDSK-006 | P0 | Open a customer job and BATCH-A from Desktop; preview original files, inspect page count/options and apply one rejection with undo before final review. | Owner sees the exact customer files/job IDs; preview is authorised and complete; review state and per-file decision match the Web owner view and persisted API state. |
| WDSK-007 | P0 | Submit a pending synthetic UPI reference from customer view; attempt owner dispatch before verification; then verify only a QA fixture with independent confirmation. | Pending/rejected payment cannot dispatch; owner verification is explicit, reasoned and audited; amount/job/reference are correct; repeat action is idempotent. |
| WDSK-008 | P0 | Inspect printer inventory, capability list, disabled/offline printers and default selection in Desktop. | Only the actual Agent-discovered/registered QA devices appear; unsupported capabilities are disabled/explained; no incompatible or SHOP-B printer can be selected. |
| WDSK-009 | P0 | Open Print Agent in Desktop while the Windows Agent service is running, then stop it only in a disposable environment and reopen the status screen. | UI distinguishes service installed/running, local endpoint readiness, cloud heartbeat, printer discovery and queue health; no single green heartbeat falsely implies every layer is ready. |
| WDSK-010 | P0 | Exercise guided enrollment using a fresh one-time QA authorization in a disposable Windows profile; then test expired, replayed, wrong-shop and already-active computer authorization. | Enrollment is authenticated, tenant-bound, short-lived and single-use; a computer active at another shop cannot silently transfer; no pairing code/API URL is demanded in the normal guided owner flow. |
| WDSK-011 | P0 | Revoke the disposable QA Agent in the server, reopen Desktop and use the explicit reconnect action only after revocation is confirmed. | Revoked identity cannot heartbeat/dispatch; reconnect requires the explicit owner action and fresh authorization; existing queue/config is not silently discarded. |
| WDSK-012 | P0 | Change selected network adapter from Wi-Fi to Ethernet and back in a disposable QA shop; inspect LAN endpoint/phone readiness status. | Selected interface is explicit; no hidden network switch or Wi-Fi disconnect occurs; local endpoint and status update reflect the selected interface accurately. |
| WDSK-013 | P0 | Start Test with a phone in Desktop; complete Android same-network check; then try wrong network, expired code and replay. | Correct fresh check updates Desktop without manual refresh; negative tests fail clearly; station, pairing, printer selection and customer draft are unchanged. |
| WDSK-014 | P0 | Dispatch an approved, paid synthetic job to QA-PRINTER; compare Agent/spooler status with actual PDF/controlled physical output; leave a second case unconfirmed. | Correct content/settings reach the selected QA printer; spooler acceptance is labelled accurately; only inspected output is confirmed; uncertain output stays OUTPUT_UNCONFIRMED. |
| WDSK-015 | P0 | In a disposable Windows environment, restart Desktop and separately restart Agent at queued, pre-spooler and uncertain post-submit points. | Desktop lifecycle does not stop the independent Agent service; pending jobs recover from durable state; an uncertain physical submission is not automatically printed again. |
| WDSK-016 | P0 | Inspect local logs, app files, WebView profile, process command line, firewall and named-pipe access after sign-in, readiness test and synthetic job. | No secrets, tokens, document contents or customer data appear in logs/plaintext; local endpoints reject unauthorised remote clients; production binary exposes no diagnostic DevTools port. |
| WDSK-017 | P1 | Test keyboard-only operation, visible focus, screen reader names, light/dark theme, high contrast, reduced motion and 200% zoom in the real Desktop window. | Critical owner actions are reachable and understandable; status is not conveyed by colour alone; focus is visible; contrast and text scale remain usable. |
| WDSK-018 | P0 | Upgrade a disposable paired Windows installation with pending synthetic queue work; simulate new Agent startup failure and perform a safe rollback test. | Installer preserves pairing, configuration and pending work; failed upgrade restores the prior executable path/service state; cleanup occurs only after replacement health is confirmed. |

## 7. Cross-platform owner-to-customer journeys

These are mandatory integrated journeys. Run from a single frozen local build and use the same SHOP-A/SHOP-B fixtures across all devices. Keep a single test record with evidence from each surface.

| ID | Priority | Scenario and procedure | Expected result |
|---|---:|---|---|
| XPLAT-001 | P0 | OWNER-A changes SHOP-A profile, rate card and station in Web; sign into the actual Windows Desktop and read each changed value. | Both owner surfaces show the same server-persisted tenant state and version; no stale local cache overwrites a newer change. |
| XPLAT-002 | P0 | OWNER-A changes a QA printer/station setting in Desktop; reload the Web workspace and customer station page. | Web and customer views reflect the same setting; any station disable/rotation takes effect server-side; no stale QR silently starts a session. |
| XPLAT-003 | P0 | CUSTOMER-GUEST opens the SHOP-A HTTPS station link in mobile Web, uploads DOC-PDF and accepts the server quote; OWNER-A opens the same job in Desktop. | One server job ID is visible to the correct owner; customer and owner see matching file, page, quote and status data; no duplicated job is created by opening another surface. |
| XPLAT-004 | P0 | CUSTOMER-ACCOUNT scans SHOP-A in Android, selects an original saved file and sends it through the existing customer portal; OWNER-A reviews it in Desktop. | Android creates no alternate print job/payment model; original filename/hash and customer linkage are correct; the same job appears in the SHOP-A queue. |
| XPLAT-005 | P0 | CUSTOMER-ACCOUNT selects three files from nested Android folders and submits one batch; OWNER-A reviews it in Desktop, rejects one file and applies review. | Three distinct PrintJobs share one Batch ID; selection order and per-file identity survive handoff; rejected file is excluded; accepted files remain eligible under the shared lifecycle. |
| XPLAT-006 | P0 | Use the same global customer at SHOP-A and SHOP-B, then compare Android Activity, each shop's Customers/history view and guest browser history. | Global customer history includes only linked account sessions across shops; each shop sees only its own linked activity; guest sessions are not silently attached to the account. |
| XPLAT-007 | P0 | Submit an identical synthetic document once with Cloud-assisted mode and once with direct Shop Wi-Fi mode. Compare hash, analysis, quote, owner queue and output status. | Transfer path differs but job/quote/payment/review/dispatch state machine remains the same; local bytes stay on shop LAN as documented; no duplicate/missing file or altered content. |
| XPLAT-008 | P0 | Run the Windows Desktop → Android Check shop Wi-Fi → Windows auto-update sequence with an active Android shop session and unsent draft. | The fresh one-time readiness check passes only on the selected network; Desktop updates automatically; the Android shop, session, draft and printer configuration remain unchanged. |
| XPLAT-009 | P0 | Reject one batch file in Desktop, undo before apply, then apply the final rejection; inspect customer Web and Android Activity/status. | All surfaces show the same accepted/rejected outcomes and payable total; no rejected file is dispatched; a refresh cannot restore an obsolete decision. |
| XPLAT-010 | P0 | Complete a QA payment verification, explicit dispatch and output confirmation for one accepted job; leave another output uncertain. | Customer and owner surfaces agree on each distinct state; only the actually inspected output becomes COMPLETED; the uncertain job remains OUTPUT_UNCONFIRMED. |
| XPLAT-011 | P0 | Disconnect internet or restart the local Agent after an upload/job has been created; restore connectivity and reopen Web, Android and Desktop. | Each surface recovers the same authoritative job without duplicate payment/upload/dispatch; failures are explained; uncertain print output is not automatically retried. |
| XPLAT-012 | P0 | Suspend SHOP-A through the local platform-admin workflow while a customer station is open; attempt new customer work, then perform the allowed recovery/reactivation path. | New work is blocked by server policy on every surface; existing history/financial/physical-job recovery remains available only where authorised; reactivation is audited and does not change SHOP-B. |

## 8. Run order and evidence contract

### Required execution order

1. **Freeze local candidate:** record branch, exact commit SHA, app/API/Portal versions, Android APK identity, Windows executable hash and run ID.
2. **Automated local baseline:** run the complete local QA runner. It covers the real local Web/API journey, portal and .NET regression suites, Android JVM/instrumentation when prerequisites are present, Windows Agent tests and Windows Desktop build/startup smoke.
3. **Shop-owner Web:** execute WOWN-001–008 with OWNER-A and SHOP-A/SHOP-B.
4. **Customer Web:** execute WCUS-001–010 using guest and account contexts where supported.
5. **Windows Desktop:** execute WDSK-001–018 in the actual native window. A browser test or successful Desktop startup smoke is not a substitute.
6. **Android physical:** execute ANDX-001–022 on the selected QA phone/tablet. Emulator-only results do not close camera, file-provider, Local Network Access or physical shop-Wi-Fi acceptance.
7. **Cross-platform:** execute XPLAT-001–012 in order. Do not mix results from different candidate commits.
8. **Re-test:** every P0/P1 failure after the fix; run all relevant dependent journeys again; preserve first-failure evidence.

### Local automated command

From the existing Windows checkout, after reviewing local modifications and pulling the intended QA branch with fast-forward only:

~~~powershell
cd D:\Projects\QRtoPrint
git status --short
git pull --ff-only origin feature/smartprint-clean-qa-runtime
adb devices
.\scripts\qa\run-smartprint-qa.ps1 -LocalAndroidDeviceSerial "f3a153f1"
~~~

The explicit Android serial is the previously selected physical QA device. Change it only when the intended device is different. If no physical phone is connected, omit the serial and record physical Android cases as BLOCKED/NOT RUN; do not call them passed. Do not add the published-app download switch for a local-first run.

### Evidence required for every case

Case ID | Result | Exact commit/build | Role | Surface/device/OS | Steps actually performed | Expected vs actual | Local job/batch/station ID (non-secret) | Screenshot/video/log/API evidence path | Defect ID | Retest build/result | Tester/date

Evidence must prove persisted state and the visible experience where both matter. A 200 response, green dashboard tile, loaded screen, successful build or Agent heartbeat alone is insufficient. Keep a single evidence directory under the run's existing artifacts folder and redact secrets/customer data.

## 9. History-based session restoration and reprint acceptance

**Important distinction:** The current implementation supports continuity of an existing station session: Web checks the tab's station-scoped session token, then the Android native bridge's encrypted per-station token, validates it with the API, and restores the matching customer draft when its session marker matches. Android's Activity > Print history retrieves the signed-in customer's jobs and opens order details/timeline. The current Activity history view does **not** expose a command to restore an old session/draft or reprint a historical job. The Shop Print Queue's History action is an audit timeline, not a customer session restore.

These cases must not be marked PASS merely because the history list or order details open:

| ID | Priority | Scenario and procedure | Expected result |
|---|---:|---|---|
| HST-001 | P0 | Start a customer session at SHOP-A, upload a synthetic file or batch, configure its print settings, then reload the Web tab and force-stop/reopen the Android app while the original station session remains valid. | The same station-scoped session is resumed only after API validation; the saved draft is restored only when its session marker matches; original job/batch IDs, files, settings, upload idempotency and status are retained; no duplicate job/payment is created. If the token has expired/revoked, it is cleared and a safe fresh-session path is shown instead. |
| HST-002 | P1 | Sign in as CUSTOMER-ACCOUNT, open Android Activity > Print history, search/filter a prior job, open View details, then let the access token expire and repeat after a safe app restart. | History and details are fetched from the authenticated server; one safe token refresh is performed; batch/job identity, shop, payment/refund summary and event timeline match server state. Viewing history does not mutate, reset or re-open the job. |
| HST-003 | P1 | From customer Print history, attempt to restore an unfinished historical job/draft after the original browser tab/WebView/session has been closed. | **Current release capability check:** record NOT IMPLEMENTED because Activity history has View details but no Restore/Continue action or history-to-draft recovery contract. Do not simulate the action or count details viewing as restoration. If this capability is approved for implementation, its acceptance must restore the same authorised original job/batch and immutable settings without creating a second job, quote or payment, and must reject expired/revoked/foreign-customer sessions. |
| HST-004 | P1 | From a completed, rejected, refunded or cancelled history entry, look for a Reprint/Use these files action; where a future approved flow exists, initiate a reprint and compare old/new records. | **Current release capability check:** record NOT IMPLEMENTED if no explicit reprint action exists. A future reprint must create a new station/customer session and new PrintJob/quote/payment lifecycle; it must never reuse the old payment, old authorisation, old dispatch or mark the old job as reopened. Original history remains immutable. |
| HST-005 | P0 | Sign out, expire/revoke the station token, rotate/disable the old SHOP-A station code, then sign back in on the same or a different device and inspect customer history; separately try a different customer's job ID and SHOP-B context. | Account-linked history remains available only to the authenticated owning customer and returns only authorised job details. Old guest/session tokens cannot be resurrected from history or used after expiry/rotation; no stored document or draft is exposed to another account/shop; restoring access to a new print request requires a fresh valid station session. |
| HST-006 | P1 | Create more than 100 synthetic linked jobs across SHOP-A and SHOP-B, then fetch and display Android Activity history. Search the oldest and newest records and compare API ordering. | Current API behaviour is explicitly verified: it returns at most the latest 100 jobs in descending creation time, and the UI does not claim to show unlimited all-time history. Record the fixed limit as a product limitation; pagination/older-history retrieval requires implementation before promising complete history. |
| HST-007 | P0 | Let the original short-lived QR session expire after a customer account was attached; sign in with the same global customer and load Print history plus an old job's status. Also attempt to use the expired QR token for quote/upload/new-job operations. | Customer history and job status remain available through the global customer identity after QR-session expiry, as intended by the API; expired QR capabilities cannot create, quote, upload or mutate new print work. History access must not revive the old QR session. |
| HST-008 | P0 | Expire the customer access token and trigger overlapping operations: Activity history load, job-detail load and the native WebView customer-token bridge. Repeat with deliberately delayed responses. | Refresh-token rotation is coordinated or safely recovered: requests use the newest saved token, no valid customer is spuriously signed out, history/details do not intermittently fail because another request consumed the old refresh token, and only one active refresh-token chain remains. |
| HST-009 | P0 | Begin a slow history/detail request, sign out before it returns, and then allow the response to complete. Reopen Activity while signed out; sign in as a different QA customer. | No stale response is rendered after sign-out or into the next account's screen; credentials are cleared; the new account sees only its own jobs; local documents and shop guest-session storage are not deleted by account sign-out. |
| HST-010 | P0 | On one Android device, create resumable station sessions and drafts for two different shops/station codes. Switch between them, force-stop/reopen, and revisit each shop through its valid code. | Encrypted guest tokens and draft markers remain scoped to the exact station/shop; each shop resumes only its own session and draft; no job, file, quote or payment state from SHOP-A appears in SHOP-B. |
| HST-011 | P1 | Corrupt/truncate the Android encrypted account payload and separately the encrypted guest print-session payload; repeat after a QA-only Keystore key invalidation/recreation simulation. | Unreadable tokens are cleared safely and require sign-in or a fresh station scan; the app never crashes, exposes ciphertext/plaintext secrets or treats a corrupted token as valid; local files remain intact. |
| HST-012 | P0 | Test Web draft recovery after reload, browser tab closure/reopen, browser crash/session restore, malformed localStorage JSON, unavailable storage and quota-exceeded writes. | A draft is restored only with the matching validated session marker; it is never attached to a newly issued session merely because the station code matches. If recovery is impossible, the UI explains the safe next step; existing uploaded jobs are recoverable through signed-in history where linked, and no duplicate upload/payment is silently created. |
| HST-013 | P0 | Create a QR session and upload a guest job while signed out; then sign into CUSTOMER-ACCOUNT without creating a new QR session. Next create a fresh station session while signed in and compare account history. | Guest work created before authentication is not silently linked to the account; only sessions created with the authenticated customer identity are included in customer history, unless a separately approved explicit linking flow is introduced. |
| HST-014 | P1 | Sign into the same global customer on a second Android device. Open Activity and inspect jobs from both shops; then try to continue the first device's active guest draft without scanning a shop QR there. | Account-linked history and permitted job details are available on the second device; device-encrypted guest session tokens and local draft data are not copied across devices; a new guest print session requires a fresh valid station scan, with no cross-device token leakage. |

**Release interpretation:** HST-001 and HST-002 verify current session continuity and history viewing. HST-003 and HST-004 are explicit feature gaps, not test failures in code that does not claim to implement them; however, they are not passes. HST-006 records the current 100-job history limit. HST-008 and HST-009 are important race-condition/privacy gates. HST-013 protects the global account boundary. Product/release owners must decide whether history-based restore/reprint and older-history pagination are required for this release. HST-005, HST-007, HST-008, HST-009, HST-010, HST-012 and HST-013 are P0 security/data-continuity boundaries.

## 10. Release gate



A candidate is not accepted merely because CI is green or the automated local runner exits successfully.

- Every P0 case in the master plan and this supplement must PASS on the candidate, or be a confirmed NOT APPLICABLE with written approval. BLOCKED, NOT RUN, FAIL and NOT IMPLEMENTED are not passes.
- All 84 supplemental cases must have an explicit result. The native Windows Desktop cases must be run in the native window; Android physical cases must be run on real supported hardware for their hardware-dependent scenarios.
- No unresolved P0 defect is permitted. P1 issues require correction/retest or an explicit, time-bound risk decision by the release authority.
- No claims of complete manager/operator roles, email verification/recovery, live gateway payments/refunds, encrypted direct-LAN payloads, malware scanning, offline printing, universal printer support, signed release readiness or physical-device compatibility beyond the evidence actually collected.
- Any test involving an actual physical print must use synthetic content and a specifically designated QA printer. Output confirmation must be based on observed output, never spooler acceptance alone.
- The release record must distinguish automated CI, local Web/API E2E, emulator, physical Android, native Windows Desktop, Windows Agent/service and physical printer evidence.

**Current execution state:** This document defines additional acceptance work. Its cases are NOT RUN until evidence is recorded for the exact candidate. No physical-device, native Desktop end-to-end, shop-Wi-Fi or physical-printer result is implied by the existence of this plan.
