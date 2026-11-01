package com.quantaedge.smartprint

import android.content.Context
import android.graphics.Color
import android.graphics.Paint
import android.graphics.pdf.PdfDocument
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.action.ViewActions.scrollTo
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withContentDescription
import androidx.test.espresso.matcher.ViewMatchers.withId
import androidx.test.espresso.matcher.ViewMatchers.withText
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.hamcrest.Matchers.allOf
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.FileOutputStream

@RunWith(AndroidJUnit4::class)
class SavedPdfEditorInstrumentedTest {
    @Test
    fun savesReorderedAndRotatedPdfCopyWithoutTouchingSource() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val source = File(context.filesDir, "documents/CI-editor-document.pdf")
        source.parentFile?.mkdirs()
        createPdf(source, 2)
        val edited = File(source.parentFile, "CI-editor-document_edited.pdf")
        edited.delete()
        val originalSha256 = sha256(source)

        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                val navigationLabel = com.google.android.material.R.id.navigation_bar_item_small_label_view
                onView(allOf(withId(navigationLabel), withText("Files"))).perform(click())
                onView(withContentDescription("More options for CI-editor-document.pdf")).perform(scrollTo(), click())
                onView(withText("Edit PDF")).perform(click())

                onView(withText("Edit PDF")).check(matches(isDisplayed()))
                onView(withContentDescription("PDF page preview loading")).check(matches(isDisplayed()))
                onView(withContentDescription("Rotate page 1")).perform(click())
                waitForView(withText("90°"))
                onView(withContentDescription("Remove page 1")).check(matches(isDisplayed()))
                waitForView(withContentDescription("Page preview ready"))
                onView(withContentDescription("Preview page 1")).perform(click())
                onView(withText("Close")).check(matches(isDisplayed()))
                onView(withText("Close")).perform(click())
                onView(withContentDescription("Select page 2")).perform(click())
                onView(withContentDescription("Move page 2 up")).perform(click())
                onView(withContentDescription("Save edited PDF copy")).perform(click())
            }
            val saveDeadline = System.currentTimeMillis() + 10_000
            while (!edited.isFile && System.currentTimeMillis() < saveDeadline) {
                androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().waitForIdleSync()
                SystemClock.sleep(100)
            }
            assertTrue("Save copy should create a separate edited PDF in the source folder.", edited.isFile)
            ParcelFileDescriptor.open(edited, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
                PdfRenderer(descriptor).use { renderer ->
                    assertEquals("Both source pages should be preserved in the edited copy.", 2, renderer.pageCount)
                    renderer.openPage(0).use { page ->
                        assertTrue("The moved original second page should remain portrait.", page.height > page.width)
                    }
                    renderer.openPage(1).use { page ->
                        assertTrue("The rotated original first page should be landscape in the saved copy.", page.width > page.height)
                    }
                }
            }
            assertTrue("The original imported PDF must remain present.", source.isFile)
            assertTrue("The original PDF must remain non-empty.", source.length() > 0)
            assertArrayEquals(
                "Rotating, reordering or previewing pages must never modify the imported PDF.",
                originalSha256,
                sha256(source)
            )
        } finally {
            source.delete()
            edited.delete()
        }
    }

    private fun waitForView(matcher: org.hamcrest.Matcher<android.view.View>) {
        val deadline = SystemClock.uptimeMillis() + 10_000L
        var lastFailure: Throwable? = null
        while (SystemClock.uptimeMillis() < deadline) {
            try {
                onView(matcher).check(matches(isDisplayed()))
                return
            } catch (error: androidx.test.espresso.NoMatchingViewException) {
                lastFailure = error
            } catch (error: AssertionError) {
                lastFailure = error
            }
            SystemClock.sleep(100L)
        }
        throw AssertionError("Expected PDF editor content did not become visible.", lastFailure)
    }

    private fun sha256(file: File): ByteArray =
        java.security.MessageDigest.getInstance("SHA-256").digest(file.readBytes())

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
