package com.quantaedge.smartprint

import android.content.Context
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.action.ViewActions.closeSoftKeyboard
import androidx.test.espresso.action.ViewActions.replaceText
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withId
import androidx.test.espresso.matcher.ViewMatchers.withContentDescription
import androidx.test.espresso.matcher.ViewMatchers.withHint
import androidx.test.espresso.matcher.ViewMatchers.withText
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.hamcrest.Matchers.allOf
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class MainActivityInstrumentedTest {
    @Test
    fun rendersCompactSmartPrintHomeAndFourDestinations() {
        ActivityScenario.launch(MainActivity::class.java).use {
            onView(withText("Your space")).check(matches(isDisplayed()))
            onView(withText("Recent")).check(matches(isDisplayed()))
            onView(withText("Import")).check(matches(isDisplayed()))
            val label = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(label), withText("Files"))).check(matches(isDisplayed()))
            onView(allOf(withId(label), withText("Scan"))).check(matches(isDisplayed()))
            onView(allOf(withId(label), withText("Activity"))).check(matches(isDisplayed()))
        }
    }

    @Test
    fun guestAccountShowsCompactSignInForm() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        CustomerAccountStore(context).clear()
        ActivityScenario.launch(MainActivity::class.java).use {
            onView(withContentDescription("Account")).perform(click())
            onView(withText("Guest access is ready")).check(matches(isDisplayed()))
            onView(withHint("Email address")).check(matches(isDisplayed()))
            onView(withHint("Password")).check(matches(isDisplayed()))
            onView(withText("Create account")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun scanDestinationUsesFocusedQrEntry() {
        ActivityScenario.launch(MainActivity::class.java).use {
            val label = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(label), withText("Scan"))).perform(click())
            onView(withText("Connect to a shop")).check(matches(isDisplayed()))
            onView(withText("Scan QR code")).check(matches(isDisplayed()))
            onView(withText("Open My Files")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun systemBackReturnsToPreviouslySelectedDestination() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        CustomerAccountStore(context).clear()
        ActivityScenario.launch(MainActivity::class.java).use {
            val label = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(label), withText("Activity"))).perform(click())
            onView(withContentDescription("Account")).perform(click())
            onView(withText("Guest access is ready")).check(matches(isDisplayed()))
            androidx.test.espresso.Espresso.pressBack()
            onView(withText("Print history")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun foldersCanOpenAndExpandInline() {
        val folderName = "CI School " + System.currentTimeMillis()
        ActivityScenario.launch(MainActivity::class.java).use {
            val label = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(label), withText("Files"))).perform(click())
            onView(withText("New folder")).perform(click())
            onView(withHint("Name")).perform(replaceText(folderName), closeSoftKeyboard())
            onView(withText("Save")).perform(click())

            onView(withText(folderName)).check(matches(isDisplayed()))
            onView(withText(folderName)).perform(click())
            onView(withText("New folder")).perform(click())
            onView(withHint("Name")).perform(replaceText("Nested"), closeSoftKeyboard())
            onView(withText("Save")).perform(click())
            onView(withText("Nested")).check(matches(isDisplayed()))
            onView(withText("Back")).perform(click())
            onView(withContentDescription("Expand $folderName")).perform(click())
            onView(withText("Nested")).check(matches(isDisplayed()))
            onView(withContentDescription("Collapse $folderName")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun fileContextActionsUseBottomSheet() {
        ActivityScenario.launch(MainActivity::class.java).use {
            val label = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(label), withText("Files"))).perform(click())
            onView(withText("New folder")).perform(click())
            onView(withHint("Name")).perform(replaceText("CI Sheet Folder"), closeSoftKeyboard())
            onView(withText("Save")).perform(click())
            onView(withContentDescription("More options for CI Sheet Folder")).perform(click())
            onView(withText("Folder actions")).check(matches(isDisplayed()))
            onView(withText("Rename")).check(matches(isDisplayed()))
            onView(withText("Delete")).check(matches(isDisplayed()))
        }
    }
}

@RunWith(AndroidJUnit4::class)
class QrScannerPermissionInstrumentedTest {
    @Test
    fun explainsCameraPermissionBeforeStartingTheScanner() {
        ActivityScenario.launch(QrScannerActivity::class.java).use {
            onView(withText("Camera access needed")).check(matches(isDisplayed()))
            onView(withText("Allow camera for QR scanning."))
                .check(matches(isDisplayed()))
            onView(withText("Allow camera")).check(matches(isDisplayed()))
        }
    }
}
