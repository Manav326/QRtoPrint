package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class StationQrLinkTest {
    private val code = "abcdefghijklmnopqrstuvwx12345678"
    private val service = "https://print.example.test"

    @Test fun acceptsTheConfiguredPortalStationRoute() {
        val parsed = StationQrLink.parse("https://print.example.test/s/$code", service)
        assertEquals("print.example.test", parsed.host)
        assertEquals(code, parsed.publicCode)
        assertEquals("https://print.example.test/s/$code", parsed.url)
    }

    @Test fun rejectsAnotherHttpsOriginEvenWhenThePathLooksValid() {
        assertThrows(IllegalArgumentException::class.java) {
            StationQrLink.parse("https://attacker.example/s/$code", service)
        }
        assertThrows(IllegalArgumentException::class.java) {
            StationQrLink.parse("https://print.example.test:444/s/$code", service)
        }
    }

    @Test fun acceptsPrivateLanHttpOnlyInDebugDevelopmentMode() {
        val parsed = StationQrLink.parse(
            "http://192.168.1.20:3000/s/$code",
            service,
            allowLocalDevelopment = true
        )
        assertEquals("192.168.1.20", parsed.host)
        assertEquals(code, parsed.publicCode)
        assertEquals("http://192.168.1.20:3000/s/$code", parsed.url)
    }

    @Test fun rejectsLocalHttpOutsideDevelopmentOrOutsideThePrivateLan() {
        assertThrows(IllegalArgumentException::class.java) {
            StationQrLink.parse("http://192.168.1.20:3000/s/$code", service)
        }
        assertThrows(IllegalArgumentException::class.java) {
            StationQrLink.parse("http://203.0.113.20:3000/s/$code", service, allowLocalDevelopment = true)
        }
        assertThrows(IllegalArgumentException::class.java) {
            StationQrLink.parse("http://192.168.1.20:8080/s/$code", service, allowLocalDevelopment = true)
        }
    }

    @Test fun rejectsUnsafeOrUnrelatedQrCodes() {
        listOf(
            "http://print.example.test/s/$code",
            "https://print.example.test/admin",
            "https://print.example.test/s/short",
            "https://print.example.test/s/${code}extra",
            "https://print.example.test/s/$code?next=1",
            "https://user@print.example.test/s/$code"
        ).forEach { raw ->
            assertThrows(IllegalArgumentException::class.java) {
                StationQrLink.parse(raw, service)
            }
        }
    }
}
