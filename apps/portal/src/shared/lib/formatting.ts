export function formatMoney(minor: number, currency: string) {
  return new Intl.NumberFormat("en-IN", {
    style: "currency",
    currency,
    minimumFractionDigits: 2
  }).format(minor / 100);
}

export function prettyStatus(status: string) {
  const names:Record<string,string>={
    AWAITING_CONFIGURATION:"Configure options",
    AWAITING_CUSTOMER_APPROVAL:"Quote awaiting customer",
    QUOTE_EXPIRED:"Quote expired",
    AWAITING_SHOP_APPROVAL:"Awaiting shop approval",
    REJECTED:"Rejected",
    REFUNDED:"Fully refunded",
    PARTIALLY_REFUNDED:"Partially refunded",

    UPLOADED:"Uploaded",
    VALIDATING:"Validating",
    ANALYSING:"Analysing",
    AWAITING_PAYMENT:"Awaiting payment",
    AWAITING_COUNTER_PAYMENT:"Pay at counter",
    AUTHORISED:"Authorised",
    PAID_AUTHORISED:"Paid · authorised",
    LOCAL_QUEUE_CLAIMED:"Transferring locally",
    LOCAL_QUEUE_READY:"Local copy secured",
    PRINT_AUTHORISED:"Ready to print",
    PRINT_SUBMITTING:"Sending to printer",
    SPOOLER_ACCEPTED:"Accepted by spooler",
    PRINTING:"Printing",
    OUTPUT_UNCONFIRMED:"Output unconfirmed",
    PDF_EXPORTING:"Creating PDF",
    PDF_EXPORTED:"PDF saved",
    QUEUED:"Queued",
    SUBMITTING_TO_SPOOLER:"Sending to printer",
    COMPLETED:"Completed",
    FAILED:"Failed",
    CANCELLED:"Cancelled"
  };
  return names[status] || status.replaceAll("_"," ").toLowerCase().replace(/\\b\\w/g,letter=>letter.toUpperCase());
}
