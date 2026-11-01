# Android Premium Document Studio — Enhancement Tracker

**Scope:** Native Android customer app in `apps/android`  
**Base:** `main` at Android merge commit `d37dda3ebb7ff3e1339ddceaf2a1da21ac71049a`  
**Working branch:** `feature/android-multi-file-batch-handoff`  
**Status:** Implementation expanding; CI and physical-device acceptance tracked per revision

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
| AND-02 | Brand and visual system | Apply cohesive SmartPrint by QuantaEdge identity, app icon/wordmark treatment, refined typography, colour, surfaces, spacing, iconography, motion and accessible contrast. No arbitrary black bottom-nav container. Support system/light/dark themes consistently. | In progress | Native app label, wordmark, launcher mark and initial palette updated; full screen and dark-mode verification remains. |
| AND-03 | Navigation and information architecture | Replace the current three-icon treatment with a balanced task-based structure: Home, My Files, Scan & Print, Activity. Keep account/profile action at top right. Avoid duplicate routes and wasted space. | In progress | Four-destination navigation and top-right guest profile entry implemented; needs emulator regression and visual refinement. |
| AND-04 | Global customer account | Keep core document preparation and shop printing usable without registration. Add SmartPrint-global customer sign-in/sign-up, distinct from shop-owner accounts. A customer identity has no tenant/shop ownership and works at every participating shop. Keep local guest documents separate from account identity. | In progress | Added dedicated customer registration/login, customer claim, refresh-token support, Keystore-encrypted Android token storage and top-frame/origin-restricted WebView token messaging. Final build, API tests and end-to-end validation pending. |
| AND-05 | Document capture | Add a dedicated document camera flow for paper pages and photos, with permission rationale, live framing/boundary guidance, automatic capture/crop/perspective correction where supported, and a manual correction fallback. Capture multiple pages into one document. QR scanning remains a separate camera mode. | In progress | Integrated Google ML Kit Document Scanner (PDF + JPEG, multi-page, full scanner mode). SDK supplies detection/crop/rotate/reorder/filter UI on-device; scanner module is delivered through Google Play services and may download on first use. |
| AND-06 | Page editing | Support page preview, corner/crop adjustment, rotate, reorder, delete/retake, straighten, filters (colour/grayscale/B&W) and safe save. Never overwrite the original source silently; make edits reversible or retain the source. | In progress | Saved-PDF editor supports preview, rotate, reorder, remove and save-as-copy; rasterized editing and current-revision test validation remain. |
| AND-07 | Document library and collections | Provide a first-class Print Collection concept. Manage files and folders/subfolders; rename, move, reorder by drag, preview, multi-select, merge where supported, and preserve the intended print order. | In progress | Nested library, multi-select and local PDF composition exist; a first-class collection entity and direct multi-file batch handoff remain. |
| AND-08 | Multi-file import | Import multiple supported files and folder trees using Android Storage Access Framework; report skipped/unsupported files and per-file failures; enforce transfer budgets; preserve names, MIME types, relative paths and order where available. | In progress | Existing SAF import remains. Native WebView picker now supports a true multi-file selection across app-library subfolders and returns the original file URIs in selection order; provider interoperability and CI validation are pending. |
| AND-09 | Export and conversion | Export one or many files/folders to user-selected destinations. Support only conversions implemented and validated (initial target: images to PDF and multiple images to one PDF; PDF page operations where supported). Preserve originals and clearly disclose unsupported conversions. | In progress | Images/PDFs can be composed into a print PDF; saved PDF page edits save a separate rasterized copy. Lossless/vector-preserving and Office conversions are not implemented. |
| AND-10 | Scan & Print journey | Redesign the screen as one connected journey: prepare documents → scan shop QR → configure print → quote/pay → track/collect. Explain in crisp steps with icons/flow, clarify that shop QR is displayed at a participating shop, and make the primary CTA clear. | In progress | Native Scan & Print landing flow with concise three-step explanation and shop-QR CTA added; emulator testing remains. |
| AND-11 | QR shop connection | Keep QR decoding on-device and validate only the supported station route/origin rules. Explain what is being scanned, what connection is created, permissions and fallback paths. Preserve existing HTTPS and LAN security policy. | Not started | Existing QR scanner and station-link validation must remain intact. |
| AND-12 | Print configuration continuity | After shop connection, make the prepared collection available to the existing print workflow without bypassing server validation, quote, shop review, payment or dispatch controls. Be explicit about formats and size/page limits. | In progress | Native multi-select now hands original document URIs to the existing portal input when it requests multiple files. The portal uploads one file per request under one batch ID; quote, payment, shop review and dispatch remain server-owned. CI and device validation are pending. |
| AND-13 | Activity and cross-shop account history | Present global SmartPrint customer profile and print history across all shops. Only list jobs whose print sessions are linked to the signed-in customer ID; guest jobs and sessions created before sign-in remain unlinked unless an explicit account-linking flow is added. | In progress | Added customer profile/jobs endpoints and Android history screen; build and account-to-multiple-shop validation pending. |
| AND-14 | Accessibility and responsive quality | Validate touch target sizes, talkback labels, contrast, font scaling, keyboard/IME behaviour, edge-to-edge insets, small devices and landscape where relevant. | Not started | |
| AND-15 | Automated quality gates | Add unit/UI tests for navigation, collection ordering, import/export safety, document/page operations, scanner permission/error states and portal handoff. Run Android unit tests, emulator instrumentation, lint/build and repo CI checks. | In progress | JVM and emulator tests cover navigation, account storage, mixed PDF/image composition and saved-PDF editing. Run full CI on the synced commit before marking complete. |
| AND-16 | Real-device acceptance | Exercise camera capture, QR scan, Android 8+ compatibility, photo/PDF/file-manager providers, multi-file folder trees, export, WebView file selection, shop Wi-Fi/Cloudflare and Agent upload. Record device/OS/provider and outcomes. | Not started | CI/emulator evidence is not physical-device proof. |
| AND-17 | Documentation and release boundary | Update Android README, product blueprint/design/roadmap if scope changes; describe formats, permissions, guest/account behaviour, limits and verified/unverified device matrix. Keep Play Store/release signing claims accurate. | In progress | README/tracker updated for main's multi-file batch capability and the Android compatibility-helper limitation; verify CI and device matrix. |

