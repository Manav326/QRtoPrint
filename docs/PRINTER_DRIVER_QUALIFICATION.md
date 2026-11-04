# Printer driver qualification

## Purpose

Qualify selected Windows printer driver packages on disposable GitHub-hosted Windows runners without claiming that a physical printer has been tested.

The existing SmartPrint Agent and Windows spooler remain the sole print execution path. This workflow does not introduce a vendor-specific print implementation.

## Initial India-market registry

The version-controlled registry is `scripts/printer-qualification/printer-driver-manifest.json`.

Initial entries:

- HP Universal Print Driver PCL 6 (exact package/version pending).
- Canon Generic Plus UFR II 3.50 (blocked pending written commercial-use permission).
- Epson Universal Print Driver 2.123.00 for the L3150 Series Windows 11 x64 support listing (87.53 MB; package extraction and license review pending).
- Brother Universal Printer Driver 1.0.0.0 for the India DCP-J100 support page (20.37 MB; two-sided printing explicitly unsupported by this driver).
- Pantum P2503 Windows Driver 1.0.5.
- Pantum Universal Printing Driver 1.0.6.

Official support pages are references, not permission to download or redistribute. No driver package is committed to this repository, cached in a public artifact, or published with SmartPrint releases.

## Approval gate

A driver is eligible for a runner only when all of these are present and reviewed:

1. Official vendor HTTPS package URL.
2. Exact package version and SHA-256 pinned in the manifest.
3. License/use approval for automated CI execution.
4. Extracted INF and catalog paths inside a ZIP/CAB package.
5. Valid catalog signature with the expected publisher.
6. Exact Windows driver name and supported model family.
7. An explicit `ciApproval: approved` and `licenseReview: approved`.

The harness accepts only ZIP and CAB packages. It never executes vendor setup EXEs, scripts or custom installer binaries. If a package is available only as an EXE, it remains pending until a vendor-approved extraction route is established.

Canon's India Generic Plus UFR II V3.50 support page currently carries restrictive personal/non-commercial-use language and prohibits transfer, redistribution and modification. This entry is blocked from commercial CI pending written permission or an alternate license. Brother's India support listing states that its Universal Printer Driver 1.0.0.0 does not support two-sided printing; this limitation must be carried into any model-specific compatibility result.

## How the hosted test works

1. Validate the complete manifest without downloading packages.
2. On an explicitly selected, approved driver, download only from an allowlisted vendor host. Redirects are checked at every hop.
3. Verify the pinned SHA-256 before extraction.
4. Extract to a disposable temporary folder and enforce path containment for the INF.
5. Verify a valid signed catalog from the approved publisher.
6. Stage the INF with PnPUtil and register the print driver.
7. Create a uniquely named queue with a local file-backed port.
8. Run the existing `WindowsPrintExecutor.Print` path against a generated, non-customer PNG.
9. Capture the rendered printer-language output, SHA-256 and Windows `DeviceCapabilities` report.
10. Upload only the JSON evidence. Remove the queue, port and generated print output; the hosted VM is discarded after the run.

The file-backed port tests the driver and Windows rendering/spooler path, not USB, Wi-Fi, paper feed, physical colour, duplex mechanics, tray behavior or print quality.

## Run

The dedicated GitHub Actions workflow supports:

- `manifest-only`: safe default; validates metadata without downloading any vendor package.
- One explicitly selected driver ID: runs only after both approval gates have been completed in the manifest.

The workflow runs on GitHub-hosted Windows VMs only. It does not use self-hosted runners or repository secrets.

## Evidence and status language

The output report uses `DRIVER_RENDERED` only when an approved package was installed, a queue was created, Windows accepted the spooler job and the driver produced a non-empty output file.

It always sets `physicalPrinterVerified: false`.

Only a separate, documented physical test on a named printer model can change a model's certification to hardware verified. Do not infer physical support from a driver's advertised capabilities alone.

## Follow-on work

- Resolve exact package versions, download URLs, hashes, extraction and licenses for each vendor.
- Add model-specific test fixtures and expected capability baselines.
- Extend the runner matrix to additional Windows versions only where the driver vendor supports them.
- Build a protected, isolated physical-printer lab for USB and network printing, trays, duplex, paper handling and output-quality certification.
- Surface compatibility evidence in the shop printer setup experience without automatically changing a shop's selected printer or settings.
