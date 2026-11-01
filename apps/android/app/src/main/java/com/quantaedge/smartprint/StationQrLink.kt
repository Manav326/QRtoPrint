package com.quantaedge.smartprint

import java.net.URI

data class StationQrLink(val url: String, val host: String, val publicCode: String) {
    companion object {
        private val codePattern = Regex("^[A-Za-z0-9_-]{32}$")

        /**
         * The station URL must use the exact service origin configured for this build.
         * A debug build may be configured to use a private IPv4 HTTP origin on port 3000.
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
            val expectedHost = expected.host?.lowercase()
            val expectedScheme = expected.scheme?.lowercase()
            val expectedIsHttps = expectedScheme == "https" && !expectedHost.isNullOrBlank() &&
                expected.userInfo == null && expected.port in listOf(-1, 443)
            val expectedIsDebugHttp = allowLocalDevelopment && expectedScheme == "http" &&
                expected.port == 3000 && !expectedHost.isNullOrBlank() &&
                LocalAgentUploadPolicy.isPrivateIpv4Host(expectedHost)
            require(expectedIsHttps || expectedIsDebugHttp) {
                "SmartPrint service origin must be HTTPS. Debug builds may use a configured private IPv4 HTTP origin on port 3000."
            }

            val expectedPort = if (expected.port == -1) {
                if (expectedScheme == "https") 443 else 80
            } else expected.port
            val actualPort = if (uri.port == -1) {
                if (uri.scheme.equals("https", true)) 443 else 80
            } else uri.port
            val matchesConfiguredOrigin = uri.scheme.equals(expectedScheme, true) &&
                host.equals(expectedHost, true) && actualPort == expectedPort

            require(matchesConfiguredOrigin) {
                "This QR code is not from the configured SmartPrint service. Check that you scanned the shop's official QR code."
            }

            val canonicalPort = if ((expectedScheme == "https" && expectedPort == 443) ||
                (expectedScheme == "http" && expectedPort == 80)) -1 else expectedPort
            val canonical = URI(expectedScheme, null, host, canonicalPort, "/s/${segments[1]}", null, null).toASCIIString()
            return StationQrLink(canonical, host, segments[1])
        }
    }
}
