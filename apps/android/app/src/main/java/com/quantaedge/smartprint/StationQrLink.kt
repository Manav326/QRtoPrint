package com.quantaedge.smartprint

import java.net.URI

data class StationQrLink(val url: String, val host: String, val publicCode: String) {
    companion object {
        private val codePattern = Regex("^[A-Za-z0-9_-]{32}$")

        /**
         * Production QR codes must resolve to the service origin compiled into the app.
         * Debug builds may additionally use a private IPv4 HTTP portal on port 3000.
         */
        fun parse(
            raw: String?,
            expectedServiceUrl: String = BuildConfig.SERVICE_BASE_URL,
            allowLocalDevelopment: Boolean = false
        ): StationQrLink {
            require(!raw.isNullOrBlank()) { "QR code is empty." }
            val uri = try { URI(raw.trim()) } catch (_: Exception) {
                throw IllegalArgumentException("QR code is not a valid URL.")
            }
            val host = uri.host?.lowercase()
            require(!host.isNullOrBlank() && uri.userInfo == null) { "Shop URL is invalid." }
            require(uri.rawQuery == null && uri.rawFragment == null) {
                "Shop URL must not contain a query or fragment."
            }
            val segments = uri.rawPath?.split('/')?.filter { it.isNotEmpty() }.orEmpty()
            require(segments.size == 2 && segments[0] == "s" && codePattern.matches(segments[1])) {
                "This is not a QRtoPrint station URL."
            }

            val expected = try { URI(expectedServiceUrl.trim().trimEnd('/')) } catch (_: Exception) {
                throw IllegalArgumentException("SmartPrint service origin is not configured correctly.")
            }
            require(expected.scheme.equals("https", true) && !expected.host.isNullOrBlank() &&
                expected.userInfo == null && expected.port in listOf(-1, 443)) {
                "SmartPrint service origin must be a valid HTTPS origin."
            }

            val matchesConfiguredOrigin = uri.scheme.equals("https", true) &&
                host.equals(expected.host, true) && (uri.port == -1 || uri.port == 443)
            val isLocalDevelopmentHttp = allowLocalDevelopment &&
                uri.scheme.equals("http", true) && uri.port == 3000 &&
                LocalAgentUploadPolicy.isPrivateIpv4Host(host)

            require(matchesConfiguredOrigin || isLocalDevelopmentHttp) {
                "This QR code is not from the configured SmartPrint service. Check that you scanned the shop's official QR code."
            }

            val scheme = if (matchesConfiguredOrigin) "https" else "http"
            val port = if (isLocalDevelopmentHttp) 3000 else -1
            val canonical = URI(scheme, null, host, port, "/s/${segments[1]}", null, null).toASCIIString()
            return StationQrLink(canonical, host, segments[1])
        }
    }
}
