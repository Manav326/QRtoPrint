# SmartPrint Public Website & Distribution Launch Checklist

Last updated: 2026-11-01

This checklist tracks the public website, non-technical Windows setup, Android release and public software distribution. A checked source-code item means the implementation is present in the repository; it does not imply a successful CI run, deployment or physical-device acceptance.

## A. Public website and shop entry

- [x] Replace the unauthenticated root page with the SmartPrint public marketing landing page.
- [x] Add the custom storefront-plus-printer **For print shops** entry in the top-right header.
- [x] Route shop entry to the existing sign-in flow at `/shop`.
- [x] Allow direct shop registration at `/shop?mode=register`.
- [x] Add a return-to-website link to the existing authentication screen.
- [x] Explain the customer QR journey, shop review/approval, Agent and printer connection.
- [x] Add separate Windows and Android download cards; keep their CTAs disabled until signed release assets actually exist.
- [x] Add public metadata, social preview metadata and a SmartPrint favicon.
- [x] Preserve the existing `/s/{publicCode}` customer QR route ahead of public landing-page routing.
- [x] Run portal type-check/build on the current main commit and resolve any failures (CI run 344 passed).
- [ ] Deploy the updated portal and verify `https://qr-test.paintpulp.com/` on desktop and mobile.
- [ ] Complete keyboard, contrast, screen-reader and responsive browser checks.
- [ ] Publish and link the actual Terms of Service and Privacy Policy pages.
- [x] Do not expose broken `releases/latest/download` links before a release exists; display explicit signing/release-pending states instead.
- [ ] After the first signed release is published, enable the stable download URLs and confirm each asset serves anonymously over HTTPS.

## B. Windows Agent installer for shop owners

- [x] Keep the existing self-contained Windows x64 Agent and DPAPI-protected pairing identity.
- [x] Allow the existing Agent pairing command to receive a one-time enrollment code non-interactively through configuration.
- [x] Add an Inno Setup wizard that asks for the shop's one-time pairing code and uses the configured public HTTPS origin.
- [x] Package the Agent with the existing service-install and service-uninstall scripts.
- [x] Reuse the existing service installer to register the Windows Service and Private-profile firewall rule without showing a terminal.
- [x] Add a bounded local readiness check against `/api/v1/local/status` before setup reports success.
- [x] Allow repairs/upgrades to reuse a saved pairing only after an authenticated cloud heartbeat succeeds.
- [x] Exercise the non-interactive `--check-pairing` command on a clean Windows runner and assert its expected unpaired exit code in CI run 351.
- [x] Stop a previously running Agent service before replacing files and attempt to restore it if setup exits before service reinstallation succeeds.
- [x] Add CI steps intended to compile and retain an unsigned installer validation artifact.
- [x] Add a tag-based release step that requires a Windows code-signing PFX and verifies the installer signature before publication.
- [x] Verify the Inno Setup compilation job succeeds on GitHub Actions; the installer compiled and its validation artifact uploaded in CI run 344.
- [ ] Verify the resulting installer contains only customer-relevant files and installs cleanly on a fresh Windows 10/11 x64 machine.
- [ ] Test a normal shop-owner Windows account with UAC elevation, no preinstalled .NET runtime and no terminal usage.
- [ ] Test expired, malformed, already-used and wrong-shop pairing codes, including safe recovery after a failed pairing.
- [ ] Verify service startup after restart, local status, cloud heartbeat and printer discovery in the shop workspace.
- [ ] Test a real printer, Microsoft Print to PDF, Direct LAN upload and cloud-assisted fallback after installation.
- [ ] Configure and protect the trusted Windows code-signing certificate secrets.
- [ ] Decide whether a tray companion and silent auto-update are needed for the first public release.

## C. Android customer app

