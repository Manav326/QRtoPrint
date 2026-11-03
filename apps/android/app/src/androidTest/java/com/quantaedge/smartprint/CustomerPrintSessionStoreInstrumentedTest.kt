package com.quantaedge.smartprint

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class CustomerPrintSessionStoreInstrumentedTest {
    private val shopA = "abcdefghijklmnopqrstuvwx12345678"
    private val shopB = "87654321xwvutsrqponmlkjihgfedcba"

    @Test
    fun guestSessionsPersistEncryptedAndRemainScopedPerShop() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val store = CustomerPrintSessionStore(context)
        store.clear(shopA)
        store.clear(shopB)
        try {
            store.save(shopA, "guest-session-a")
            store.save(shopB, "guest-session-b")
            assertEquals("guest-session-a", store.read(shopA))
            assertEquals("guest-session-b", store.read(shopB))
            store.clear(shopA)
            assertNull(store.read(shopA))
            assertEquals("guest-session-b", store.read(shopB))
        } finally {
            store.clear(shopA)
            store.clear(shopB)
        }
    }

    @Test(expected = IllegalArgumentException::class)
    fun rejectsInvalidStationCodes() {
        CustomerPrintSessionStore(ApplicationProvider.getApplicationContext()).save("bad", "token")
    }
}
