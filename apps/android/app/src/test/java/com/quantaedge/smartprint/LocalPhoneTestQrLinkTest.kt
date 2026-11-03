package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class LocalPhoneTestQrLinkTest {
    private val token = "A".repeat(43)

    @Test
    fun acceptsTheShopOwnersShortLivedPrivateAgentReadinessUrl() {
        val link = LocalPhoneTestQrLink.parse(
            "http://192.168.1.20:17842/api/v1/local/connection-check?token=$token"
        )

        assertEquals("192.168.1.20", link.host)
        assertEquals(
            "http://192.168.1.20:17842/api/v1/local/connection-check?token=$token",
            link.url
        )
    }

    @Test
    fun acceptsAllRfc1918ShopAddressRanges() {
        listOf(
            "http://10.20.30.40:17842/api/v1/local/connection-check?token=$token",
            "http://172.20.30.40:17842/api/v1/local/connection-check?token=$token",
            "http://192.168.50.10:17842/api/v1/local/connection-check?token=$token"
        ).forEach { assertEquals(token, LocalPhoneTestQrLink.parse(it).url.substringAfter("token=")) }
    }

    @Test
    fun rejectsPublicHostsWrongPortsPathsAndUnexpectedQueryParameters() {
        listOf(
            "https://192.168.1.20:17842/api/v1/local/connection-check?token=$token",
            "http://8.8.8.8:17842/api/v1/local/connection-check?token=$token",
            "http://localhost:17842/api/v1/local/connection-check?token=$token",
            "http://192.168.1.20:8080/api/v1/local/connection-check?token=$token",
            "http://192.168.1.20:17842/api/v1/local/status?token=$token",
            "http://192.168.1.20:17842/api/v1/local/connection-check?token=$token&next=https://example.com",
            "http://user@192.168.1.20:17842/api/v1/local/connection-check?token=$token",
            "http://192.168.001.020:17842/api/v1/local/connection-check?token=$token",
            "http://192.168.1.20:17842/api/v1/local/connection-check?token=short"
        ).forEach { raw ->
            assertThrows(IllegalArgumentException::class.java) { LocalPhoneTestQrLink.parse(raw) }
        }
    }
}
