package com.quantaedge.smartprint

/**
 * Cleartext is permitted only for the paired shop Agent's private IPv4 upload
 * route and its two read-only connection-check routes. Other WebView resources stay on HTTPS.
 */
internal object LocalAgentUploadPolicy {
    fun allows(scheme: String, host: String, port: Int, path: String): Boolean =
        scheme.equals("http", ignoreCase = true) &&
            port == 17842 &&
            (path.startsWith("/api/v1/local/uploads/") ||
                path == "/api/v1/local/status" ||
                path == "/api/v1/local/connection-check") &&
            isPrivateIpv4Host(host)

    fun isPrivateIpv4Host(host: String): Boolean {
        val segments = host.split('.')
        if (segments.size != 4) return false
        if (segments.any { segment ->
                segment.isEmpty() ||
                    (segment.length > 1 && segment.startsWith('0')) ||
                    segment.any { !it.isDigit() }
            }) return false

        val octets = segments.map { it.toIntOrNull() ?: return false }
        if (octets.any { it !in 0..255 }) return false
        return octets[0] == 10 ||
            (octets[0] == 172 && octets[1] in 16..31) ||
            (octets[0] == 192 && octets[1] == 168)
    }
}
