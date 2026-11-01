# SmartPrint Release Signing Setup

The public release workflow intentionally refuses to publish unsigned customer installers. Configure these credentials once in the GitHub repository before creating the first release tag.

## 1. Android release keystore

The Android package ID is `com.thinkwithsujeet.qrtoprint`. Its release signing key must remain stable for all future updates to install over the existing app.

Generate a dedicated release keystore on a secure machine with JDK 17 or later:

```powershell
keytool -genkeypair -v -keystore smartprint-release.jks -keyalg RSA -keysize 4096 -validity 10000 -alias smartprint-release
```

Use a unique, strong keystore password and key password. Store the keystore and credentials in a secure offline backup. Do not commit the keystore to GitHub or send it in chat.

Convert the keystore to one-line Base64 for the GitHub secret. In PowerShell:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes(".\smartprint-release.jks")) | Set-Clipboard
```

In GitHub, open **Settings → Secrets and variables → Actions → New repository secret** and add:

- `ANDROID_SIGNING_KEYSTORE_BASE64`: the copied Base64 text
- `ANDROID_SIGNING_KEYSTORE_PASSWORD`: keystore password
- `ANDROID_SIGNING_KEY_ALIAS`: `smartprint-release` (or the alias you chose)
- `ANDROID_SIGNING_KEY_PASSWORD`: key password

Keep a separate secure backup of the original `.jks` file. If it is lost, future Android updates cannot be signed with the same identity.

## 2. Windows installer code-signing certificate

Obtain a trusted Windows code-signing certificate for the legal publisher identity that should be shown to shop owners. Export the certificate with its private key as a password-protected PFX file.

The Inno Setup script currently declares `AppPublisher=QuantaEdge`. Confirm that this matches the publisher identity represented by the certificate, or update the installer metadata to the certificate's actual legal publisher before releasing.

Convert the PFX to Base64 on a secure Windows machine:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes(".\smartprint-code-signing.pfx")) | Set-Clipboard
```

Add these GitHub Actions repository secrets:

- `WINDOWS_SIGNING_CERT_BASE64`: copied Base64 PFX
- `WINDOWS_SIGNING_CERT_PASSWORD`: PFX password

The release workflow signs the final Setup.exe with SHA-256 and a trusted timestamp, then verifies its Authenticode signature before publication. Do not use a self-signed certificate for a public customer release.

## 3. Public service URL

Add or update the repository Actions variable:

- Name: `SMARTPRINT_PUBLIC_BASE_URL`
- Value: the canonical public HTTPS origin, currently `https://qr-test.paintpulp.com`

The Windows setup and Android APK are both built against this origin. The deployment's `PUBLIC_BASE_URL` must agree with it.

## 4. First signed release

After all secrets and the URL variable are configured, wait for the main-branch CI build and installer validation to pass. Then create and push a version tag from the intended main commit, for example:

```powershell
git checkout main
git pull
git tag v0.1.0
git push origin v0.1.0
```

The Release SmartPrint workflow builds and signs both apps, verifies the Android APK signature, calculates SHA-256 checksums and publishes these assets:

- `SmartPrint-Windows-Setup.exe`
- `SmartPrint-Android.apk`
- `SHA256SUMS.txt`

The landing page's permanent download URLs then resolve to the latest published release.

Do not create the first tag until the release signing secrets are configured and the current main build has passed. The workflow is designed to fail closed if either signing identity is missing.


## 5. Version retention and latest downloads

Every public stable release is a separate GitHub Release with a semantic-version tag such as `v0.1.0`. **Retain every published stable version indefinitely.** Do not delete older releases and do not replace the binaries attached to a published tag. A new build must receive a new version tag.

The website's `releases/latest/download/...` URLs are a moving pointer to the latest stable release. Publishing a newer version moves that pointer; it does not remove or replace the older versioned release. Older releases remain available from their individual release pages and tag-specific asset URLs.

The release workflow now:
1. Accepts stable `vMAJOR.MINOR.PATCH` tags only.
2. Creates or refreshes an unpublished draft for that tag; a retry may refresh a draft only.
3. Verifies both local package checksums and the uploaded release asset names and sizes.
4. Publishes the release and marks it latest only after verification.
5. Refuses to run against an already-published release tag and never overwrites a published asset.

The five-day retention on GitHub Actions artifacts applies only to temporary build-transfer artifacts. It does not apply to files attached to published GitHub Releases.

**Repository-owner hardening:** enable GitHub's immutable-release protection for this repository where available, and protect the `v*` tag pattern from updates or deletion. These are repository settings and are not changed by the workflow. Until enabled, the workflow enforces no-replacement for automated publishing, but a repository owner could still manually edit or delete a release.
