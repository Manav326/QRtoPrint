package com.quantaedge.smartprint

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LocalAgentUploadPolicyTest {
    @Test
    fun acceptsOnlyRfc1918Ipv4Ranges() {
        assertTrue(LocalAgentUploadPolicy.isPrivateIpv4Host("10.0.0.8"))
        assertTrue(LocalAgentUploadPolicy.isPrivateIpv4Host("172.16.1.20"))
        assertTrue(LocalAgentUploadPolicy.isPrivateIpv4Host("172.31.255.254"))
        assertTrue(LocalAgentUploadPolicy.isPrivateIpv4Host("192.168.1.50"))
        assertFalse(LocalAgentUploadPolicy.isPrivateIpv4Host("172.15.1.20"))
        assertFalse(LocalAgentUploadPolicy.isPrivateIpv4Host("172.32.1.20"))
        assertFalse(LocalAgentUploadPolicy.isPrivateIpv4Host("192.167.1.50"))
        assertFalse(LocalAgentUploadPolicy.isPrivateIpv4Host("8.8.8.8"))
    }

    @Test
    fun rejectsMalformedDottedHostsAndAmbiguousOctets() {
        assertFalse(LocalAgentUploadPolicy.isPrivateIpv4Host("10.shop.1.2.3"))
        assertFalse(LocalAgentUploadPolicy.isPrivateIpv4Host("10.0.0"))
        assertFalse(LocalAgentUploadPolicy.isPrivateIpv4Host("10.0.0.999"))
        assertFalse(LocalAgentUploadPolicy.isPrivateIpv4Host("010.0.0.1"))
        assertFalse(LocalAgentUploadPolicy.isPrivateIpv4Host("10.-1.0.1"))
        assertFalse(LocalAgentUploadPolicy.isPrivateIpv4Host("shop.local"))
    }

    @Test
    fun allowsOnlyThePairedAgentStatusGetAndUploadPost() {
        assertTrue(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/api/v1/local/status", "GET"
        ))
        assertTrue(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/api/v1/local/uploads/550e8400-e29b-41d4-a716-446655440000", "POST"
        ))
        assertFalse(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/api/v1/local/status", "POST"
        ))
        assertFalse(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/api/v1/local/uploads/550e8400-e29b-41d4-a716-446655440000", "GET"
        ))
        assertFalse(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/api/v1/local/uploads/550e8400-e29b-41d4-a716-446655440000/other", "POST"
        ))
        assertFalse(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/api/v1/local/uploads/not-a-ticket", "POST"
        ))
        assertFalse(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/health", "GET"
        ))
        assertFalse(LocalAgentUploadPolicy.allows(
            "https", "192.168.1.4", 17842, "/api/v1/local/status", "GET"
        ))
        assertFalse(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 80, "/api/v1/local/status", "GET"
        ))
        assertFalse(LocalAgentUploadPolicy.allows(
            "http", "example.com", 17842, "/api/v1/local/status", "GET"
        ))
    }

    @Test
    fun allowsOnlyMatchingCorsAndLocalNetworkPreflights() {
        assertTrue(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/api/v1/local/status", "OPTIONS", "GET"
        ))
        assertTrue(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/api/v1/local/uploads/550e8400-e29b-41d4-a716-446655440000", "OPTIONS", "POST"
        ))
        assertFalse(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/api/v1/local/status", "OPTIONS", "POST"
        ))
        assertFalse(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/api/v1/local/uploads/550e8400-e29b-41d4-a716-446655440000", "OPTIONS", "GET"
        ))
        assertFalse(LocalAgentUploadPolicy.allows(
            "http", "192.168.1.4", 17842, "/api/v1/local/status", "OPTIONS"
        ))
    }
}
