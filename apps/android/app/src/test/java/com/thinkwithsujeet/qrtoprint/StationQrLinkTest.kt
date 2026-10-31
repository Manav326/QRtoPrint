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
    @Test fun acceptsPrivateLanHttpOnlyInDebugDevelopmentMode() {
        val parsed = StationQrLink.parse("http://192.168.1.20:3000/s/$code", allowLocalDevelopment = true)
        assertEquals("192.168.1.20", parsed.host)
        assertEquals(code, parsed.publicCode)
        assertEquals("http://192.168.1.20:3000/s/$code", parsed.url)
    }
    @Test fun rejectsLocalHttpOutsideDevelopmentOrOutsideThePrivateLan() {
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("http://192.168.1.20:3000/s/$code") }
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("http://203.0.113.20:3000/s/$code", allowLocalDevelopment = true) }
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("http://192.168.1.20:8080/s/$code", allowLocalDevelopment = true) }
    }
    @Test fun rejectsUnsafeOrUnrelatedQrCodes() {
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("http://print.example.test/s/$code") }
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("https://print.example.test/admin") }
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("https://print.example.test/s/short") }
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("https://print.example.test/s/${code}extra") }
        assertThrows(IllegalArgumentException::class.java) { StationQrLink.parse("https://print.example.test/s/${code}?next=1") }
    }
}
