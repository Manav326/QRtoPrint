package com.quantaedge.smartprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class LocalNetworkPermissionPolicyTest {
    @Test
    fun requestsTheAndroid16TransitionalPermissionOnlyOnAndroid16() {
        assertEquals(
            LocalNetworkPermissionPolicy.NEARBY_WIFI_DEVICES_PERMISSION,
            LocalNetworkPermissionPolicy.permissionForDevice(osApi = 36, targetSdk = 35)
        )
        assertEquals(
            LocalNetworkPermissionPolicy.NEARBY_WIFI_DEVICES_PERMISSION,
            LocalNetworkPermissionPolicy.permissionForDevice(osApi = 36, targetSdk = 36)
        )
        assertNull(LocalNetworkPermissionPolicy.permissionForDevice(osApi = 35, targetSdk = 35))
        assertNull(LocalNetworkPermissionPolicy.permissionForDevice(osApi = 37, targetSdk = 35))
        assertNull(LocalNetworkPermissionPolicy.permissionForDevice(osApi = 37, targetSdk = 37))
    }
}
