# Android Premium Document Studio — Enhancement Tracker

**Scope:** Native Android customer app in `apps/android`  
**Base:** `main` at `3a0e29c6d97228ebeba7c4da24e3023422920d35`  
**Working branch:** `feature/android-document-studio-premium-ux`  
**Status:** Initial Android implementation complete; follow-up product gaps remain

This tracker is the source of truth for the Android enhancement work. Update each status and its evidence as code, tests and device validation are completed. Do not mark an item complete merely because a screen or API call exists.

## Product outcome

**SmartPrint by QuantaEdge** is a premium document preparation and print handoff app. Customer-facing branding must say SmartPrint by QuantaEdge; QRtoPrint remains the repository and technical integration name only. A customer account is a global SmartPrint identity, never owned by a print shop or tenant. One account can connect to any participating shop by scanning its QR code. A customer can prepare a print collection at home, connect it to a participating print shop by scanning the shop QR code, configure the available print options in the existing customer workflow, and follow the existing payment and fulfilment lifecycle.

The Android app must continue to reuse the existing tenant/station-bound QR session, customer portal, API upload grants, server quote, shop approval, payment and output-confirmation workflow. Do not create a second print-job state machine or imply that printing works offline.

## Status legend

- `Not started` — not yet changed or validated in this workstream.
- `In progress` — implementation underway; acceptance evidence is incomplete.
- `Implemented` — code is present and automated checks pass; physical-device checks may still be outstanding.
- `Verified` — acceptance criteria have been exercised on supported Android devices/providers and evidence is recorded.
- `Blocked` — requires an external decision, service, credential, device or unsupported platform capability.

## Work tracker

