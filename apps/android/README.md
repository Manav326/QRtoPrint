# SmartPrint by QuantaEdge — Android

The customer-facing app is **SmartPrint by QuantaEdge**. QRtoPrint remains the repository and technical integration name. The Android app is a customer companion to the existing portal and API. It does not run its own pricing, payment or print-dispatch backend.

## Local document library

- Documents are copied into app-private storage under `files/documents`.
- Create nested folders, move files/folders between directories, and reorder sibling items without renaming customer files.
- Select several local photos and combine them into a PDF; original images remain unchanged.
- Create nested folders, import multiple files, or import a directory tree using Android's Storage Access Framework.
- Export one file or an entire folder tree into a user-selected Android File Manager destination. Multiple selected files export together as a ZIP archive that preserves their original filenames.
- Open/share files with other Android apps.
- The customer portal's file picker can select a saved PDF/JPG/JPEG/PNG from this library.

General library imports allow up to 100 MB per file. The print workflow uses the existing server-supported single-document types and 20 MB hard limit. Files remain on the device until the customer chooses to upload one.

## SmartPrint document capture

The native document-capture action uses Google ML Kit Document Scanner in full mode and requests both PDF and JPEG outputs. The scanner supports multi-page capture and its on-device UI provides document-edge detection, crop/perspective correction, rotation, page ordering and image filters. Google Play services downloads the scanner UI/models when needed; the first launch can require a download, and devices below the scanner's 1.7 GB RAM requirement are not supported. If unavailable, customers can still import photos/PDFs through Android's file picker. Scanned PDF and JPEG pages are saved in a timestamped folder in My files.

## Global SmartPrint customer account

SmartPrint customer accounts are global and independent of any print shop. A customer registers once with SmartPrint, then uses the same account at any participating shop by scanning that shop's QR code. Shops are session destinations, not customer-account owners. New authenticated QR sessions carry the global customer ID; account print history is aggregated across shops by that ID. Guest customers can still scan, organise and print without registration. Existing guest sessions and print jobs are not silently attached to an account later.

Customer identity is separate from shop staff identity and uses dedicated `/api/v1/auth/register-customer` and `/api/v1/auth/customer-login` endpoints. Android access/refresh tokens are encrypted with Android Keystore AES-GCM. The WebView does not expose the long-lived token to page JavaScript through a generic JavaScript interface: AndroidX WebKit messaging is restricted to the trusted top-level shop origin and main frame.

## QR and print flow

The QR station code format is matched to the API's current generator: 24 random bytes encoded as a 32-character Base64URL path segment. The app still validates the host, HTTPS scheme, route, port, query and fragment before opening the portal.

The CameraX preview feeds frames to bundled ML Kit Barcode Scanning locally. No camera frame is uploaded or retained. Only a decoded QR payload leaves the scanner, and it is accepted only when it matches the HTTPS `/s/{publicCode}` station route.

The embedded customer portal then creates the same station-bound session as the PWA. Cloud-assisted and local Agent upload share the existing API, quote, shop approval, payment and dispatch lifecycle. Local Agent upload is allowed only to private IPv4 addresses on TCP 17842 and its upload path; the customer portal itself remains HTTPS.

## Build

From repository root:

```bash
gradle --no-daemon -p apps/android :app:testDebugUnitTest :app:assembleDebug
```

The CI workflow uploads the debug APK as `qrtoprint-android-debug`. Download it with `scripts\\download-android-apk.ps1`; add `-Install` to install it on one connected, ADB-authorised Android device. Debug builds are for development only. Release signing, physical Android/Wi-Fi validation, Play Store listing and distribution are not yet complete.

The CI Android job also uploads JVM and emulator test reports as `qrtoprint-android-test-reports`, including JUnit XML and the connected-test HTML report when generated. Use the report artifact to inspect individual test cases and failures from a CI run.

## Automated Android UI tests

The CI Android job also launches an Android 15 (API 35) emulator and runs
`connectedDebugAndroidTest`. Current instrumented coverage includes the
document-library landing screen, nested-folder navigation and the explicit
camera-permission explanation screen. This is emulator UI coverage; it does
not replace physical-device QR camera, file-manager provider, Wi-Fi Agent or
printer validation.
