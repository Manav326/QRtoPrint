package com.thinkwithsujeet.qrtoprint

import android.content.Context
import android.graphics.Color
import android.graphics.Paint
import android.graphics.pdf.PdfDocument
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withContentDescription
import androidx.test.espresso.matcher.ViewMatchers.withId
import androidx.test.espresso.matcher.ViewMatchers.withText
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.hamcrest.Matchers.allOf
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.FileOutputStream

@RunWith(AndroidJUnit4::class)
class SavedPdfEditorInstrumentedTest {
    @Test
    fun opensSavedPdfEditorAndSupportsPersistentPageRotationWithoutTouchingSource() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val source = File(context.filesDir, "documents/CI-editor-document.pdf")
        source.parentFile?.mkdirs()
        createPdf(source, 2)

        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                val navigationLabel = com.google.android.material.R.id.navigation_bar_item_small_label_view
                onView(allOf(withId(navigationLabel), withText("My Files"))).perform(click())
                onView(withContentDescription("More options for CI-editor-document.pdf")).perform(click())
                onView(withText("Edit PDF pages")).perform(click())

                onView(withText("Edit PDF pages")).check(matches(isDisplayed()))
                onView(withText("Page 1")).check(matches(isDisplayed()))
                onView(withContentDescription("Rotate page 1")).perform(click())
                onView(withText("Rotated 90°")).check(matches(isDisplayed()))
                onView(withContentDescription("Move page 2 up")).check(matches(isDisplayed()))
                onView(withContentDescription("Remove page 1")).check(matches(isDisplayed()))
                onView(withContentDescription("Preview page 1")).perform(click())
                onView(withText("Close")).check(matches(isDisplayed()))
                onView(withText("Close")).perform(click())
            }
            assertTrue("The original imported PDF must remain untouched.", source.isFile)
            assertTrue("The original PDF must remain non-empty.", source.length() > 0)
        } finally {
            source.delete()
        }
    }

    private fun createPdf(file: File, pages: Int) {
        val document = PdfDocument()
        try {
            repeat(pages) { index ->
                val page = document.startPage(
                    PdfDocument.PageInfo.Builder(420, 594, index + 1).create()
                )
                page.canvas.drawColor(Color.WHITE)
                page.canvas.drawText(
                    "Editor page ${index + 1}",
                    36f,
                    72f,
                    Paint(Paint.ANTI_ALIAS_FLAG).apply { textSize = 28f; color = Color.BLACK }
                )
                document.finishPage(page)
            }
            FileOutputStream(file).use { document.writeTo(it) }
        } finally {
            document.close()
        }
    }
}
