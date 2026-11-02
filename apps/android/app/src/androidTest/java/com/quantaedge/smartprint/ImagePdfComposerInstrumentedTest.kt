package com.quantaedge.smartprint

import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import androidx.exifinterface.media.ExifInterface
import androidx.test.core.app.ApplicationProvider
import android.content.Context
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.FileOutputStream

class ImagePdfComposerInstrumentedTest {
    @Test
    fun combinesSelectedPhotosIntoAReadableMultipagePdf() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val first = File(context.cacheDir, "composer-first.png")
        val second = File(context.cacheDir, "composer-second.png")
        val output = File(context.cacheDir, "composer-output.pdf")
        try {
            listOf(first, second).forEachIndexed { index, file ->
                val bitmap = Bitmap.createBitmap(360 + index * 20, 480, Bitmap.Config.ARGB_8888)
                bitmap.eraseColor(if (index == 0) Color.WHITE else Color.LTGRAY)
                FileOutputStream(file).use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
                bitmap.recycle()
            }

            assertEquals(2, ImagePdfComposer.compose(listOf(first, second), output))
            ParcelFileDescriptor.open(output, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
                PdfRenderer(descriptor).use { renderer ->
                    assertEquals(2, renderer.pageCount)
                }
            }
        } finally {
            first.delete()
            second.delete()
            output.delete()
        }
    }

    @Test
    fun appliesExifRotationToReviewPreviewAndComposedPdf() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val source = File(context.cacheDir, "composer-rotated-source.jpg")
        val output = File(context.cacheDir, "composer-rotated-output.pdf")
        try {
            val bitmap = Bitmap.createBitmap(360, 480, Bitmap.Config.ARGB_8888)
            bitmap.eraseColor(Color.WHITE)
            FileOutputStream(source).use {
                assertTrue(bitmap.compress(Bitmap.CompressFormat.JPEG, 100, it))
            }
            bitmap.recycle()

            ExifInterface(source.absolutePath).apply {
                setAttribute(
                    ExifInterface.TAG_ORIENTATION,
                    ExifInterface.ORIENTATION_ROTATE_90.toString()
                )
                saveAttributes()
            }

            val preview = ImagePdfComposer.decodePreviewBitmap(source)
            assertNotNull(preview)
            preview!!
            try {
                assertEquals(480, preview.width)
                assertEquals(360, preview.height)
            } finally {
                preview.recycle()
            }

            assertEquals(1, ImagePdfComposer.compose(listOf(source), output))
            ParcelFileDescriptor.open(output, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
                PdfRenderer(descriptor).use { renderer ->
                    assertEquals(1, renderer.pageCount)
                    val page = renderer.openPage(0)
                    try {
                        assertTrue("EXIF-rotated photo should use a landscape PDF page", page.width > page.height)
                    } finally {
                        page.close()
                    }
                }
            }
        } finally {
            source.delete()
            output.delete()
        }
    }
}
