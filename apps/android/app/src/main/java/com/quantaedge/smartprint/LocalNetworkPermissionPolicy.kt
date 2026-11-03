package com.quantaedge.smartprint

/**
 * Local-network access remains implicit for apps targeting API 36 or lower.
 * Android 16's RESTRICT_LOCAL_NETWORK compatibility change is opt-in for QA;
 * it must not cause a normal customer to see a Nearby devices prompt.
 *
 * Once the app targets API 37, Android 17 requires ACCESS_LOCAL_NETWORK at
 * runtime. Add that permission to the manifest as part of that target-SDK
 * migration, then this policy selects it only on Android 17+.
 */
internal object LocalNetworkPermissionPolicy {
    const val ANDROID_17_API = 37
    const val ACCESS_LOCAL_NETWORK_PERMISSION = "android.permission.ACCESS_LOCAL_NETWORK"

    fun permissionForDevice(osApi: Int, targetSdk: Int): String? =
        if (osApi >= ANDROID_17_API && targetSdk >= ANDROID_17_API) {
            ACCESS_LOCAL_NETWORK_PERMISSION
        } else {
            null
        }
}
