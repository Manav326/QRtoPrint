# SmartPrint by QuantaEdge — Android

The customer-facing app is **SmartPrint by QuantaEdge**. QRtoPrint remains the repository and technical integration name. The Android app is a customer companion to the existing portal and API. It does not run its own pricing, payment or print-dispatch backend.

## Local document library

- Documents are copied into app-private storage under `files/documents`.
- Create nested folders, move files/folders between directories, and reorder sibling items without renaming customer files.
- Select several local photos and combine them into a PDF; original images remain unchanged.
- Create nested folders, import multiple files, or import a directory tree using Android's Storage Access Framework.
- Export one file or an entire folder tree into a user-selected Android File Manager destination. Multiple selected files export together as a ZIP archive that preserves their original filenames.
- Open/share files with other Android apps.
- The customer portal's native file picker can select one saved PDF/JPG/JPEG/PNG or attach multiple original files together; multi-file selections can span this app's nested folders. The library includes a current-folder search, a folder picker for moving items, a document-storage summary and an import free-space reserve check.

## Print batches and saved PDF editing

**Separate PDF merge helper:** Select 2–20 PDF, JPG, JPEG or PNG files in one library folder and choose **Combine into one PDF**. The app combines them in displayed selection order into one PDF and preserves the originals. PDF pages are rasterized at up to a 2,000-pixel long edge; each image becomes one page. The result is limited to 20 MB and 500 pages; source selection may total up to 100 MB. This helper creates one uploaded document, not a true multi-file server-side collection. Use the portal's **Select multiple files** input for separate print jobs in one batch.

**Server-side batch capability now present in main:** The portal/API/Windows Agent workflow has been extended to handle a multi-file print batch, with shop-side per-file review and rejection/refund handling. The native WebView picker now recognises when the portal requests multiple files, keeps selection while browsing nested local folders, and returns original file URIs in selection order. The existing portal uploads each file separately with one shared batch ID, using the shop's current upload/quote/review/payment/dispatch lifecycle. CI and physical-device/provider acceptance for this integration are still pending.

Saved PDFs also expose **Edit PDF pages** from the file menu. Customers can preview a page, rotate by 90 degrees, move pages up/down, remove unwanted pages and save a new edited copy. The original PDF is never overwritten. The editor uses Android's PDF renderer and rasterizes edited pages, so this is a convenient document-correction workflow rather than lossless vector PDF editing. The in-app warning now explains that selectable text, vector content and some fine detail can be lost.

- 
General library imports allow up to 100 MB per file. The print workflow uses the existing server-supported single-document types and 20 MB hard limit. Files remain on the device until the customer chooses to upload one.

## SmartPrint document capture

The native document-capture action uses Google ML Kit Document Scanner in full mode and requests both PDF and JPEG outputs. The scanner supports multi-page capture and its on-device UI provides document-edge detection, crop/perspective correction, rotation, page ordering and image filters. Google Play services downloads the scanner UI/models when needed; the first launch can require a download, and devices below the scanner's 1.7 GB RAM requirement are not supported. If unavailable, customers can still import photos/PDFs through Android's file picker. Scanned PDF and JPEG pages are saved in a timestamped folder in My files.

## Global SmartPrint customer account

SmartPrint customer accounts are global and independent of any print shop. A customer registers once with SmartPrint, then uses the same account at any participating shop by scanning that shop's QR code. Shops are session destinations, not customer-account owners. New authenticated QR sessions carry the global customer ID; account print history is aggregated across shops by that ID. Guest customers can still scan, organise and print without registration. Existing guest sessions and print jobs are not silently attached to an account later.

Customer identity is separate from shop staff identity and uses dedicated `/api/v1/auth/register-customer` and `/api/v1/auth/customer-login` endpoints. Android access/refresh tokens and guest station-session tokens are encrypted with Android Keystore AES-GCM. Guest tokens are stored per station, so switching shops does not overwrite another shop's resumable session. The browser persists only non-secret job metadata per station. The WebView does not expose credentials through a generic JavaScript interface: AndroidX WebKit messaging is restricted to the configured top-level service origin and main frame, never to a host supplied by a scanned QR code.

