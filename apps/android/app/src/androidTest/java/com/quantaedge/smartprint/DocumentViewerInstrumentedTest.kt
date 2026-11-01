package com.quantaedge.smartprint

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.Paint
import android.graphics.pdf.PdfDocument
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.action.ViewActions.scrollTo
import androidx.test.espresso.action.ViewActions.swipeLeft
import androidx.test.espresso.action.ViewActions.swipeRight
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withContentDescription
import androidx.test.espresso.matcher.ViewMatchers.withId
import androidx.test.espresso.matcher.ViewMatchers.withText
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.FileOutputStream

@RunWith(AndroidJUnit4::class)
class DocumentViewerInstrumentedTest {
    @Test
    fun opensPdfInsideSmartPrintAndNavigatesBetweenPages() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val folderName = "CI-reader-folder-${System.currentTimeMillis()}"
        val directory = File(context.filesDir, "documents/$folderName")
        val source = File(directory, "CI-in-app-reader.pdf")
        directory.mkdirs()
        createPdf(source)

        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                val filesTab = com.google.android.material.R.id.navigation_bar_item_small_label_view
                onView(org.hamcrest.Matchers.allOf(withText("Files"), withId(filesTab))).perform(click())
                onView(withText(folderName)).perform(scrollTo(), click())
                onView(withText(source.name)).perform(scrollTo(), click())

                onView(withText("Page 1 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Next page")).perform(click())
                onView(withText("Page 2 of 2")).check(matches(isDisplayed()))
                it.recreate()
                onView(withText("Page 2 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Previous page")).perform(click())
                onView(withText("Page 1 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Open folder $folderName")).perform(click())
                onView(withText(source.name)).check(matches(isDisplayed()))
                onView(withText(source.name)).perform(scrollTo(), click())
                onView(withText("Page 1 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Back to $folderName")).perform(click())
                onView(withText(source.name)).check(matches(isDisplayed()))
            }
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun opensTheTappedImageAndRestoresItsPositionAfterRecreation() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val directory = File(context.filesDir, "documents/CI-reader-images-${System.currentTimeMillis()}")
        directory.mkdirs()
        val first = File(directory, "first.png")
        val selected = File(directory, "selected.png")
        writeTestImage(first)
        writeTestImage(selected)
        first.setLastModified(1_000L)
        selected.setLastModified(2_000L)

        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                val filesTab = com.google.android.material.R.id.navigation_bar_item_small_label_view
                onView(org.hamcrest.Matchers.allOf(withText("Files"), withId(filesTab))).perform(click())
                onView(withText(directory.name)).perform(scrollTo(), click())
                onView(withText(selected.name)).perform(scrollTo(), click())

                waitForView(withContentDescription("Image preview"))
                onView(withText("Photo 2 of 2")).check(matches(isDisplayed()))
                it.recreate()
                waitForView(withContentDescription("Image preview"))
                onView(withText("Photo 2 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Previous photo")).perform(click())
                onView(withText("Photo 1 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Next photo")).perform(click())
                onView(withText("Photo 2 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Image preview")).perform(swipeRight())
                onView(withText("Photo 1 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Image preview")).perform(swipeLeft())
                onView(withText("Photo 2 of 2")).check(matches(isDisplayed()))
                onView(withContentDescription("Back to ${directory.name}")).perform(click())
                onView(withText(selected.name)).check(matches(isDisplayed()))
            }
        } finally {
            directory.deleteRecursively()
        }
    }


    private fun waitForView(matcher: org.hamcrest.Matcher<android.view.View>) {
        val deadline = android.os.SystemClock.uptimeMillis() + 10_000L
        var lastFailure: Throwable? = null
        while (android.os.SystemClock.uptimeMillis() < deadline) {
            try {
                onView(matcher).check(matches(isDisplayed()))
                return
            } catch (error: androidx.test.espresso.NoMatchingViewException) {
                lastFailure = error
            } catch (error: AssertionError) {
                lastFailure = error
            }
            android.os.SystemClock.sleep(100L)
        }
        throw AssertionError("View did not become visible within 10 seconds.", lastFailure)
    }

    private fun writeTestImage(file: File) {
        val bitmap = Bitmap.createBitmap(640, 900, Bitmap.Config.ARGB_8888).apply {
            eraseColor(Color.rgb(245, 242, 235))
            val canvas = android.graphics.Canvas(this)
            canvas.drawColor(Color.rgb(245, 242, 235))
            canvas.drawCircle(320f, 450f, 140f, Paint(Paint.ANTI_ALIAS_FLAG).apply {
                color = Color.rgb(101, 87, 210)
            })
        }
        try {
            FileOutputStream(file).use { output ->
                assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG, 100, output))
            }
        } finally {
            bitmap.recycle()
        }
    }

    private fun createPdf(file: File) {
        val document = PdfDocument()
        try {
            repeat(2) { index ->
                val page = document.startPage(
                    PdfDocument.PageInfo.Builder(420, 594, index + 1).create()
                )
                page.canvas.drawColor(Color.WHITE)
                page.canvas.drawText(
                    "Reader page ${index + 1}",
                    36f,
                    72f,
                    Paint(Paint.ANTI_ALIAS_FLAG).apply {
                        textSize = 28f
                        color = Color.BLACK
                    }
                )
                document.finishPage(page)
            }
            FileOutputStream(file).use(document::writeTo)
        } finally {
            document.close()
        }
    }
}
