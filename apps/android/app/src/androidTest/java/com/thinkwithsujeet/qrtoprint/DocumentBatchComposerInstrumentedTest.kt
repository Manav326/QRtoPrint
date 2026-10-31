package com.thinkwithsujeet.qrtoprint

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.Paint
import android.graphics.pdf.PdfDocument
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.FileOutputStream

class DocumentBatchComposerInstrumentedTest {
    private val context: Context
        get() = ApplicationProvider.getApplicationContext()

    @Test
    fun combinesMultiplePdfsAndImagesIntoOneOrderedPrintPdf() {
        val sourcePdf = File(context.cacheDir, "batch-source.pdf")
        val image = File(context.cacheDir, "batch-photo.png")
        val output = File(context.cacheDir, "batch-output.pdf")
        try {
            createPdf(sourcePdf, pages = 2)
            val bitmap = Bitmap.createBitmap(480, 640, Bitmap.Config.ARGB_8888)
            bitmap.eraseColor(Color.LTGRAY)
            FileOutputStream(image).use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
            bitmap.recycle()

            assertEquals(3, DocumentBatchComposer.compose(listOf(sourcePdf, image), output))
            assertPdfPageCount(output, 3)
            assertPdfPageCount(sourcePdf, 2)
            assertTrue(image.isFile)
        } finally {
            sourcePdf.delete()
            image.delete()
            output.delete()
        }
    }

    @Test
    fun savesPageRemovalRotationAndReorderingAsNewPdfWithoutChangingSource() {
        val source = File(context.cacheDir, "edit-source.pdf")
        val edited = File(context.cacheDir, "edit-result.pdf")
        try {
            createPdf(source, pages = 3)
            val plan = listOf(
                PdfPageEdit(sourceIndex = 2, rotationDegrees = 90),
                PdfPageEdit(sourceIndex = 0, rotationDegrees = 0)
            )
            assertEquals(2, DocumentBatchComposer.editPdf(source, plan, edited))
            assertPdfPageCount(edited, 2)
            assertPdfPageCount(source, 3)

            val preview = DocumentBatchComposer.renderPagePreview(
                source,
                PdfPageEdit(sourceIndex = 0, rotationDegrees = 90)
            )
            try {
                assertTrue(preview.width > preview.height)
            } finally {
                preview.recycle()
            }
        } finally {
            source.delete()
            edited.delete()
        }
    }

    private fun createPdf(file: File, pages: Int) {
        val document = PdfDocument()
        try {
            repeat(pages) { index ->
                val page = document.startPage(
                    PdfDocument.PageInfo.Builder(420, 594, index + 1).create()
                )
                page.canvas.drawColor(if (index % 2 == 0) Color.WHITE else Color.LTGRAY)
                page.canvas.drawText(
                    "Source page ${index + 1}",
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

    private fun assertPdfPageCount(file: File, expected: Int) {
        ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
            PdfRenderer(descriptor).use { renderer -> assertEquals(expected, renderer.pageCount) }
        }
    }
}
