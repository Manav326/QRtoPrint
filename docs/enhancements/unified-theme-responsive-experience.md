# Enhancement: Unified theme and responsive SmartPrint experience

**Status:** In progress  
**Implementation branch:** `feature/unified-theme-responsive-experience`  
**Base:** `main` at `7070b4cb0ad510e98bdd78e218dcb7108f61f01d`

## Product requirements

- Light theme is the first-install/default choice.
- Users can explicitly select Light or Dark.
- Theme choice is global across public landing, customer QR flow, shop-owner portal, platform-admin portal, login screens and Windows desktop.
- Theme choice persists across refresh, route navigation, sign-out/sign-in and app restart.
- Appearance preference is stored independently of authentication/session credentials.
- Native Android offers an in-app Appearance setting; its preference also governs the native scanner and the embedded shop WebView.
- Text, form controls, cards, statuses and navigation remain readable in both themes.
- Every route remains usable on phone, tablet, laptop and desktop; no clipped or overlapping core controls.
- Desktop navigation uses a collapsible sidebar with independently scrollable navigation and workspace areas. Mobile retains a drawer pattern; Android retains native navigation and its glass surfaces.

## Implementation checklist

### Shared web theme
- [x] Centralize theme state at the portal root.
- [x] Default missing theme preference to Light.
- [x] Persist selection in localStorage independently of auth/session storage.
- [x] Restore saved selection before first paint on portal and desktop entry pages.
- [x] Reuse one accessible theme toggle on landing, shop, platform admin, login and customer QR routes.
- [ ] Confirm logout/login and refresh flows with automated browser tests.

### Visual theme coverage
- [x] Add light-theme overrides for previously dark-only login surfaces.
- [x] Add dark-theme palette for public landing sections and artwork.
- [x] Add dark-theme palette for customer QR upload, file-selection, payment and job-state surfaces.
- [ ] Audit all routes/components for remaining hardcoded colour literals and contrast issues.
- [ ] Verify empty, error, loading, modal, notification and table states in both themes.

### Responsive layout
- [x] Add shared viewport-width and long-content safety rules.
- [x] Keep desktop sidebar navigation and main workspace independently scrollable.
- [x] Keep desktop app header pinned and mobile header sticky.
- [x] Retain responsive shop/admin drawer behavior.
- [x] Adapt landing header/nav for narrow screens.
- [ ] Verify shop/admin pages and reusable components at 1440px, 1024px, 768px, 520px and 390px.
- [ ] Verify desktop layout at minimum supported Windows window size.

### Android and Windows apps
- [x] Add a dedicated Android theme preference store separate from customer authentication.
- [x] Default Android theme to Light and apply it before activity UI creation.
- [x] Add native Account > Appearance controls.
- [x] Synchronize Android theme with the embedded trusted-origin WebView.
- [x] Add Android persistence test covering default, selection, sign-out and restart.
- [x] Restore saved preference before Windows desktop React rendering.
- [ ] Verify scanner, account, document viewer, dialogs, file picker and embedded shop portal in both themes.
- [ ] Verify Tauri/Windows light and dark surfaces, nav, tables and dialogs at minimum window size.

### Verification and release
- [ ] Portal theme/responsive regression tests pass.
- [ ] Portal typecheck and production build pass.
- [ ] Desktop frontend typecheck/build and native Windows host build pass.
- [ ] Android unit tests, APK build and emulator UI tests pass.
- [ ] API/Agent/PostgreSQL regression jobs remain green.
- [ ] Final code review confirms no auth, payment, refund, file, print or navigation workflow regressions.
- [ ] Merge only after green CI and explicit approval.

## Technical notes

- Web theme preference key: `qrtoprint.theme` in origin-scoped localStorage.
- Auth tokens remain in the existing session/secure-storage flows; they are not used to store appearance.
- Native Android preference file: `smartprint.ui-preferences`; it does not share the customer AccountStore.
- Android WebView synchronization is restricted to the configured trusted portal origin and uses the existing `SmartPrintNative` message bridge.
- Windows Tauri reuses the shared portal app and preference helper.
