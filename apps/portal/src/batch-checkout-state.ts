export function shouldBlockBatchCheckoutForExpiredQuote(
  batchPaymentStatus: string | null | undefined,
  acceptedQuoteHasExpired: boolean
): boolean {
  const paymentAlreadyStarted =
    batchPaymentStatus !== null &&
    batchPaymentStatus !== undefined &&
    batchPaymentStatus !== "REJECTED";

  return acceptedQuoteHasExpired && !paymentAlreadyStarted;
}
