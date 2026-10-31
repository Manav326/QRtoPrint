package com.thinkwithsujeet.qrtoprint

import androidx.test.core.app.ActivityScenario
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.action.ViewActions.closeSoftKeyboard
import androidx.test.espresso.action.ViewActions.replaceText
import androidx.test.espresso.assertion.ViewAssertions.doesNotExist
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withId
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
    fun rendersDocumentLibraryAndPrimaryNavigation() {
        ActivityScenario.launch(MainActivity::class.java).use {
            onView(withText("Keep everything ready")).check(matches(isDisplayed()))
            onView(withText("Import files")).check(matches(isDisplayed()))
            onView(withText("Import folder")).check(matches(isDisplayed()))
            val navigationLabel = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(navigationLabel), withText("Scan QR"))).check(matches(isDisplayed()))
            onView(allOf(withId(navigationLabel), withText("Print"))).check(matches(isDisplayed()))
        }
    }

    @Test
    fun createsFolderAndNavigatesIntoAndOutOfIt() {
        ActivityScenario.launch(MainActivity::class.java).use {
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
