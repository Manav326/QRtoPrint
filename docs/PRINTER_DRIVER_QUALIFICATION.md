# Printer driver qualification

## Purpose

Qualify selected Windows printer driver packages on disposable GitHub-hosted Windows runners without claiming that a physical printer has been tested.

The existing SmartPrint Agent and Windows spooler remain the sole print execution path. This workflow does not introduce a vendor-specific print implementation.

## Runner and OS certification boundary

The qualification runner currently reports Windows Server 2025 x64. GitHub-hosted Windows runners do not provide a Windows 11 desktop image in this workflow. A successful install/render therefore certifies only the recorded Windows Server runner environment. It does not certify Windows 11 compatibility. The manifest records this distinction for every vendor entry. A model/OS claim requires vendor support information plus a matching Windows 11 hardware or supported VM/lab test.

## Initial India-market registry

The version-controlled registry is `scripts/printer-qualification/printer-driver-manifest.json`.

**Current gate result:** no vendor package is eligible for installation yet. Canon lists Server 2025 support but is blocked by its India license disclaimer. HP, Epson, Brother and Pantum P2503 currently lack reviewed Server 2025 support evidence; the Pantum universal package still needs an exact supported-OS declaration. The script stops before downloading a package when the vendor's listed OS does not match the runner.

Initial entries:

- HP Universal Print Driver PCL 6 (Windows Server 2025 support and exact package/version pending).
- Canon Generic Plus UFR II 3.50 (blocked pending written commercial-use permission).
- Epson Universal Print Driver 2.123.00 for the L3150 Series Windows 11 x64 support listing (87.53 MB; Server 2025 support, package extraction and license review pending).
- Brother Universal Printer Driver 1.0.0.0 for the India DCP-J100 support page (20.37 MB; Server 2025 support pending; two-sided printing explicitly unsupported).
- Pantum P2503 Windows Driver 1.0.5 (currently listed for Windows 11 x64 only; Server 2025 support pending).
- Pantum Universal Printing Driver 1.0.6 (exact Windows OS target pending; not eligible for hosted installation yet).
- Ricoh PCL6 Driver for Universal Print 4.46.0.0 (official EXE archive inspected without execution; Ricoh package signature valid, INF catalog WHCP signature pinned; installation remains blocked because the installer accepts only ZIP/CAB INF packages and license/install review is pending).
- Kyocera KX Universal Printer Driver 8.7.0422.0 (official ZIP audited; x64 INF and Kyocera-signed catalog identified, package SHA-256 and catalog thumbprint pinned; exact supported OS/model/license/install review remains pending).

Official support pages are references, not permission to download or redistribute. No driver package is committed to this repository, cached in a public artifact, or published with SmartPrint releases.

## Approval gate

A driver is eligible for a runner only when all of these are present and reviewed:

1. Official vendor HTTPS package URL.
2. Exact package version and SHA-256 pinned in the manifest; the non-executing package audit verifies both the downloaded hash and the version declared by a printer INF.
3. License/use approval for automated CI execution.
4. Extracted INF and catalog paths inside a ZIP/CAB package.
5. At least one valid signature on an INF-declared catalog. The catalog publisher may be the printer manufacturer or Microsoft Windows Hardware Compatibility Publisher (WHCP); capture its exact certificate thumbprint. If the outer package is Authenticode-signed, record and verify its publisher separately.
6. Exact Windows driver name and supported model family.
7. The exact INF-declared catalog publisher certificate thumbprint pinned in the manifest.
8. An explicit `ciApproval: approved` and `licenseReview: approved`.

