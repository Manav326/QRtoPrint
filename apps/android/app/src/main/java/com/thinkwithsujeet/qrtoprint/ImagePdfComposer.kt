package com.thinkwithsujeet.qrtoprint

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.pdf.PdfDocument
import java.io.File
import java.io.FileOutputStream

/**
 * Creates a simple print-ready PDF from a customer-selected ordered set of local images.
 * Source images remain untouched; each image is placed proportionally on an A4-sized page.
 */
object ImagePdfComposer {
    const val MAX_PAGES = 50
    const val MAX_SOURCE_BYTES = 100L * 1024L * 1024L
    const val MAX_OUTPUT_BYTES = 100L * 1024L * 1024L
    private const val MAX_DECODE_DIMENSION = 2400

    fun compose(images: List<File>, output: File): Int {
        require(images.isNotEmpty()) { "Select at least one photo." }
        require(images.size <= MAX_PAGES) { "A PDF can contain up to $MAX_PAGES photos at a time." }
        require(images.all { it.isFile && it.extension.lowercase() in setOf("jpg", "jpeg", "png") }) {
            "Only JPG, JPEG and PNG photos can be combined into a PDF."
        }
        require(images.sumOf { it.length() } <= MAX_SOURCE_BYTES) {
            "The selected photos exceed the 100 MB conversion limit."
        }

        val pdf = PdfDocument()
        try {
            images.forEachIndexed { index, image ->
                val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                BitmapFactory.decodeFile(image.absolutePath, bounds)
                require(bounds.outWidth > 0 && bounds.outHeight > 0) { "Could not read ${image.name}." }

                val decodeOptions = BitmapFactory.Options().apply {
                    inSampleSize = sampleSize(bounds.outWidth, bounds.outHeight)
                    inPreferredConfig = Bitmap.Config.ARGB_8888
                }
                val bitmap = BitmapFactory.decodeFile(image.absolutePath, decodeOptions)
                    ?: error("Could not decode ${image.name}.")
                try {
                    val pageWidth = if (bitmap.width > bitmap.height) 842 else 595
                    val pageHeight = if (bitmap.width > bitmap.height) 595 else 842
                    val page = pdf.startPage(
                        PdfDocument.PageInfo.Builder(pageWidth, pageHeight, index + 1).create()
                    )
                    drawFittedPage(page.canvas, bitmap, pageWidth, pageHeight)
                    pdf.finishPage(page)
                } finally {
                    bitmap.recycle()
                }
            }
            output.parentFile?.let { check(it.exists() || it.mkdirs()) { "PDF output folder could not be created." } }
            FileOutputStream(output).use { stream ->
                pdf.writeTo(stream)
                stream.fd.sync()
            }
            require(output.length() in 1..MAX_OUTPUT_BYTES) { "The generated PDF exceeds the 100 MB file limit." }
            return images.size
        } catch (error: Exception) {
            output.delete()
            throw error
        } finally {
            pdf.close()
        }
    }

    private fun sampleSize(width: Int, height: Int): Int {
        var sample = 1
        while (width / sample >= MAX_DECODE_DIMENSION ||
            height / sample >= MAX_DECODE_DIMENSION) {
            sample *= 2
        }
        return sample
    }

    private fun drawFittedPage(canvas: Canvas, bitmap: Bitmap, pageWidth: Int, pageHeight: Int) {
        canvas.drawColor(Color.WHITE)
        val margin = 22f
        val availableWidth = pageWidth - margin * 2
        val availableHeight = pageHeight - margin * 2
        val scale = minOf(availableWidth / bitmap.width, availableHeight / bitmap.height)
        val width = bitmap.width * scale
        val height = bitmap.height * scale
        val left = (pageWidth - width) / 2f
        val top = (pageHeight - height) / 2f
        val paint = Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
        canvas.drawBitmap(bitmap, null, RectF(left, top, left + width, top + height), paint)
    }
}
