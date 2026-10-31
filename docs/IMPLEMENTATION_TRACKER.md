# SmartPrint by QuantaEdge — implementation tracker

Working branch: `feature/shop-review-refunds-and-portal-polish`. Keep implementation on this branch; do not write to `main`.

## Implemented in source
- [x] Multi-file PDF/JPG/JPEG/PNG customer uploads share one batch identifier.
- [x] Every file starts accepted; the shopkeeper can reject files individually and undo selections before applying review.
- [x] Verified paid files support page-level partial refunds calculated from the original paid amount.
- [x] Multiple partial refunds accumulate; the total cannot exceed the original payment.
- [x] Refund idempotency keys prevent duplicate records and cannot be reused across files.
- [x] Previously refunded pages cannot be restored.
- [x] The API previews the additional refund before the shopkeeper confirms that money has already been returned.
- [x] The Windows Agent persists rejected pages and omits them from physical print and PDF export.
- [x] Partially refunded jobs remain eligible for printing their accepted pages.
- [x] Shop collection reports subtract recorded refunds from net collections.
- [x] SmartPrint by QuantaEdge branding is applied to portal metadata, Android launcher and primary documentation.

## Acceptance still required
- [ ] Run portal typecheck/build and .NET Release build/tests.
- [ ] Apply the EF migration against PostgreSQL containing historical refund rows.
- [ ] Exercise partial/full refunds, cumulative amounts and concurrent idempotency against PostgreSQL.
- [ ] Validate filtered physical print and PDF output on Windows.
- [ ] Validate page-review UX on desktop and physical Android.
- [ ] Rehearse UPI/cash returns and verify the customer receives the money before recording it.

## Rules
- Refunds are not automatic: the shopkeeper returns money through their own UPI/bank or cash process and records the returned amount.
- Page numbers are 1-based. The Windows Agent filters those numbers against the original document.
- Page selections can be changed before recording. Refunded pages are then permanently excluded.
- Refunds are blocked after local handoff, Agent claim, spooler activity or output confirmation.
- Rejecting all pages returns the remaining payment and marks the job `REFUNDED`.
