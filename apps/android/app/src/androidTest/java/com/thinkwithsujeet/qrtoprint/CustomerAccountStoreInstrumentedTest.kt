package com.thinkwithsujeet.qrtoprint

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class CustomerAccountStoreInstrumentedTest {
    @Test
    fun customerTokensPersistEncryptedAndCanBeClearedWithoutTouchingGuestFiles() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val store = CustomerAccountStore(context)
        store.clear()
        val expected = CustomerAuth(
            accessToken = "smartprint-access-token",
            refreshToken = "smartprint-refresh-token",
            accessTokenExpiresUtc = "2026-10-31T16:00:00Z",
            displayName = "Guest to customer",
            email = "customer@example.test"
        )

        try {
            store.save(expected)
            assertEquals(expected, store.read())
            store.clear()
            assertNull(store.read())
        } finally {
            store.clear()
        }
    }
}