## QR and print flow

The QR station code format is matched to the API's current generator: 24 random bytes encoded as a 32-character Base64URL path segment. The app validates the host against the configured smartPrintServiceUrl HTTPS origin, as well as the route, port, query and fragment. Production QR codes from any other host are rejected. A debug build may be configured with a private IPv4 HTTP service origin on port 3000; the scanned station URL must still match that exact configured origin, and the WebView credential bridge remains pinned to it.

The CameraX preview feeds frames to bundled ML Kit Barcode Scanning locally. No camera frame is uploaded or retained. Only a decoded QR payload leaves the scanner, and it is accepted only when it matches the HTTPS `/s/{publicCode}` station route.

The embedded customer portal then creates the same station-bound session as the PWA. Cloud-assisted and local Agent upload share the existing API, quote, shop approval, payment and dispatch lifecycle. Local Agent upload is allowed only to private IPv4 addresses on TCP 17842 and its upload path; the customer portal itself remains HTTPS.

## Build

From repository root:

```bash
gradle --no-daemon -p apps/android :app:testDebugUnitTest :app:assembleDebug
```

Build the Android app with the service origin configured in one place:

```bash
$env:SMARTPRINT_PUBLIC_BASE_URL = "<the canonical HTTPS origin configured for this deployment>"
gradle --no-daemon -p apps/android -PsmartPrintServiceUrl="$env:SMARTPRINT_PUBLIC_BASE_URL" :app:testDebugUnitTest :app:assembleDebug
```

The build requires an explicit `smartPrintServiceUrl` Gradle property or `SMARTPRINT_PUBLIC_BASE_URL` environment variable. It intentionally has no baked-in service-host default, preventing test and production APKs from silently targeting the wrong deployment. The CI workflow uploads the debug APK as `qrtoprint-android-debug`. Download it with `scripts\\download-android-apk.ps1`; add `-Install` to install it on one connected, ADB-authorised Android device. Debug builds are for development only. Release signing, physical Android/Wi-Fi validation, Play Store listing and distribution are not yet complete.

The native Activity screen shows account-linked job dates, original filenames, quoted amounts, payment method/status, paid/refunded/net amounts, rejected pages and output-confirmation time. The API returns these fields through tenant-scoped document/payment queries; account history remains limited to the latest 100 jobs.

The CI Android job also uploads JVM and emulator test reports as `qrtoprint-android-test-reports`, including JUnit XML and the connected-test HTML report when generated. Use the report artifact to inspect individual test cases and failures from a CI run.

## Automated Android UI tests

The CI Android job also launches an Android 15 (API 35) emulator and runs
`connectedDebugAndroidTest`. Current instrumented coverage includes SmartPrint home/navigation, global guest-account entry, Scan & Print explanation, nested-folder operations, scanner permission explanation, encrypted account-token storage, mixed PDF/image batch composition and saved-PDF page edits. The merged Android baseline passed CI; each subsequent branch change must pass CI before it is treated as verified. This remains emulator/API/unit coverage; it does not replace physical-device camera, Google Play services scanner, file-manager provider, shop Wi-Fi, payment or printer validation.


## Customer-app recovery and hardening

- The Scan & Print destination shows the current shop session and offers an explicit **Scan another shop** action. The toolbar also exposes **Switch shop** when connected.
- The active WebView state is saved on Activity recreation. Guest station tokens are persisted in encrypted Android Keystore storage; active-job drafts are stored per station without storing the bearer token in browser storage.
- Multi-file batch items refresh their own server status instead of presenting every non-selected item as merely Uploaded.
- Customer account navigation returns to the destination from which the account was opened. The Activity list shows the job creation date.
- The native PDF combine/editor disclosures explain rasterization and possible quality/searchability loss.

Email verification/password recovery are not exposed as functional controls yet because the current API has no customer reset/verification-token endpoints or email-delivery contract. The account activity API also needs additional fields before the app can show actual paid/refunded/net amounts and original filenames. See docs/ANDROID_CUSTOMER_APP_HARDENING.md for the remaining backend and physical-device gates.
