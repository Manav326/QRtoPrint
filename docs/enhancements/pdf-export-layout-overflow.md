# Enhancement checklist: PDF export paths and table text wrapping

**Status:** In progress  
**Implementation branch:** `enhancement/pdf-export-layout-overflow`  
**Base:** `main` at `40b0eb96b781812fcf40307963392db6211911f5`

## Problem

Long values in dense tables can run into adjacent columns instead of wrapping inside their own cells. The most visible case is a completed PDF export, where the full Windows destination path (for example, a path under `C:\Users\Public\Documents\QRtoPrint Exports\`) can overwhelm the print-job row. Long filenames, folder names, MIME types, identifiers, printer names and timestamps can produce the same class of layout defect.

## Acceptance checklist

### Table layout
- [ ] Remove the blanket no-wrap behavior from generic portal tables; wrapping must be the safe default.
- [ ] Keep deliberate no-wrap behavior only on compact values that benefit from it (for example status chips and action labels).
- [ ] Constrain dense queue columns and make the table scroll within its own container on narrow viewports instead of expanding the whole page.
- [ ] Allow long identifiers, filenames, printer metadata and Windows paths to wrap/break within their own cells.
- [ ] Preserve intentional minimum widths and horizontal scrolling for platform tenant/subscription tables.

### PDF export details
- [ ] Show a successfully saved PDF's destination in a separate, clearly labelled detail row for that file.
- [ ] Keep the path readable and selectable; break long path segments without changing the stored path.
- [ ] Do not render a destination path for physical-print jobs or exports without a confirmed saved-file receipt.
- [ ] Keep batch summaries compact when a batch contains multiple files; show each file's own path in its expanded file details.

### Other affected surfaces
- [ ] Audit shop print queue, customer list/history, platform tenant/subscription tables, status history and report panels for similar spill/overflow behavior.
- [ ] Ensure filenames and paths in cards/modals/history wrap without forcing the page wider than the viewport.
- [ ] Verify action groups wrap cleanly and remain usable at phone, tablet and desktop widths.
- [ ] Preserve existing print, payment, refund, batch-review and PDF export workflows.

### Verification
- [ ] Add regression coverage for long Windows paths, long filenames and long print-setting text.
- [ ] Verify queue/table layouts at 1440px, 1024px, 768px and 390px widths.
- [ ] Run portal tests, typecheck and production build.
- [ ] Run the relevant GitHub Actions checks and confirm no new failures.
- [ ] Review the final diff for unrelated changes.

## Implementation notes

The current portal stylesheet applies `white-space: nowrap` to every table. This is too broad for a table containing paths and other variable-length data. The fix should establish wrapping as the baseline while retaining explicit compact/scroll behavior where a particular table needs it. The shop print-job API already carries the PDF output receipt fields; the UI should present the saved path only when the local agent has reported a completed export.
