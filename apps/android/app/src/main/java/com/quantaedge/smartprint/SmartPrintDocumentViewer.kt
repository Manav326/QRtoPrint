package com.quantaedge.smartprint

import android.app.Activity
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import android.view.GestureDetector
import android.view.Gravity
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.View
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.appcompat.widget.AppCompatImageView
import com.google.android.material.button.MaterialButton
import com.google.android.material.card.MaterialCardView
import java.io.File
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

/**
 * Private, read-only viewer for documents already stored in SmartPrint.
 *
 * PDF pages and thumbnails are rendered off the UI thread. Only the current
 * page and a small thumbnail window are decoded at a time to keep memory use
 * bounded even for long documents.
 */
class SmartPrintDocumentViewer(
    context: Context,
    startFile: File,
    private val imageSiblings: List<File>,
    private val onNavigateImage: (File) -> Unit
) : LinearLayout(context) {
    companion object {
        private const val MAX_PAGE_EDGE = 2200
        private const val MAX_THUMB_EDGE = 180
        private const val MAX_IMAGE_EDGE = 2600
        private const val THUMBNAIL_RADIUS_DP = 10
    }

    private data class PageThumbnail(val pageIndex: Int, val bitmap: Bitmap)
    private data class PdfFrame(
        val pageCount: Int,
        val pageIndex: Int,
        val pageBitmap: Bitmap,
        val thumbnails: List<PageThumbnail>
    )

    private val host = context as? Activity ?: error("Document viewer requires an Activity context.")
    private val worker: ExecutorService = Executors.newSingleThreadExecutor()
    private val disposed = AtomicBoolean(false)
    private val stage = FrameLayout(context)
    private val pageImage = ZoomableDocumentImageView(context) { direction ->
        if (!isPdf) navigateImage(direction)
    }
    private val feedback = TextView(context).apply {
        gravity = Gravity.CENTER
        textSize = 13f
        setTextColor(Color.WHITE)
        setPadding(dp(24), dp(12), dp(24), dp(12))
    }
    private val pageLabel = TextView(context).apply {
        textSize = 12f
        gravity = Gravity.CENTER
        setTextColor(getColor(R.color.qp_text))
        contentDescription = "Document page position"
    }
    private val previousButton = actionButton("‹", "Previous page")
    private val nextButton = actionButton("›", "Next page")
    private val rotateButton = actionButton("↻", "Rotate current page")
    private val imagePreviousButton = actionButton("‹", "Previous photo")
    private val imageNextButton = actionButton("›", "Next photo")
    private val thumbnailScroller = HorizontalScrollView(context).apply {
        isHorizontalScrollBarEnabled = false
        clipToPadding = false
        setPadding(dp(8), dp(6), dp(8), dp(8))
        visibility = View.GONE
    }
    private val thumbnailRow = LinearLayout(context).apply {
        orientation = HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
    }

    private var currentFile: File = startFile
    private var currentPage = 0
    private var pageCount = 0
    private var rotationDegrees = 0
    private var renderVersion = 0
    private var currentImageIndex = imageSiblings.indexOfFirst {
        sameFile(it, startFile)
    }.coerceAtLeast(0)
    private val displayedBitmaps = mutableListOf<Bitmap>()
    private val isPdf = startFile.extension.equals("pdf", ignoreCase = true)

    init {
        orientation = VERTICAL
        setBackgroundColor(getColor(R.color.qp_surface))
        setPadding(dp(10), dp(8), dp(10), dp(10))

        val controls = LinearLayout(context).apply {
            orientation = HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, 0, 0, dp(8))
        }
        if (isPdf) {
            previousButton.setOnClickListener { showPdfPage(currentPage - 1) }
            nextButton.setOnClickListener { showPdfPage(currentPage + 1) }
            rotateButton.setOnClickListener {
                rotationDegrees = (rotationDegrees + 90) % 360
                renderCurrentPdfPage()
            }
            controls.addView(previousButton, LinearLayout.LayoutParams(dp(48), dp(42)))
            controls.addView(pageLabel, LinearLayout.LayoutParams(0, dp(42), 1f))
            controls.addView(rotateButton, LinearLayout.LayoutParams(dp(54), dp(42)))
            controls.addView(nextButton, LinearLayout.LayoutParams(dp(48), dp(42)))
        } else {
            imagePreviousButton.setOnClickListener { navigateImage(-1) }
            imageNextButton.setOnClickListener { navigateImage(1) }
            controls.addView(imagePreviousButton, LinearLayout.LayoutParams(dp(48), dp(42)))
            controls.addView(pageLabel, LinearLayout.LayoutParams(0, dp(42), 1f))
            controls.addView(imageNextButton, LinearLayout.LayoutParams(dp(48), dp(42)))
        }
        addView(controls, LinearLayout.LayoutParams(-1, dp(50)))

        stage.setBackgroundColor(Color.rgb(22, 25, 35))
        stage.addView(pageImage, FrameLayout.LayoutParams(-1, -1))
        stage.addView(
            feedback,
            FrameLayout.LayoutParams(-1, -2, Gravity.CENTER)
        )
        addView(stage, LinearLayout.LayoutParams(-1, 0, 1f))

        thumbnailScroller.addView(
            thumbnailRow,
            HorizontalScrollView.LayoutParams(-2, -1)
        )
        addView(thumbnailScroller, LinearLayout.LayoutParams(-1, dp(102)))

        val guidance = TextView(context).apply {
            text = if (isPdf) {
                "Pinch to zoom · Drag to move · Use page thumbnails to jump"
            } else {
                "Pinch to zoom · Drag to move · Swipe to change photos"
            }
            textSize = 10f
            gravity = Gravity.CENTER
            setTextColor(getColor(R.color.qp_muted))
            setPadding(0, dp(5), 0, 0)
        }
        addView(guidance, LinearLayout.LayoutParams(-1, dp(22)))

        if (isPdf) {
            thumbnailScroller.visibility = View.VISIBLE
            showLoading("Loading PDF…")
            loadPdfPageCount()
        } else {
            updateImageControls()
            showLoading("Opening image…")
            loadImage(currentFile)
        }
    }

    fun dispose() {
        if (!disposed.compareAndSet(false, true)) return
        renderVersion++
        worker.shutdownNow()
        clearDisplayedBitmaps()
        pageImage.setDocumentBitmap(null)
    }

    private fun loadPdfPageCount() {
        submitRender {
            val count = ParcelFileDescriptor.open(
                currentFile,
                ParcelFileDescriptor.MODE_READ_ONLY
            ).use { descriptor ->
                PdfRenderer(descriptor).use { renderer -> renderer.pageCount }
            }
            require(count > 0) { "This PDF does not contain any pages." }
            host.runOnUiThread {
                if (disposed.get()) return@runOnUiThread
                pageCount = count
                updatePdfControls()
                renderCurrentPdfPage()
            }
        }
    }

    private fun showPdfPage(index: Int) {
        if (!isPdf || pageCount <= 0) return
        val target = index.coerceIn(0, pageCount - 1)
        if (target == currentPage) return
        currentPage = target
        rotationDegrees = 0
        updatePdfControls()
        renderCurrentPdfPage()
    }

    private fun updatePdfControls() {
        pageLabel.text = "Page ${if (pageCount == 0) 1 else currentPage + 1} of ${max(1, pageCount)}"
        previousButton.isEnabled = currentPage > 0
        nextButton.isEnabled = pageCount > 0 && currentPage < pageCount - 1
        rotateButton.isEnabled = pageCount > 0
        previousButton.alpha = if (previousButton.isEnabled) 1f else 0.4f
        nextButton.alpha = if (nextButton.isEnabled) 1f else 0.4f
    }

    private fun renderCurrentPdfPage() {
        if (disposed.get() || pageCount <= 0) return
        val request = ++renderVersion
        val file = currentFile
        val requestedPage = currentPage
        val requestedRotation = rotationDegrees
        showLoading("Rendering page ${requestedPage + 1} of $pageCount…")
        submitRender {
            var frame: PdfFrame? = null
            try {
                frame = renderPdfFrame(file, requestedPage, requestedRotation)
                val result = frame
                host.runOnUiThread {
                    if (disposed.get() || request != renderVersion) {
                        recycleFrame(result)
                        return@runOnUiThread
                    }
                    clearDisplayedBitmaps()
                    pageCount = result.pageCount
                    currentPage = result.pageIndex
                    displayedBitmaps.add(result.pageBitmap)
                    pageImage.setDocumentBitmap(result.pageBitmap)
                    result.thumbnails.forEach { displayedBitmaps.add(it.bitmap) }
                    showFeedback(false)
                    updatePdfControls()
                    showThumbnails(result.thumbnails)
                }
            } catch (error: Exception) {
                frame?.let(::recycleFrame)
                showRenderError(request, error)
            }
        }
    }

    private fun renderPdfFrame(file: File, requestedPage: Int, rotation: Int): PdfFrame {
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
            PdfRenderer(descriptor).use { renderer ->
                require(renderer.pageCount > 0) { "This PDF does not contain any pages." }
                val pageIndex = requestedPage.coerceIn(0, renderer.pageCount - 1)
                val raw = renderPage(renderer, pageIndex, MAX_PAGE_EDGE)
                val mainBitmap = rotateBitmap(raw, rotation)
                if (mainBitmap !== raw) raw.recycle()

                val first = max(0, pageIndex - 2)
                val last = min(renderer.pageCount - 1, pageIndex + 2)
                val thumbnails = mutableListOf<PageThumbnail>()
                try {
                    for (index in first..last) {
                        val thumbnailRaw = renderPage(renderer, index, MAX_THUMB_EDGE)
                        val thumbnail = if (index == pageIndex) rotateBitmap(thumbnailRaw, rotation) else thumbnailRaw
                        if (thumbnail !== thumbnailRaw) thumbnailRaw.recycle()
                        thumbnails.add(PageThumbnail(index, thumbnail))
                    }
                    PdfFrame(renderer.pageCount, pageIndex, mainBitmap, thumbnails)
                } catch (error: Exception) {
                    mainBitmap.recycle()
                    thumbnails.forEach { if (!it.bitmap.isRecycled) it.bitmap.recycle() }
                    throw error
                }
            }
        }
    }

    private fun renderPage(renderer: PdfRenderer, pageIndex: Int, maxEdge: Int): Bitmap {
        renderer.openPage(pageIndex).use { page ->
            val scale = min(1f, maxEdge.toFloat() / max(page.width, page.height).toFloat())
            val width = max(1, (page.width * scale).toInt())
            val height = max(1, (page.height * scale).toInt())
            val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
            val transform = Matrix().apply {
                setScale(width.toFloat() / page.width, height.toFloat() / page.height)
            }
            try {
                page.render(bitmap, null, transform, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                return bitmap
            } catch (error: Exception) {
                bitmap.recycle()
                throw error
            }
        }
    }

    private fun rotateBitmap(bitmap: Bitmap, degrees: Int): Bitmap {
        val normalized = ((degrees % 360) + 360) % 360
        if (normalized == 0) return bitmap
        val matrix = Matrix().apply { postRotate(normalized.toFloat()) }
        return Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true)
    }

    private fun showThumbnails(thumbnails: List<PageThumbnail>) {
        thumbnailRow.removeAllViews()
        thumbnails.forEach { thumbnail ->
            val card = MaterialCardView(context).apply {
                radius = dp(THUMBNAIL_RADIUS_DP).toFloat()
                cardElevation = 0f
                strokeWidth = dp(if (thumbnail.pageIndex == currentPage) 2 else 1)
                strokeColor = getColor(
                    if (thumbnail.pageIndex == currentPage) R.color.qp_primary else R.color.qp_outline
                )
                setCardBackgroundColor(getColor(R.color.qp_card))
                contentDescription = "Go to page ${thumbnail.pageIndex + 1}"
                isClickable = true
                isFocusable = true
                setOnClickListener { showPdfPage(thumbnail.pageIndex) }
            }
            val column = LinearLayout(context).apply {
                orientation = VERTICAL
                gravity = Gravity.CENTER
                setPadding(dp(4), dp(4), dp(4), dp(4))
            }
            val image = ImageView(context).apply {
                setImageBitmap(thumbnail.bitmap)
                scaleType = ImageView.ScaleType.FIT_CENTER
                contentDescription = "Page ${thumbnail.pageIndex + 1} thumbnail"
            }
            column.addView(image, LinearLayout.LayoutParams(dp(58), dp(62)))
            column.addView(
                TextView(context).apply {
                    text = (thumbnail.pageIndex + 1).toString()
                    textSize = 9f
                    gravity = Gravity.CENTER
                    setTextColor(getColor(R.color.qp_text))
                },
                LinearLayout.LayoutParams(-1, dp(16))
            )
            card.addView(column)
            thumbnailRow.addView(
                card,
                LinearLayout.LayoutParams(dp(76), dp(88)).apply { marginEnd = dp(7) }
            )
        }
    }

    private fun loadImage(file: File) {
        val request = ++renderVersion
        showLoading("Opening image…")
        submitRender {
            var bitmap: Bitmap? = null
            try {
                bitmap = decodeImage(file)
                val result = bitmap
                host.runOnUiThread {
                    if (disposed.get() || request != renderVersion) {
                        if (!result.isRecycled) result.recycle()
                        return@runOnUiThread
                    }
                    clearDisplayedBitmaps()
                    displayedBitmaps.add(result)
                    pageImage.setDocumentBitmap(result)
                    pageImage.contentDescription = "Current document: ${file.name}"
                    showFeedback(false)
                    updateImageControls()
                }
            } catch (error: Exception) {
                bitmap?.let { if (!it.isRecycled) it.recycle() }
                showRenderError(request, error)
            }
        }
    }

    private fun decodeImage(file: File): Bitmap {
        require(file.isFile && file.length() > 0) { "This image is empty or unavailable." }
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.absolutePath, bounds)
        require(bounds.outWidth > 0 && bounds.outHeight > 0) { "This image could not be decoded." }
        var sample = 1
        while (max(bounds.outWidth / sample, bounds.outHeight / sample) > MAX_IMAGE_EDGE) {
            sample *= 2
        }
        return BitmapFactory.decodeFile(
            file.absolutePath,
            BitmapFactory.Options().apply {
                inSampleSize = sample
                inPreferredConfig = Bitmap.Config.ARGB_8888
            }
        ) ?: error("This image could not be decoded.")
    }

    private fun updateImageControls() {
        pageLabel.text = "Photo ${currentImageIndex + 1} of ${max(1, imageSiblings.size)}"
        imagePreviousButton.isEnabled = currentImageIndex > 0
        imageNextButton.isEnabled = currentImageIndex < imageSiblings.lastIndex
        imagePreviousButton.alpha = if (imagePreviousButton.isEnabled) 1f else 0.4f
        imageNextButton.alpha = if (imageNextButton.isEnabled) 1f else 0.4f
    }

    private fun navigateImage(direction: Int) {
        if (isPdf || disposed.get()) return
        val target = currentImageIndex + direction
        if (target !in imageSiblings.indices) return
        val next = imageSiblings[target]
        if (!next.isFile) return
        onNavigateImage(next)
    }

    private fun showLoading(message: String) {
        feedback.text = message
        feedback.visibility = View.VISIBLE
    }

    private fun showFeedback(visible: Boolean) {
        feedback.visibility = if (visible) View.VISIBLE else View.GONE
    }

    private fun showRenderError(request: Int, error: Exception) {
        host.runOnUiThread {
            if (disposed.get() || request != renderVersion) return@runOnUiThread
            clearDisplayedBitmaps()
            feedback.text = error.message?.takeIf { it.isNotBlank() } ?: "Could not open this document."
            feedback.visibility = View.VISIBLE
        }
    }

    private fun clearDisplayedBitmaps() {
        pageImage.setDocumentBitmap(null)
        thumbnailRow.removeAllViews()
        displayedBitmaps.forEach { if (!it.isRecycled) it.recycle() }
        displayedBitmaps.clear()
    }

    private fun recycleFrame(frame: PdfFrame) {
        if (!frame.pageBitmap.isRecycled) frame.pageBitmap.recycle()
        frame.thumbnails.forEach { if (!it.bitmap.isRecycled) it.bitmap.recycle() }
    }

    private fun submitRender(task: () -> Unit) {
        if (disposed.get()) return
        try {
            worker.execute {
                if (!disposed.get()) task()
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) {
            // The viewer was closed while a render was being queued.
        }
    }

    private fun actionButton(label: String, description: String): MaterialButton {
        return MaterialButton(context).apply {
            text = label
            contentDescription = description
            isAllCaps = false
            minWidth = dp(0)
            minHeight = dp(40)
            insetTop = 0
            insetBottom = 0
            cornerRadius = dp(11)
            textSize = 19f
            strokeWidth = dp(1)
            strokeColor = android.content.res.ColorStateList.valueOf(getColor(R.color.qp_outline))
            backgroundTintList = android.content.res.ColorStateList.valueOf(getColor(R.color.qp_card))
            setTextColor(getColor(R.color.qp_text))
        }
    }

    private fun sameFile(left: File, right: File): Boolean =
        runCatching { left.canonicalPath == right.canonicalPath }.getOrDefault(false)

    private fun getColor(resourceId: Int): Int = host.getColor(resourceId)
    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()
}