## Implementation sequence

1. Finish AND-01 audit and capture baseline limitations.
2. Define the shared Android design tokens and navigation shell (AND-02/03).
3. Implement document capture and editing using the least-fragile supported Android approach (AND-05/06).
4. Unify imported/scanned content around Print Collections and complete safe import/export (AND-07/08/09).
5. Redesign Scan & Print and connect the collection to the existing secure workflow (AND-10/11/12).
6. Add truthful guest/profile/activity experiences (AND-04/13).
7. Complete automated, accessibility, emulator and physical-device evidence (AND-14/15/16); update docs (AND-17).


## Latest verified CI evidence

- **Workflow:** [GitHub Actions run 41216111944](https://github.com/Manav326/QRtoPrint/actions/runs/41216111944)
- **Revision:** `e2e479b43c1a3f0074ca19c4e243e8729c5b8245`
- **Result:** workflow completed successfully, including Android 15 (API 35) emulator UI tests.
- **Artifacts uploaded by this historical run:** `qrtoprint-android-debug`, `qrtoprint-android-test-reports` and `qrtoprint-agent-win-x64`. The current CI workflow uses `smartprint-android-debug` and `smartprint-android-test-reports` for Android artifacts.

## Known product follow-ups

1. Main provides server-side multi-document batches and shop-side per-file review/refund. The native WebView chooser now supports multiple original files across local folders. It must pass the new branch's CI and physical Android/provider tests before this integration can be considered verified.
2. Saved PDFs now have an in-app page editor (preview, rotate, reorder, remove, save-as-copy). It rasterizes pages; lossless/vector-preserving edits, extraction to separate files, saved-photo editing and Office conversions remain outstanding.
3. Exercise real camera capture, QR scanning, Google Play services scanner availability, file-manager providers, Android-version compatibility, light/dark and accessibility behaviour, shop Wi-Fi/tunnel, live account use across multiple shops, payment and actual printer output on physical devices.
4. Customer sessions are linked to the global account only when a customer starts the QR session while signed in. Guest and earlier sessions are not retroactively linked; email verification and password recovery are not implemented here.

CI/emulator success is development evidence, not production release or Play Store approval.


## Current batch/editor implementation (validation pending)

- When the customer portal requests multiple files, Android now presents a multi-select view of the private document library. Customers can browse nested folders without losing prior selections, then attach the selected original PDF/JPG/JPEG/PNG files as distinct URIs in selection order.
- The existing portal then uploads each original file separately with one shared batch ID. Each file remains an independently reviewable print job; Android does not flatten the selection into one combined document for this route.
- The native picker validates the API's current 20-file, 20 MB per-file and 50 MB aggregate limits, rejects duplicate paths and skips missing/unsupported files. Unit tests cover these boundaries; branch CI is still pending.
- The separate **Combine into one PDF** helper remains a compatibility conversion action that combines selected PDFs/photos into one PDF. It is not the true multi-file batch route.
- The final combined PDF is capped at 20 MB and 500 pages because those are the current print-job limits. Source selection can total 100 MB, but output must meet the upload limit.
- PDF pages are rasterized up to a 2,000-pixel long edge when combined or edited. This is not a lossless/vector-preserving merge.
- Saved PDFs can be previewed, rotated, reordered, have pages removed, and saved as a new copy; the original is never overwritten.
- Existing emulator coverage includes mixed PDF/image composition and saved-PDF page editing. The merged baseline passed CI; the new multi-file chooser and selection-policy tests still require a fresh branch CI run.

## Current branch implementation evidence

- Android file chooser reads `FileChooserParams.MODE_OPEN_MULTIPLE` and retains ordered selections while navigating the app's nested folders.
- Original file URIs are returned to the existing WebView file-input callback; the existing customer portal performs separate uploads with the same batch ID and handles progress/retry.
- No API state machine, price calculation, upload grant, payment, refund, shop review, Agent dispatch or physical-output workflow was duplicated or bypassed.

## Guardrails

- Preserve original files and user data. Use additive migrations/storage changes only when required and tested.
- No fake sign-in/sign-up or local-only UI that appears to authenticate against a server.
- No silent format conversion, file deletion, page omission, sequence change or print-settings substitution.
- No customer-facing document transfer over insecure arbitrary HTTP. Keep the existing tightly scoped private-LAN Agent exception and HTTPS defaults.
- Document capture and shop QR scanning are distinct operations but parts of the same document-to-print journey.
- A CI APK is not production release approval; physical Android/provider/shop-Wi-Fi validation remains explicit.
