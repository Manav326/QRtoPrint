package com.thinkwithsujeet.qrtoprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class StationQrLinkTest {
    private val code = "abcdefghijklmnopqrstuvwx12345678"

    @Test fun acceptsTheCurrentPortalStationRoute() {
        val parsed = StationQrLink.parse("https://print.example.test/s/$code")
        assertEquals("print.example.test", parsed.host)
        assertEquals(code, parsed.publicCode)
        assertEquals("https://print.example.test/s/$code", parsed.url)
    }
    @Test fun rejectsUnsafeOrUnrelatedQrCodes() {
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("http://print.example.test/s/$code") }
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("https://print.example.test/admin") }
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("https://print.example.test/s/short") }
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("https://print.example.test/s/${code}extra") }
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("https://print.example.test/s/${code}?next=1") }
    }
}
