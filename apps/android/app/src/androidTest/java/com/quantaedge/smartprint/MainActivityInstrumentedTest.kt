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
import eightbitlab.com.blurview.BlurView
import android.view.View
import android.view.ViewGroup
import org.junit.Assert.assertEquals

@RunWith(AndroidJUnit4::class)
class MainActivityInstrumentedTest {
    @Test
    fun rendersCompactSmartPrintHomeAndFourDestinations() {
        ActivityScenario.launch(MainActivity::class.java).use {
            onView(withText("Your space")).check(matches(isDisplayed()))
            onView(withText("Recent")).check(matches(isDisplayed()))
            onView(withText("Scan document")).check(matches(isDisplayed()))
            onView(withText("Import")).check(matches(isDisplayed()))
            val label = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(label), withText("Files"))).check(matches(isDisplayed()))
            onView(allOf(withId(label), withText("Print"))).check(matches(isDisplayed()))
            onView(allOf(withId(label), withText("Activity"))).check(matches(isDisplayed()))
        }
    }

    @Test
    fun nativeShellUsesTwoRealBlurOverlaysForItsNavigationChrome() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { activity ->
                val content = activity.findViewById<ViewGroup>(android.R.id.content)
                fun countBlurViews(view: View): Int {
                    if (view is BlurView) return 1
                    if (view !is ViewGroup) return 0
                    var count = 0
                    for (index in 0 until view.childCount) count += countBlurViews(view.getChildAt(index))
                    return count
                }
                assertEquals(2, countBlurViews(content))
            }
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
            onView(allOf(withId(label), withText("Print"))).perform(click())
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
    fun systemBackFromPrintTabReturnsHome() {
        ActivityScenario.launch(MainActivity::class.java).use {
            val label = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(label), withText("Print"))).perform(click())
            onView(withText("Connect to a shop")).check(matches(isDisplayed()))
            androidx.test.espresso.Espresso.pressBack()
            onView(withText("Your space")).check(matches(isDisplayed()))
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
            onView(withText("Scan here")).check(matches(isDisplayed()))
            it.recreate()
            onView(withText("Scan here")).check(matches(isDisplayed()))
            onView(withContentDescription("Open folder My Files")).perform(click())
            onView(withText(folderName)).check(matches(isDisplayed()))
            onView(withText(folderName)).perform(click())
            onView(withText("Nested")).check(matches(isDisplayed()))
            onView(withContentDescription("Open folder My Files")).perform(click())
            onView(withContentDescription("More options for $folderName")).perform(click())
            onView(withText("Expand in list")).perform(click())
            onView(withText("Nested")).check(matches(isDisplayed()))
            onView(withContentDescription("More options for $folderName")).perform(click())
            onView(withText("Collapse in list")).check(matches(isDisplayed()))
        }
    }

    @Test
    fun systemBackFromNestedFolderReturnsToItsParent() {
        val folderName = "CI Back Folder " + System.currentTimeMillis()
        ActivityScenario.launch(MainActivity::class.java).use {
            val label = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(label), withText("Files"))).perform(click())
            onView(withText("New folder")).perform(click())
            onView(withHint("Name")).perform(replaceText(folderName), closeSoftKeyboard())
            onView(withText("Save")).perform(click())
            onView(withText(folderName)).perform(click())
            onView(withText("New folder")).perform(click())
            onView(withHint("Name")).perform(replaceText("Nested"), closeSoftKeyboard())
            onView(withText("Save")).perform(click())
            onView(withText("Nested")).check(matches(isDisplayed()))
            onView(withText("Nested")).perform(click())
            onView(withText("Scan here")).check(matches(isDisplayed()))

            androidx.test.espresso.Espresso.pressBack()
            onView(withText("Scan here")).check(matches(isDisplayed()))
            onView(withText("Nested")).check(matches(isDisplayed()))
            androidx.test.espresso.Espresso.pressBack()
            onView(withText(folderName)).check(matches(isDisplayed()))
        }
    }

    @Test
    fun fileContextActionsUseBottomSheet() {
        val folderName = "CI Sheet Folder " + System.currentTimeMillis()
        ActivityScenario.launch(MainActivity::class.java).use {
            val label = com.google.android.material.R.id.navigation_bar_item_small_label_view
            onView(allOf(withId(label), withText("Files"))).perform(click())
            onView(withText("New folder")).perform(click())
            onView(withHint("Name")).perform(replaceText(folderName), closeSoftKeyboard())
            onView(withText("Save")).perform(click())
            onView(withContentDescription("More options for " + folderName)).perform(click())
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