/**
 * Matrix-backed image surface with pinch zoom, drag-to-pan, double-tap reset,
 * and horizontal swipe callbacks when the image is at its fitted zoom.
 */
private class ZoomableDocumentImageView(
    context: Context,
    private val onSwipe: (Int) -> Unit
) : AppCompatImageView(context) {
    private val displayMatrix = Matrix()
    private var minimumScale = 1f
    private var currentScale = 1f
    private var lastX = 0f
    private var lastY = 0f

    private val scaleDetector = ScaleGestureDetector(
        context,
        object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(detector: ScaleGestureDetector): Boolean {
                val target = (currentScale * detector.scaleFactor)
                    .coerceIn(minimumScale, minimumScale * 5f)
                val factor = target / currentScale
                displayMatrix.postScale(factor, factor, detector.focusX, detector.focusY)
                currentScale = target
                imageMatrix = displayMatrix
                return true
            }
        }
    )

    private val gestureDetector = GestureDetector(
        context,
        object : GestureDetector.SimpleOnGestureListener() {
            override fun onDown(event: MotionEvent): Boolean = true

            override fun onDoubleTap(event: MotionEvent): Boolean {
                fitToView()
                return true
            }

            override fun onFling(
                first: MotionEvent?,
                second: MotionEvent,
                velocityX: Float,
                velocityY: Float
            ): Boolean {
                if (
                    first != null &&
                    currentScale <= minimumScale * 1.05f &&
                    abs(first.x - second.x) > dp(55) &&
                    abs(first.x - second.x) > abs(first.y - second.y) * 1.2f
                ) {
                    onSwipe(if (first.x > second.x) 1 else -1)
                    return true
                }
                return false
            }
        }
    )

    init {
        scaleType = ScaleType.MATRIX
        adjustViewBounds = false
        isFocusable = true
        contentDescription = "Document page"
    }

    fun setDocumentBitmap(bitmap: Bitmap?) {
        setImageBitmap(bitmap)
        if (bitmap != null) post { fitToView() } else {
            displayMatrix.reset()
            currentScale = 1f
            minimumScale = 1f
            imageMatrix = displayMatrix
        }
    }

    private fun fitToView() {
        val image = drawable ?: return
        if (width <= 0 || height <= 0 || image.intrinsicWidth <= 0 || image.intrinsicHeight <= 0) return
        val fit = min(
            width.toFloat() / image.intrinsicWidth.toFloat(),
            height.toFloat() / image.intrinsicHeight.toFloat()
        )
        minimumScale = fit.coerceAtLeast(0.01f)
        currentScale = minimumScale
        displayMatrix.reset()
        displayMatrix.postScale(minimumScale, minimumScale)
        val renderedWidth = image.intrinsicWidth * minimumScale
        val renderedHeight = image.intrinsicHeight * minimumScale
        displayMatrix.postTranslate(
            (width - renderedWidth) / 2f,
            (height - renderedHeight) / 2f
        )
        imageMatrix = displayMatrix
    }

    override fun onSizeChanged(width: Int, height: Int, oldWidth: Int, oldHeight: Int) {
        super.onSizeChanged(width, height, oldWidth, oldHeight)
        if (width != oldWidth || height != oldHeight) fitToView()
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        gestureDetector.onTouchEvent(event)
        scaleDetector.onTouchEvent(event)
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                lastX = event.x
                lastY = event.y
            }
            MotionEvent.ACTION_MOVE -> {
                if (event.pointerCount == 1 && !scaleDetector.isInProgress) {
                    val dx = event.x - lastX
                    val dy = event.y - lastY
                    displayMatrix.postTranslate(dx, dy)
                    imageMatrix = displayMatrix
                }
                lastX = event.x
                lastY = event.y
            }
            MotionEvent.ACTION_UP -> performClick()
            MotionEvent.ACTION_CANCEL -> Unit
        }
        return true
    }

    override fun performClick(): Boolean {
        super.performClick()
        return true
    }

    private fun dp(value: Int): Float = value * resources.displayMetrics.density
}
