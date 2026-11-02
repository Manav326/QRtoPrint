package com.quantaedge.smartprint

/**
 * Cleartext is permitted only for the paired shop Agent's private IPv4 status and upload endpoints.
 * The HTTPS customer portal and every other WebView resource stay on HTTPS.
 */
internal object LocalAgentUploadPolicy {
    fun allows(
        scheme: String,
        host: String,
        port: Int,
        path: String,
        method: String,
        preflightMethod: String? = null
    ): Boolean {
        if (!scheme.equals("http", ignoreCase = true) ||
            port != 17842 ||
            !isPrivateIpv4Host(host)) return false

        val requestMethod = method.uppercase()
        val requestedMethod = preflightMethod?.uppercase()
        return when {
            path == "/api/v1/local/status" ->
                requestMethod == "GET" || (requestMethod == "OPTIONS" && requestedMethod == "GET")
            path.startsWith("/api/v1/local/uploads/") ->
                requestMethod == "POST" || (requestMethod == "OPTIONS" && requestedMethod == "POST")
            else -> false
        }
    }

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