The installation harness accepts only ZIP and CAB packages. It never executes vendor setup EXEs, scripts or custom installer binaries. If a package is available only as an EXE, it remains pending until a vendor-approved INF-package extraction route is established. Before installation, the selected INF must declare the catalog being checked; that catalog must have a valid signature whose exact certificate thumbprint matches the pinned manifest value. A vendor-name substring alone is not sufficient for installation approval. The non-executing package audit distinguishes an outer package signer (for example, Ricoh) from the INF catalog signer (which can be Microsoft WHCP for a WHQL-signed driver). Ricoh's currently inspected 4.46.0.0 package records the WHCP catalog signer thumbprint separately from the Ricoh outer-package signer; the installer remains blocked until an INF package, exact INF/catalog mapping, package hash and licensing/OS approvals are available.

Canon's India Generic Plus UFR II V3.50 support page currently carries restrictive personal/non-commercial-use language and prohibits transfer, redistribution and modification. This entry is blocked from commercial CI pending written permission or an alternate license. Brother's India support listing states that its Universal Printer Driver 1.0.0.0 does not support two-sided printing; this limitation must be carried into any model-specific compatibility result.

## How the hosted test works

1. Validate the complete manifest without downloading packages.
2. On an explicitly selected, approved driver, download only from an allowlisted vendor host. Redirects are checked at every hop.
3. Verify the pinned SHA-256 before extraction and confirm the printer INF DriverVer matches the manifest version.
4. Extract to a disposable temporary folder and enforce path containment for the INF.
5. Read the catalog references from the selected INF and verify a valid signature against the exact certificate thumbprint pinned in the manifest.
6. Stage the INF with PnPUtil and register the print driver.
7. Create a uniquely named queue with a local file-backed port.
8. Run the existing `WindowsPrintExecutor.Print` path in monochrome on A4 against a generated, non-customer PNG. This provides a conservative baseline; feature-specific colour/duplex checks are separate.
9. Capture the rendered printer-language output, SHA-256 and Windows `DeviceCapabilities` report.
10. Upload only the JSON evidence. Remove the queue, port and generated print output; the hosted VM is discarded after the run.

The file-backed port tests the driver and Windows rendering/spooler path, not USB, Wi-Fi, paper feed, physical colour, duplex mechanics, tray behavior or print quality.

## Run

The dedicated GitHub Actions workflow supports:

- `manifest-only`: safe default; validates metadata without downloading any vendor package.
- One explicitly selected driver ID: runs only after both approval gates have been completed in the manifest.

The workflow is pinned to GitHub-hosted `windows-2025` x64 (Windows Server 2025). The qualification script reads the actual OS caption and architecture and refuses to download a driver unless the exact runner target appears in that package's vendor-listed `windowsTargets`. A successful Server 2025 result is not evidence of Windows 11 x64 support. The standard hosted Windows 11 runner is ARM64, so Windows 11 x64-only packages remain unqualified on this runner until a suitable isolated test environment is available. The vendor-install job targets the GitHub Environment `printer-driver-qualification`. Before approving any manifest entry, create that environment in repository Settings → Environments and require designated reviewers; the workflow declaration alone does not configure reviewer protection. The workflow does not use self-hosted runners or repository secrets.

## Evidence and status language

The output report uses `DRIVER_RENDERED` only when an approved package was installed, a queue was created, Windows accepted the spooler job and the driver produced a non-empty output file. The PR workflow also runs a built-in Microsoft Print to PDF fixture through the same file-backed local port and production Agent path; this validates the harness mechanics but is not a vendor-driver pass. The render test waits for the spooler job to finish and requires three identical output hashes across consecutive observations, plus an A4 capability report, before recording a pass.

It always sets `physicalPrinterVerified: false`.

Only a separate, documented physical test on a named printer model can change a model's certification to hardware verified. Do not infer physical support from a driver's advertised capabilities alone.

## Follow-on work

- Resolve exact package versions, download URLs, hashes, extraction and licenses for each vendor.
- Add model-specific test fixtures and expected capability baselines.
- Extend the runner matrix to additional Windows versions only where the driver vendor supports them.
- Build a protected, isolated physical-printer lab for USB and network printing, trays, duplex, paper handling and output-quality certification.
- Surface compatibility evidence in the shop printer setup experience without automatically changing a shop's selected printer or settings.
