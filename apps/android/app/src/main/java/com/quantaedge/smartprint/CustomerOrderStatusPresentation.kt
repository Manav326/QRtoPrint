package com.quantaedge.smartprint

import java.util.Locale

internal object CustomerOrderStatusPresentation {
    private val terminalStatuses = setOf(
        "COMPLETED", "PDF_EXPORTED", "REJECTED", "REFUNDED", "CANCELLED", "FAILED"
    )

    fun activityStatusLabel(status: String): String = when (status.trim().uppercase(Locale.ROOT)) {
        "AWAITING_SHOP_APPROVAL" -> "Shop review"
        "AWAITING_PAYMENT", "AWAITING_CUSTOMER_PAYMENT" -> "Payment due"
        "AWAITING_COUNTER_PAYMENT" -> "Pay at counter"
        "PAYMENT_REPORTED", "CUSTOMER_REPORTED" -> "Verify pending"
        "PAYMENT_REJECTED" -> "Payment declined"
        "PAID_AUTHORISED", "VERIFIED" -> "Payment verified"
        "LOCAL_QUEUE_CLAIMED" -> "Transferring"
        "LOCAL_QUEUE_READY" -> "Queued"
        "PRINT_AUTHORISED" -> "Print approved"
        "PRINT_SUBMITTING" -> "Sending"
        "SPOOLER_ACCEPTED" -> "Queue accepted"
        "PRINTING" -> "Printing"
        "OUTPUT_UNCONFIRMED" -> "Output check"
        "PDF_EXPORTING" -> "Saving PDF"
        "PDF_EXPORTED" -> "PDF ready"
        "COMPLETED" -> "Completed"
        "REJECTED" -> "Rejected"
        "REFUNDED" -> "Refunded"
        "PARTIALLY_REFUNDED" -> "Partly refunded"
        "CANCELLED" -> "Cancelled"
        "FAILED" -> "Failed"
        "QUOTE_EXPIRED" -> "Quote expired"
        else -> "In progress"
    }

    fun isTerminalStatus(status: String): Boolean =
        terminalStatuses.contains(status.trim().uppercase(Locale.ROOT))

    /** Prefer the live stage while any file is still active; otherwise show a terminal result. */
    fun batchSummaryStatus(statuses: List<String>): String {
        val normalized = statuses.map { it.trim().uppercase(Locale.ROOT) }
        normalized.firstOrNull { !isTerminalStatus(it) }?.let { return it }
        return when {
            "COMPLETED" in normalized -> "COMPLETED"
            "PDF_EXPORTED" in normalized -> "PDF_EXPORTED"
            "REFUNDED" in normalized -> "REFUNDED"
            "CANCELLED" in normalized -> "CANCELLED"
            "FAILED" in normalized -> "FAILED"
            "REJECTED" in normalized -> "REJECTED"
            else -> normalized.firstOrNull().orEmpty()
        }
    }

    fun filePaymentSummary(
        status: String?, allocatedAmountMinor: Long, refundedAmountMinor: Long, currency: String
    ): String? {
        if (allocatedAmountMinor <= 0L && refundedAmountMinor <= 0L && status.isNullOrBlank()) return null
        val allocated = maxOf(0L, allocatedAmountMinor)
        val refunded = maxOf(0L, refundedAmountMinor)
        val net = maxOf(0L, allocated - refunded)
        val statusLabel = status?.takeIf { it.isNotBlank() }
            ?.let { " · " + activityStatusLabel(it) }.orEmpty()
        return "File allocation: ${formatMoney(allocated, currency)} · " +
            "Returned: ${formatMoney(refunded, currency)} · Net: ${formatMoney(net, currency)}$statusLabel"
    }

    fun batchPaymentSummary(status: String, amountMinor: Long, currency: String, fileCount: Int): String =
        "Batch total: ${formatMoney(maxOf(0L, amountMinor), currency)} · " +
            "${activityStatusLabel(status)} · $fileCount ${if (fileCount == 1) "file" else "files"}"

    private fun formatMoney(amountMinor: Long, currency: String): String =
        "%.2f %s".format(Locale.ROOT, amountMinor / 100.0, currency)
}
