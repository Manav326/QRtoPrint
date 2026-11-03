package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class LocalNetworkPermissionPolicyTest {
    @Test
    fun doesNotPromptOnAndroid16WhenTargetingApi36OrLower() {
        assertNull(LocalNetworkPermissionPolicy.permissionForDevice(osApi = 36, targetSdk = 35))
        assertNull(LocalNetworkPermissionPolicy.permissionForDevice(osApi = 36, targetSdk = 36))
        assertNull(LocalNetworkPermissionPolicy.permissionForDevice(osApi = 35, targetSdk = 35))
    }

    @Test
    fun requestsAndroid17LocalNetworkPermissionOnlyForAndroid17Targets() {
        assertNull(LocalNetworkPermissionPolicy.permissionForDevice(osApi = 36, targetSdk = 37))
        assertNull(LocalNetworkPermissionPolicy.permissionForDevice(osApi = 37, targetSdk = 36))
        assertEquals(
            LocalNetworkPermissionPolicy.ACCESS_LOCAL_NETWORK_PERMISSION,
            LocalNetworkPermissionPolicy.permissionForDevice(osApi = 37, targetSdk = 37)
        )
        assertEquals(
            LocalNetworkPermissionPolicy.ACCESS_LOCAL_NETWORK_PERMISSION,
            LocalNetworkPermissionPolicy.permissionForDevice(osApi = 38, targetSdk = 37)
        )
    }
}
