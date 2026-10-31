package com.thinkwithsujeet.qrtoprint

import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import androidx.test.core.app.ApplicationProvider
import android.content.Context
import org.junit.Assert.assertEquals
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
}
