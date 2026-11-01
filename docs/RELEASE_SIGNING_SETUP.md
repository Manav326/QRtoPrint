# SmartPrint Release and Distribution Setup

SmartPrint currently uses a direct-download Windows beta installer. The Windows installer is intentionally unsigned while the project validates the shop-owner installation experience. The website labels the download as available and keeps the Windows security prompt note small but visible.

## 1. Windows beta distribution

No Windows code-signing secrets are required for the interim beta release.

The release workflow:
- Builds the existing self-contained x64 Windows Agent and guided Inno Setup installer.
- Verifies that the generated installer is intentionally unsigned.
- Publishes the installer as `SmartPrint-Windows-Setup.exe`.
- Publishes a `SHA256SUMS.txt` file covering every binary included in that version.
- Keeps each published stable version permanently; a new version moves the GitHub `latest` pointer without replacing older versions.

Customers should download the installer only from the official SmartPrint website or its linked GitHub Release. Windows may show an Unknown Publisher or SmartScreen prompt. Some Windows 11 devices with Smart App Control or managed security policy may block unsigned installers entirely. Do not ask customers to turn off Defender, SmartScreen, Smart App Control, or other security protections.

The website shows **Available** only when the latest stable release contains the Windows installer, and includes a brief first-run security note. Do not present the installer as signed or imply that Windows will always permit it to run.

## 2. Android release keystore

The Android package ID is `com.quantaedge.smartprint`. Its production signing key must remain stable for all future updates to install over the existing app.

Generate a dedicated release keystore on a secure machine with JDK 17 or later:

```powershell
keytool -genkeypair -v -keystore smartprint-release.jks -keyalg RSA -keysize 4096 -validity 10000 -alias smartprint-release
```

Use a unique, strong keystore password and key password. Store the keystore and credentials in a secure offline backup. Do not commit the keystore to GitHub or send it in chat.

Convert the keystore to one-line Base64 for the GitHub secret. In PowerShell:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes(".\\smartprint-release.jks")) | Set-Clipboard
```

In GitHub, open **Settings → Secrets and variables → Actions → New repository secret** and add:

- `ANDROID_SIGNING_KEYSTORE_BASE64`: the copied Base64 text
- `ANDROID_SIGNING_KEYSTORE_PASSWORD`: keystore password
- `ANDROID_SIGNING_KEY_ALIAS`: `smartprint-release` (or the alias you chose)
- `ANDROID_SIGNING_KEY_PASSWORD`: key password

Keep a separate secure backup of the original `.jks` file. If it is lost, future Android updates cannot be signed with the same identity.

The release workflow checks all four Android secrets together. If none are configured, the Android release is skipped while the Windows beta release can still publish. If only some are configured, the workflow fails rather than producing a broken Android release. Once all four secrets are available, the same versioned release workflow builds and verifies the signed APK and includes it alongside the Windows installer.

## 3. Public service URL

Add or update the repository Actions variable:

- Name: `SMARTPRINT_PUBLIC_BASE_URL`
- Value: the exact canonical HTTPS origin configured for the active deployment. Do not enter a path, query string, or fragment.

The Windows setup and Android APK are both built against this origin. The deployment's `PUBLIC_BASE_URL` must agree with it.

## 4. First Windows beta release

After the current main-branch CI run passes and the intended staging origin is confirmed, create and push a stable version tag from the intended main commit:

```powershell
git checkout main
git pull
git tag v0.1.0
git push origin v0.1.0
```

The Release SmartPrint workflow can publish the Windows installer without Windows signing secrets. Android is included only when all four Android signing secrets are configured. The release contains:

- `SmartPrint-Windows-Setup.exe`
- `SHA256SUMS.txt`
- `SmartPrint-Android.apk` (only when the production Android signing credentials are present)

Do not distribute the unsigned installer as a signed or verified-publisher build. The official page links to the release and gives customers a concise notice about possible Windows security prompts.

## 5. Future Microsoft Store distribution

The future Microsoft Store channel will use a separate MSIX package. Microsoft re-signs accepted MSIX/AppX packages during Store publication; it does not re-sign the current EXE installer. The existing Agent currently installs a Windows Service and a Private-profile firewall rule. Before Store submission, validate the applicable MSIX service/capability rules and design an approved package/install approach that preserves printer discovery, secure job dispatch, restart recovery and local-network operation. Do not assume the current EXE can simply be uploaded as an MSIX.

## 6. Version retention and latest downloads

Every public stable release is a separate GitHub Release with a semantic-version tag such as `v0.1.0`. **Retain every published stable version indefinitely.** Do not delete older releases and do not replace the binaries attached to a published tag. A new build must receive a new version tag.

The website reads the latest stable release from the repository and uses the asset download URLs returned by GitHub. It does not contain a repository slug, release tag, or direct asset URL in application source. The `VITE_SMARTPRINT_RELEASES_API_URL` and `VITE_SMARTPRINT_RELEASES_URL` build arguments are populated from GitHub Actions repository context. Publishing a newer version moves that pointer; it does not remove or replace the older versioned release. Older releases remain available from their individual release pages and tag-specific asset URLs.

The release workflow:
1. Accepts stable `vMAJOR.MINOR.PATCH` tags only.
2. Requires all four Android signing secrets; a missing secret fails the release before package publication.
3. Creates or refreshes an unpublished draft for that tag; a retry may refresh a draft only.
4. Verifies the Android signature, local package checksums and uploaded release asset names and sizes.
5. Publishes both the Windows installer and signed Android APK together, then marks the release latest only after verification.
6. Refuses to publish an already-published release tag or a release draft without the signed Android APK.

The five-day retention on GitHub Actions artifacts applies only to temporary build-transfer artifacts. It does not apply to files attached to published GitHub Releases.

**Repository-owner hardening:** enable GitHub's immutable-release protection for this repository where available, and protect the `v*` tag pattern from updates or deletion. These are repository settings and are not changed by the workflow. Until enabled, the workflow enforces no-replacement for automated publishing, but a repository owner could still manually edit or delete a release.
