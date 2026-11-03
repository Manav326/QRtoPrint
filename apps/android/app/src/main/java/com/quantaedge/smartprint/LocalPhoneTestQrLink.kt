package com.quantaedge.smartprint

import java.net.URI

/**
 * The shop-owner phone-readiness QR is a one-time local Agent URL, not a customer
 * station URL. Keep this parser deliberately narrower than a general URL parser.
 */
internal data class LocalPhoneTestQrLink(
    val url: String,
    val host: String
) {
    companion object {
        private const val AGENT_PORT = 17842
        private const val CONNECTION_CHECK_PATH = "/api/v1/local/connection-check"
        private val tokenPattern = Regex("^[A-Za-z0-9_-]{43}$")

        fun parse(raw: String?): LocalPhoneTestQrLink {
            require(!raw.isNullOrBlank()) { "Phone readiness QR is empty." }
            val uri = try { URI(raw.trim()) } catch (_: Exception) {
                throw IllegalArgumentException("Phone readiness QR is not a valid URL.")
            }
            val host = uri.host?.lowercase() ?: throw IllegalArgumentException("Phone readiness host is missing.")
            require(uri.scheme.equals("http", true)) { "Phone readiness checks must use the local HTTP Agent." }
            require(uri.userInfo == null && uri.rawFragment == null) {
                "Phone readiness URL contains unsupported URL parts."
            }
            require(uri.port == AGENT_PORT && uri.rawPath == CONNECTION_CHECK_PATH) {
                "This is not a SmartPrint Agent phone-readiness URL."
            }
            require(isPrivateIpv4(host)) {
                "Phone readiness QR must point to a private IPv4 shop-computer address."
            }
            val query = uri.rawQuery.orEmpty()
            require(query.startsWith("token=") && tokenPattern.matches(query.removePrefix("token="))) {
                "Phone readiness QR token is missing or invalid."
            }
            return LocalPhoneTestQrLink(uri.toASCIIString(), host)
        }

        private fun isPrivateIpv4(host: String): Boolean {
            val parts = host.split('.')
            if (parts.size != 4 || parts.any { part ->
                    part.isEmpty() || (part.length > 1 && part.startsWith('0')) ||
                        part.any { !it.isDigit() } || part.toIntOrNull() == null ||
                        part.toInt() !in 0..255
                }) return false
            val octets = parts.map { it.toInt() }
            return octets[0] == 10 ||
                (octets[0] == 172 && octets[1] in 16..31) ||
                (octets[0] == 192 && octets[1] == 168)
        }
    }
}
