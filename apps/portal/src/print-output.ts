export interface PdfExportReceiptFields {
  outputType?: string | null;
  outputCompletedUtc?: string | null;
  outputFilePath?: string | null;
}

/**
 * Returns the local destination only after the Agent has reported a completed
 * PDF export receipt. Physical-print jobs and incomplete/uncertain exports
 * deliberately do not expose a destination as a saved PDF.
 */
export function verifiedPdfExportPath(receipt: PdfExportReceiptFields): string | null {
  if (receipt.outputType !== "PDF_EXPORT" || !receipt.outputCompletedUtc) return null;
  if (typeof receipt.outputFilePath !== "string" || receipt.outputFilePath.trim().length === 0) return null;
  return receipt.outputFilePath;
}