- [x] Keep the native Android customer companion and existing QR/customer workflow.
- [x] Add configurable release version, canonical service URL and release signing to Gradle.
- [x] Add a release workflow that builds a release APK and verifies its signature with `apksigner`.
- [x] Pass Android JVM unit tests, debug APK build and emulator UI tests in CI run 351 (API 35 emulator).
- [ ] Generate the production Android keystore and configure the four Android signing secrets in GitHub Actions.
- [ ] Back up the keystore and credentials securely outside GitHub; do not rotate this signing identity for ordinary updates.
- [ ] Install the release APK on physical Android 8+ devices and verify QR scanning, document import, account flows and print submission.
- [ ] Validate Local Network Access and shop-Wi-Fi upload with a real paired Agent.
- [ ] Publish the first signed APK as a public Release asset.
- [ ] Decide on Play Store listing and update distribution after direct APK acceptance.

## D. Versioned release channel

- [x] Add a tag-triggered release workflow for version tags matching `v*`.
- [x] Publish intended stable asset names: `SmartPrint-Windows-Setup.exe` and `SmartPrint-Android.apk`.
- [x] Generate a `SHA256SUMS.txt` file for both public downloads.
- [x] Document stable GitHub Release asset names and keep download controls unavailable until those assets are published.
- [x] Add a version/release-notes link from each download card.
- [x] Default release builds to `https://qr-test.paintpulp.com` through the `SMARTPRINT_PUBLIC_BASE_URL` repository variable, with an explicit default.
- [x] Validate the configured release origin as a canonical HTTPS origin before building either installer.
- [x] Limit GitHub Release write permission to the publication job and fail if any expected release asset is missing.
- [ ] Configure the Windows signing secrets and Android signing secrets.
- [ ] Set `SMARTPRINT_PUBLIC_BASE_URL` to the actual canonical deployment origin before tagging.
- [ ] Run the first tagged release and verify both release assets and checksums.
- [ ] Test download links from a signed-out browser and a mobile device.
- [ ] Establish a versioning and rollback policy; retain older releases for support.

## E. Deployment and production acceptance

- [x] Confirm the main-branch CI run passes portal, API, Agent, Windows installer and Android debug validation (CI run 351, commit `f42a112`).
- [x] Build/publish the API and portal container images to GHCR and pass the migration/API-readiness/portal-response smoke test (CI run 351).
- [ ] Deploy the current main-branch portal/API images to the intended staging environment.
- [ ] Verify existing shop login, new shop registration and platform-admin routing after deployment.
- [ ] Verify existing printed QR station URLs and active shop sessions still work.
- [ ] Run end-to-end customer upload, quote, shop review, manual payment verification, dispatch and output confirmation.
- [ ] Verify no test or development build is presented as a production-signed download.
- [ ] Complete a production go/no-go review with Windows, Android, printer, security and download-link evidence.

## Current verified CI evidence

- The green CI baseline is run 352 at commit `a2ebb4772052c4a9001424f50a6b7074f02dbac0`. Landing-page download-gating changes were committed afterward and require their own CI result before being marked verified.

- Latest verified code commit: `f42a112b98912b13a4a192d28ad6cda03cb1c8fb`.
- CI run 351: [GitHub Actions run](https://github.com/Manav326/QRtoPrint/actions/runs/41261715735) — portal, .NET API/Agent, API and Agent tests, the Windows non-interactive pairing-status smoke test, Inno Setup compilation, Android JVM/emulator tests, GHCR image publication and container smoke tests all completed successfully.
- Earlier CI run 344 also passed the first full installer and container validation.
- GitHub Releases currently has no published release. The Windows validation artifact is unsigned and the Android artifact is a debug build; neither is a public customer download. The public landing page now shows non-clickable release-pending states instead of broken latest-download URLs.

## Guardrails

- Keep the existing customer QR route and shop/API contracts intact.
- Do not ask shop owners to open PowerShell, Command Prompt or a terminal for normal installation.
- Do not publish a debug APK as the public customer app.
- Do not publish an unsigned Windows installer as a trusted SmartPrint release.
- Keep the Android release keystore stable and backed up.
- Treat an Agent local readiness response as local-service verification only; the shop workspace must independently confirm the cloud heartbeat and printer inventory.
