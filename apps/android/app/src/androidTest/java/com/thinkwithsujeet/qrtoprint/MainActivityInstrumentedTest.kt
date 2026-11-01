package com.thinkwithsujeet.qrtoprint

import android.content.Context
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.action.ViewActions.closeSoftKeyboard
import androidx.test.espresso.action.ViewActions.replaceText
import androidx.test.espresso.assertion.ViewAssertions.doesNotExist
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withId
import androidx.test.espresso.matcher.ViewMatchers.withContentDescription
import androidx.test.espresso.matcher.ViewMatchers.withHint
import androidx.test.espresso.matcher.ViewMatchers.withText
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.hamcrest.Matchers.allOf
import org.hamcrest.Matchers.containsString
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class MainActivityInstrumentedTest {
    @Test
    fun rendersSmartPrintHomeAndFourTaskDestinations() {
        ActivityScenario.launch(MainActivity::class.java).use {
            onView(withText(containsString("Your documents."))).check(matches(isDisplayed()))
            onView(withText("by QuantaEdge")).check(matches(isDisplayed()))
            onView(withText("Scan a document")).check(matches(isDisplayed()))
            onView(withText("Import files")).check(matches(isDisplayed()))
            val navigationLabel = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(navigationLabel), withText("My Files"))).check(matches(isDisplayed()))
            onView(allOf(withId(navigationLabel), withText("Scan & Print"))).check(matches(isDisplayed()))
            onView(allOf(withId(navigationLabel), withText("Activity"))).check(matches(isDisplayed()))
        }
    }

    @Test
    fun guestCanOpenGlobalSmartPrintAccountAndSeeCustomerSignInOptions() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        CustomerAccountStore(context).clear()
        ActivityScenario.launch(MainActivity::class.java).use {
            onView(withContentDescription("Account")).perform(click())
            onView(withText(containsString("Every print shop."))).check(matches(isDisplayed()))
            onView(withText(containsString("Continue as a guest"))).check(matches(isDisplayed()))
            onView(withHint("Email address")).check(matches(isDisplayed()))
            onView(withHint("Password")).check(matches(isDisplayed()))
            onView(withText("Create account")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun explainsTheShopQrConnectionBeforeStartingTheScanner() {
        ActivityScenario.launch(MainActivity::class.java).use {
            val navigationLabel = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(navigationLabel), withText("Scan & Print"))).perform(click())
            onView(withText("How it works")).check(matches(isDisplayed()))
            onView(withText("Scan shop QR code")).check(matches(isDisplayed()))
            onView(withText(containsString("displayed at the counter"))).check(matches(isDisplayed()))
        }
    }

    @Test
    fun createsFolderAndNavigatesIntoAndOutOfIt() {
        ActivityScenario.launch(MainActivity::class.java).use {
            val navigationLabel = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(navigationLabel), withText("My Files"))).perform(click())
            onView(withText(containsString("New folder"))).perform(click())
            onView(withHint("Name"))
                .perform(replaceText("CI School"), closeSoftKeyboard())
            onView(withText("Save")).perform(click())

            onView(withText("CI School")).check(matches(isDisplayed()))
            onView(withText("CI School")).perform(click())
            onView(withText("Up")).check(matches(isDisplayed()))
            onView(withText("Up")).perform(click())
            onView(withText("Up")).check(doesNotExist())
        }
    }
}

@RunWith(AndroidJUnit4::class)
class QrScannerPermissionInstrumentedTest {
    @Test
    fun explainsCameraPermissionBeforeStartingTheScanner() {
        ActivityScenario.launch(QrScannerActivity::class.java).use {
            onView(withText("Camera access needed")).check(matches(isDisplayed()))
            onView(withText("Allow camera access to scan a shop QR code. Images are processed on this device and are not saved."))
                .check(matches(isDisplayed()))
            onView(withText("Enable camera")).check(matches(isDisplayed()))
        }
    }
}