| ID | Area | Scope and acceptance criteria | Status | Evidence / notes |
|---|---|---|---|---|
| AND-01 | Baseline and architecture | Audit current Android entry points, local library/store, QR scanner, portal bridge, manifest, dependencies, CI and tests before changing behaviour. Preserve existing capabilities and document any limits. | Implemented | Existing local library, multi-file/folder SAF transfer, native QR scanner and embedded portal bridge confirmed and retained. |
| AND-02 | Brand and visual system | Apply cohesive SmartPrint by QuantaEdge identity, app icon/wordmark treatment, refined typography, colour, surfaces, spacing, iconography, motion and accessible contrast. No arbitrary black bottom-nav container. Support system/light/dark themes consistently. | In progress | SmartPrint app label, launcher mark, wordmark, light/dark palette, custom vector navigation/account icons and icon-led Scan & Print steps are implemented. Full contrast, motion, font-scale and physical light/dark review remains. |
| AND-03 | Navigation and information architecture | Replace the current three-icon treatment with a balanced task-based structure: Home, My Files, Scan & Print, Activity. Keep account/profile action at top right. Avoid duplicate routes and wasted space. | Implemented | Four task destinations with custom vector icons, balanced surface, selected/inactive states, explicit accessibility labels and top-right SmartPrint account entry. Android 15 emulator UI test passes. |
| AND-04 | Global customer account | Keep core document preparation and shop printing usable without registration. Add SmartPrint-global customer sign-in/sign-up, distinct from shop-owner accounts. A customer identity has no tenant/shop ownership and works at every participating shop. Keep local guest documents separate from account identity. | Implemented | Separate global customer role/API, signup/login, refresh flow, Android Keystore AES-GCM token storage, trusted-origin/top-frame WebView messaging and cross-shop session association/history are implemented. API identity tests and account-screen/Keystore emulator tests pass. Only sessions started while signed in are linked; guest history is not retroactively attached. |
| AND-05 | Document capture | Add a dedicated document camera flow for paper pages and photos, with permission rationale, live framing/boundary guidance, automatic capture/crop/perspective correction where supported, and a manual correction fallback. Capture multiple pages into one document. QR scanning remains a separate camera mode. | Implemented | Google ML Kit Document Scanner is integrated in full mode for up to 50 pages and PDF/JPEG outputs; its on-device UI supplies document detection, crop/perspective correction, rotate, page ordering and filters. Save rollback is implemented. Requires Google Play services scanner module (may download first use); physical camera acceptance remains open. |
| AND-06 | Page editing | Support page preview, corner/crop adjustment, rotate, reorder, delete/retake, straighten, filters (colour/grayscale/B&W) and safe save. Never overwrite the original source silently; make edits reversible or retain the source. | In progress | Scan-time crop/perspective correction, rotation, page reordering and filters are provided by the scanner UI; imported saved documents can be opened/shared, but a persistent in-app editor for previously imported pages, independent resize controls and full undo/version history remain outstanding. |
| AND-07 | Document library and collections | Provide a first-class Print Collection concept. Manage files and folders/subfolders; rename, move, reorder by drag, preview, multi-select, merge where supported, and preserve the intended print order. | Implemented | Existing app-private `LocalDocumentStore` now supports nested folders, rename/delete/share/open, move, drag/manual reorder, multi-select and stable visible order without changing filenames. Local collections are prepared in one library. Integrated thumbnail/document preview remains a follow-up. |
| AND-08 | Multi-file import | Import multiple supported files and folder trees using Android Storage Access Framework; report skipped/unsupported files and per-file failures; enforce transfer budgets; preserve names, MIME types, relative paths and order where available. | Implemented | Android SAF multi-file and directory-tree import retained and audited; bounded folder count/depth and per-file failure reporting are preserved. The app also accepts shared files from other Android apps. |
| AND-09 | Export and conversion | Export one or many files/folders to user-selected destinations. Support only conversions implemented and validated (initial target: images to PDF and multiple images to one PDF; PDF page operations where supported). Preserve originals and clearly disclose unsupported conversions. | In progress | Single-file export, folder-tree export, multi-file ZIP export and tested JPG/JPEG/PNG-to-multipage-PDF conversion are implemented; source images remain unchanged. Merging existing PDFs, extracting/rearranging saved PDF pages and Office document conversions are not implemented. |
| AND-10 | Scan & Print journey | Redesign the screen as one connected journey: prepare documents → scan shop QR → configure print → quote/pay → track/collect. Explain in crisp steps with icons/flow, clarify that shop QR is displayed at a participating shop, and make the primary CTA clear. | Implemented | Dedicated Scan & Print screen, custom icon-led Prepare → Connect → Review & collect steps, explicit explanation that the customer scans the code displayed at a participating shop, and primary shop-QR action. Emulator UI test passes. |
| AND-11 | QR shop connection | Keep QR decoding on-device and validate only the supported station route/origin rules. Explain what is being scanned, what connection is created, permissions and fallback paths. Preserve existing HTTPS and LAN security policy. | Implemented | Existing CameraX/ML Kit QR scanner and strict station-link parser are retained. QR camera permission explanation is emulator-tested; shop QR decoding and LAN/Cloudflare path still require physical shop-device verification. |
| AND-12 | Print configuration continuity | After shop connection, make the prepared collection available to the existing print workflow without bypassing server validation, quote, shop review, payment or dispatch controls. Be explicit about formats and size/page limits. | In progress | Existing station-bound customer portal, quote, shop approval, payment and dispatch lifecycle is reused; global customer identity is added to newly created sessions. Current API/portal/Agent contract remains one document per print job: a prepared multi-file collection cannot yet be sent as one batch print order. Selected photos can be merged to one PDF first. |
| AND-13 | Activity and cross-shop account history | Present global SmartPrint customer profile and print history across all shops. Only list jobs whose print sessions are linked to the signed-in customer ID; guest jobs and sessions created before sign-in remain unlinked unless an explicit account-linking flow is added. | Implemented | Global SmartPrint profile and cross-shop jobs API/history view implemented. History is queried by global customer ID across tenants; API and account UI tests pass. Guest sessions and sessions started before sign-in are not auto-linked. |
| AND-14 | Accessibility and responsive quality | Validate touch target sizes, talkback labels, contrast, font scaling, keyboard/IME behaviour, edge-to-edge insets, small devices and landscape where relevant. | In progress | Primary navigation and account action have explicit accessible labels; emulator verifies account/navigation entry and basic flows. Full TalkBack, AA contrast, font scaling, small-device, landscape and keyboard review remains. |
| AND-15 | Automated quality gates | Add unit/UI tests for navigation, collection ordering, import/export safety, document/page operations, scanner permission/error states and portal handoff. Run Android unit tests, emulator instrumentation, lint/build and repo CI checks. | Implemented | GitHub Actions CI run [41216111944](https://github.com/Manav326/QRtoPrint/actions/runs/41216111944) passed on code SHA `e2e479b43c1f3a0074ca19c4e243e8729c5b8245`: portal typecheck/build, API build/tests, Windows Agent build/local queue tests, Android JVM tests/debug APK assembly and Android 15 (API 35) emulator UI tests. Test reports and APK artifacts uploaded. |
| AND-16 | Real-device acceptance | Exercise camera capture, QR scan, Android 8+ compatibility, photo/PDF/file-manager providers, multi-file folder trees, export, WebView file selection, shop Wi-Fi/Cloudflare and Agent upload. Record device/OS/provider and outcomes. | Not started | No physical Android handset, shop Wi-Fi, live scanner provider, payment or printer session has been exercised in this pass. Emulator success is not physical-device acceptance. |
| AND-17 | Documentation and release boundary | Update Android README, product blueprint/design/roadmap if scope changes; describe formats, permissions, guest/account behaviour, limits and verified/unverified device matrix. Keep Play Store/release signing claims accurate. | Implemented | Android README documents SmartPrint identity, scanner requirements, SAF import/export, account/session model, limits, build command and release/device-test boundary. Play Store signing and distribution remain unapproved. |

## Implementation sequence

1. Finish AND-01 audit and capture baseline limitations.
2. Define the shared Android design tokens and navigation shell (AND-02/03).
3. Implement document capture and editing using the least-fragile supported Android approach (AND-05/06).
4. Unify imported/scanned content around Print Collections and complete safe import/export (AND-07/08/09).
5. Redesign Scan & Print and connect the collection to the existing secure workflow (AND-10/11/12).
6. Add truthful guest/profile/activity experiences (AND-04/13).
7. Complete automated, accessibility, emulator and physical-device evidence (AND-14/15/16); update docs (AND-17).


## Latest verified CI evidence

- **Successful workflow:** [GitHub Actions run 41216111944](https://github.com/Manav326/QRtoPrint/actions/runs/41216111944)
- **Code revision tested:** `e2e479b43c1f3a0074ca19c4e243e8729c5b8245`
- **Result:** all workflow jobs passed, including Android 15/API 35 emulator UI tests.
- **Artifacts:** `qrtoprint-android-debug`, `qrtoprint-android-test-reports`, `qrtoprint-agent-win-x64`.

### Explicit follow-up gaps

1. A single print job still accepts one uploaded document. A true multi-document print batch needs a coordinated PrintJob/PrintDocument API, portal upload, quote, shop-review and Windows Agent contract change; current collections are local preparation structures, not a server batch order.
2. Existing imported PDFs/photos do not yet have a persistent in-app page editor. Scanner-time editing is provided by Google ML Kit Document Scanner; saved-file PDF merge/extraction and Office conversions remain unsupported.
3. Verify scanner-module installation/runtime, camera capture, QR scans, SAF provider behaviour, dark/light themes, font scaling, shop Wi-Fi/tunnel, account use at multiple real shops and actual printer output on physical devices.
4. Customer sessions are associated with the SmartPrint account when started while signed in. Guest sessions or earlier sessions are not retroactively associated. Email verification and password recovery are not part of this implementation.

CI/emulator validation is development evidence, not Play Store/release approval.

## Guardrails

- Preserve original files and user data. Use additive migrations/storage changes only when required and tested.
- No fake sign-in/sign-up or local-only UI that appears to authenticate against a server.
- No silent format conversion, file deletion, page omission, sequence change or print-settings substitution.
- No customer-facing document transfer over insecure arbitrary HTTP. Keep the existing tightly scoped private-LAN Agent exception and HTTPS defaults.
- Document capture and shop QR scanning are distinct operations but parts of the same document-to-print journey.
- A CI APK is not production release approval; physical Android/provider/shop-Wi-Fi validation remains explicit.
