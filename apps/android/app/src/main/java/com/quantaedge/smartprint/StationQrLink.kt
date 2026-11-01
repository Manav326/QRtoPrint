package com.quantaedge.smartprint

import java.net.URI

data class StationQrLink(val url: String, val host: String, val publicCode: String) {
    companion object {
        private val codePattern = Regex("^[A-Za-z0-9_-]{32}$")
        fun parse(raw: String?, allowLocalDevelopment: Boolean = false): StationQrLink {
            require(!raw.isNullOrBlank()) { "QR code is empty." }
            val uri = try { URI(raw.trim()) } catch (_: Exception) {
                throw IllegalArgumentException("QR code is not a valid URL.")
            }
            val host = uri.host?.lowercase()
            val segments = uri.rawPath?.split('/')?.filter { it.isNotEmpty() }.orEmpty()
            val isHttps = uri.scheme.equals("https", true) && (uri.port == -1 || uri.port == 443)
            val isLocalDevelopmentHttp = allowLocalDevelopment &&
                uri.scheme.equals("http", true) && uri.port == 3000 &&
                !host.isNullOrBlank() && LocalAgentUploadPolicy.isPrivateIpv4Host(host)
            require(isHttps || isLocalDevelopmentHttp) {
                "Shop QR codes must use HTTPS. Local debug builds may use a private-LAN HTTP URL on port 3000."
            }
            require(!host.isNullOrBlank() && uri.userInfo == null) { "Shop URL is invalid." }
            require(uri.rawQuery == null && uri.rawFragment == null) { "Shop URL must not contain a query or fragment." }
            require(segments.size == 2 && segments[0] == "s" && codePattern.matches(segments[1])) {
                "This is not a QRtoPrint station URL."
            }
            val scheme = if (isHttps) "https" else "http"
            val port = if (isLocalDevelopmentHttp) 3000 else -1
            val canonical = URI(scheme, null, host, port, "/s/${segments[1]}", null, null).toASCIIString()
            return StationQrLink(canonical, host!!, segments[1])
        }
    }
}
