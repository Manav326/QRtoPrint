# Android customer-app hardening checklist

Scope: the SmartPrint Android application remains customer-facing. Shop staff continue to use the shop web portal and Windows desktop application.

## Implementation checklist

### Security and session continuity
- [x] Bind production QR station URLs to the configured SmartPrint service origin.
- [x] Keep the native WebView message bridge pinned to the configured origin; never trust a host supplied by a QR code.
- [x] Persist guest print-session tokens encrypted with Android Keystore and scope them by station.
- [x] Persist non-secret active-job drafts per station so a restarted WebView can restore uploaded jobs.
- [x] Preserve WebView state on Android Activity recreation.
- [x] Add an explicit switch-shop flow without discarding another shop's active session.

### Customer print journey
- [x] Save one PDF by default from document scanning; do not also expose duplicate page-image derivatives in the same folder.
- [x] Refresh and display status for every file in a multi-file batch.
- [x] Make customer navigation/back handling return to the previous screen.
- [x] Explain image-based PDF conversion before combine/edit operations.
- [x] Add useful date and job context to account activity/history.

### Library, accessibility and reliability
- [x] Add library search/filter.
- [x] Replace free-form move-to-folder path entry with a folder picker.
- [x] Show document-library storage usage and available device space.
- [x] Add free-space preflight protection to imports.
- [x] Improve minimum touch targets and small supporting text.
- [ ] Tighten or explicitly constrain cleartext networking without breaking private-LAN Agent upload.

### Release validation
- [ ] Add unit/instrumented tests for QR origin validation, encrypted guest-session storage, shop switching and navigation.
- [ ] Pass Android unit tests, Android emulator tests, portal build/tests and full CI.
- [ ] Complete physical Android, camera/scanner, file-provider, process-kill recovery, shop Wi-Fi, payment and real-printer acceptance.

## Dependencies / not safe to fake
- Customer email verification and password recovery are not implemented in the current API. The API currently creates unconfirmed customer accounts but has no customer verification/reset-token endpoints or configured email delivery contract. Do not display a working-looking reset flow until backend token issuance, delivery, expiry, rate limits and redemption are implemented and tested.
- Account history currently returns only job number/status/page count/copies/currency/quoted total/date/shop. Paid/refunded/net amounts and original filename must come from an expanded tenant-safe customer-history API before they can be truthfully displayed.
- The Android app's cleartext exception currently supports dynamic private IPv4 Agent endpoints. Android Network Security Config cannot safely express an arbitrary private IPv4 range as a static exception. Moving Agent bytes through a native, policy-checked transport is the clean route to turn off global cleartext permission; do not break local printing by changing only the manifest.
- Physical-device and shop-network acceptance cannot be simulated by repository CI.
- Guest-session recovery is bounded by the API's existing print-session expiry. Android can restore a stored token after process death while the server still accepts it. If the app is not reopened before expiry, the current API does not expose an expired-guest-job recovery capability; this requires a backend recovery token or explicit customer-account linking flow.
