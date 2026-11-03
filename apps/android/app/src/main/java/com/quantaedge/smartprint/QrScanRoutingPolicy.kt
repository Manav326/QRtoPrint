package com.quantaedge.smartprint

/**
 * The scanner UI is shared, but its entry point tells the customer what they are
 * trying to do. Keeping the purpose explicit prevents a Wi-Fi check from ever
 * being interpreted as consent to change the connected shop.
 */
internal enum class QrScanPurpose(
    val wireValue: String,
    val eyebrow: String,
    val title: String,
    val instruction: String,
    val alignmentHint: String
) {
    SHOP_CONNECTION(
        wireValue = "shop_connection",
        eyebrow = "SHOP CONNECTION",
        title = "Scan shop QR",
        instruction = "Scan the SmartPrint QR displayed at the shop counter to connect.",
        alignmentHint = "Align the shop QR inside the frame."
    ),
    WIFI_READINESS(
        wireValue = "wifi_readiness",
        eyebrow = "SHOP WI-FI CHECK",
        title = "Scan Wi-Fi check QR",
        instruction = "Scan the temporary phone-check QR shown on the shop computer.",
        alignmentHint = "Align the phone-check QR inside the frame."
    );

    companion object {
        fun fromWireValue(value: String?): QrScanPurpose =
            values().firstOrNull { it.wireValue == value } ?: SHOP_CONNECTION
    }
}

internal sealed class QrScanRoute {
    data class ConnectToShop(val link: StationQrLink) : QrScanRoute()
    data class VerifyShopWifi(val link: LocalPhoneTestQrLink) : QrScanRoute()
    data class Unsupported(val message: String) : QrScanRoute()
}

internal object QrScanRoutingPolicy {
    fun route(
        rawValue: String?,
        purpose: QrScanPurpose,
        expectedServiceUrl: String,
        allowLocalDevelopment: Boolean
    ): QrScanRoute = when (purpose) {
        QrScanPurpose.SHOP_CONNECTION -> {
            val link = runCatching {
                StationQrLink.parse(
                    rawValue,
                    expectedServiceUrl = expectedServiceUrl,
                    allowLocalDevelopment = allowLocalDevelopment
                )
            }.getOrNull()
            if (link != null) {
                QrScanRoute.ConnectToShop(link)
            } else {
                QrScanRoute.Unsupported(
                    "That isn't a shop connection QR. Scan the SmartPrint QR displayed at the shop counter."
                )
            }
        }
        QrScanPurpose.WIFI_READINESS -> {
            val link = runCatching { LocalPhoneTestQrLink.parse(rawValue) }.getOrNull()
            if (link != null) {
                QrScanRoute.VerifyShopWifi(link)
            } else {
                QrScanRoute.Unsupported(
                    "That isn't a Wi-Fi check QR. Scan the temporary phone-check QR shown on the shop computer. Your current shop connection has not been changed."
                )
            }
        }
    }
}
