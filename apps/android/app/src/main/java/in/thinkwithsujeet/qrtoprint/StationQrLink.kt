package in.thinkwithsujeet.qrtoprint

import android.net.Uri
import java.net.URI

data class StationQrLink(val url: String, val host: String, val publicCode: String) {
    companion object {
        private val codePattern = Regex("^[A-Za-z0-9_-]{20,160}$")

        fun parse(raw: String?): StationQrLink {
            require(!raw.isNullOrBlank()) { "QR code is empty." }
            val uri = try { URI(raw.trim()) } catch (_: Exception) {
                throw IllegalArgumentException("QR code is not a valid URL.")
            }
            val host = uri.host?.lowercase()
            val segments = uri.rawPath?.split('/')?.filter { it.isNotEmpty() }.orEmpty()
            require(uri.scheme.equals("https", true)) { "Shop QR codes must use HTTPS." }
            require(!host.isNullOrBlank() && uri.userInfo == null) { "Shop URL is invalid." }
            require(uri.port == -1 || uri.port == 443) { "Shop URL uses an unsupported port." }
            require(uri.rawQuery == null && uri.rawFragment == null) { "Shop URL must not contain a query or fragment." }
            require(segments.size == 2 && segments[0] == "s" && codePattern.matches(segments[1])) {
                "This is not a QRtoPrint station URL."
            }
            val canonical = Uri.Builder().scheme("https").encodedAuthority(host)
                .appendPath("s").appendPath(segments[1]).build().toString()
            return StationQrLink(canonical, host!!, segments[1])
        }
    }
}
