package com.quantaedge.smartprint

/**
 * Android 16's local-network protection opt-in uses NEARBY_WIFI_DEVICES.
 * Apps targeting Android 17/API 37 will need the newer ACCESS_LOCAL_NETWORK
 * permission instead; this app currently targets API 35 and must not request it.
 */
internal object LocalNetworkPermissionPolicy {
    const val ANDROID_16_API = 36
    const val NEARBY_WIFI_DEVICES_PERMISSION = "android.permission.NEARBY_WIFI_DEVICES"

    fun permissionForDevice(osApi: Int, targetSdk: Int): String? =
        if (osApi == ANDROID_16_API && targetSdk <= ANDROID_16_API) {
            NEARBY_WIFI_DEVICES_PERMISSION
        } else {
            null
        }
}
