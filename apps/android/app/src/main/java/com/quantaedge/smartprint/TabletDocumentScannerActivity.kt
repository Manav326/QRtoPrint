package com.quantaedge.smartprint

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.view.Gravity
import android.view.Surface
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.camera.core.Camera
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageCapture
import androidx.camera.core.ImageCaptureException
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import com.google.android.material.button.MaterialButton
import java.io.File
import java.io.FileOutputStream
import java.util.UUID

/**
 * Tablet-only camera workflow. The Google Play services scanner owns its UI and cannot be
 * resized by the host app; this viewfinder keeps the document preview full-screen and places
 * capture actions in a compact overlay. Captured pages are composed into the same PDF format
 * and handed back to MainActivity for storage in My Files.
 */
class TabletDocumentScannerActivity : AppCompatActivity() {
    companion object {
        const val EXTRA_PDF_PATH = "com.quantaedge.smartprint.extra.TABLET_SCAN_PDF_PATH"
        private const val MAX_PAGE_COUNT = ImagePdfComposer.MAX_PAGES
    }

    private lateinit var previewView: PreviewView
    private lateinit var pageCountView: TextView
    private lateinit var statusView: TextView
    private lateinit var captureButton: MaterialButton
    private lateinit var doneButton: MaterialButton
    private lateinit var undoButton: MaterialButton
    private lateinit var reviewButton: MaterialButton
    private lateinit var galleryButton: MaterialButton
    private lateinit var permissionLauncher: ActivityResultLauncher<String>
    private lateinit var galleryLauncher: ActivityResultLauncher<Array<String>>
    private lateinit var sessionDirectory: File

    private val pages = mutableListOf<File>()
    private var imageCapture: ImageCapture? = null
    private var cameraProvider: ProcessCameraProvider? = null
    private var camera: Camera? = null
    private var captureInProgress = false
    private var composing = false
    private var completed = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.statusBarColor = android.graphics.Color.TRANSPARENT
        window.navigationBarColor = android.graphics.Color.TRANSPARENT
        window.decorView.systemUiVisibility = (
            View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
            )

        permissionLauncher = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted) startCamera() else showCameraPermissionHelp()
        }
        galleryLauncher = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
            if (!uris.isNullOrEmpty()) importGalleryPages(uris)
        }

        sessionDirectory = File(cacheDir, "tablet-scans/" + UUID.randomUUID().toString())
        check(sessionDirectory.exists() || sessionDirectory.mkdirs()) {
            "Temporary scan storage could not be created."
        }
        buildScannerUi()

        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) ==
            PackageManager.PERMISSION_GRANTED) {
            startCamera()
        } else {
            permissionLauncher.launch(Manifest.permission.CAMERA)
        }
    }

    private fun buildScannerUi() {
        val root = FrameLayout(this).apply {
            setBackgroundColor(android.graphics.Color.BLACK)
            clipChildren = false
            clipToPadding = false
        }
        previewView = PreviewView(this).apply {
            implementationMode = PreviewView.ImplementationMode.COMPATIBLE
            scaleType = PreviewView.ScaleType.FILL_CENTER
            contentDescription = "Live document camera preview"
        }
        root.addView(previewView, FrameLayout.LayoutParams(-1, -1))
        root.addView(DocumentFrameGuide(this), FrameLayout.LayoutParams(-1, -1))

        val topBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(16), dp(8), dp(16), dp(8))
            background = roundedTopOverlay()
        }
        val close = TextView(this).apply {
            text = "×"
            textSize = 30f
            gravity = Gravity.CENTER
            setTextColor(android.graphics.Color.WHITE)
            contentDescription = "Cancel document scan"
            isClickable = true
            isFocusable = true
            setOnClickListener { setResult(Activity.RESULT_CANCELED); finish() }
        }
        topBar.addView(close, LinearLayout.LayoutParams(dp(48), dp(48)))
        topBar.addView(TextView(this).apply {
            text = "Scan document"
            textSize = 18f
            setTextColor(android.graphics.Color.WHITE)
            setTypeface(typeface, android.graphics.Typeface.BOLD)
            setPadding(dp(8), 0, 0, 0)
        }, LinearLayout.LayoutParams(0, -2, 1f))
        undoButton = controlButton("Undo", false).apply {
            isEnabled = false
            setOnClickListener { removeLastPage() }
        }
        topBar.addView(undoButton, LinearLayout.LayoutParams(dp(76), dp(42)))
        root.addView(topBar, FrameLayout.LayoutParams(-1, -2, Gravity.TOP))

        val bottomPanel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(12), dp(18), dp(12))
            background = roundedBottomPanel()
            elevation = dp(8).toFloat()
        }
        val summary = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        statusView = TextView(this).apply {
            text = "Align the page inside the frame"
            textSize = 12f
            setTextColor(android.graphics.Color.WHITE)
            maxLines = 1
            ellipsize = android.text.TextUtils.TruncateAt.END
        }
        summary.addView(statusView, LinearLayout.LayoutParams(0, -2, 1f))
        pageCountView = TextView(this).apply {
            text = "0 / " + MAX_PAGE_COUNT + " pages"
            textSize = 12f
            setTextColor(android.graphics.Color.WHITE)
        }
        reviewButton = controlButton("Review", false).apply {
            contentDescription = "Review captured pages"
            isEnabled = false
            setOnClickListener { showPageReview() }
        }
        summary.addView(reviewButton, LinearLayout.LayoutParams(dp(82), dp(40)).apply {
            marginEnd = dp(10)
        })
        summary.addView(pageCountView)
        bottomPanel.addView(summary, LinearLayout.LayoutParams(-1, -2).apply {
            bottomMargin = dp(10)
        })

        val actions = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        galleryButton = controlButton("Add photos", false).apply {
            setOnClickListener { galleryLauncher.launch(arrayOf("image/jpeg", "image/png")) }
        }
        actions.addView(galleryButton, LinearLayout.LayoutParams(0, dp(52), 1f))
        captureButton = controlButton("●", true).apply {
            textSize = 23f
            contentDescription = "Capture page"
            cornerRadius = dp(30)
            setOnClickListener { capturePage() }
        }
        actions.addView(captureButton, LinearLayout.LayoutParams(dp(76), dp(60)).apply {
            marginStart = dp(12)
            marginEnd = dp(12)
        })
        doneButton = controlButton("Done", true).apply {
            isEnabled = false
            setOnClickListener { finishScan() }
        }
        actions.addView(doneButton, LinearLayout.LayoutParams(0, dp(52), 1f))
        bottomPanel.addView(actions)
        root.addView(bottomPanel, FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM))

        ViewCompat.setOnApplyWindowInsetsListener(root) { _, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            topBar.setPadding(dp(16), bars.top + dp(8), dp(16), dp(8))
            bottomPanel.setPadding(dp(18), dp(12), dp(18), bars.bottom + dp(12))
            insets
        }
        setContentView(root)
        ViewCompat.requestApplyInsets(root)
        updatePageActions()
    }

    private fun startCamera() {
        val providerFuture = ProcessCameraProvider.getInstance(this)
        providerFuture.addListener({
            try {
                val provider = providerFuture.get()
                val preview = Preview.Builder().build().also {
                    it.setSurfaceProvider(previewView.surfaceProvider)
                }
                val capture = ImageCapture.Builder()
                    .setCaptureMode(ImageCapture.CAPTURE_MODE_MINIMIZE_LATENCY)
                    .setJpegQuality(94)
                    .setTargetRotation(previewView.display?.rotation ?: Surface.ROTATION_0)
                    .build()
                provider.unbindAll()
                camera = provider.bindToLifecycle(
                    this,
                    CameraSelector.DEFAULT_BACK_CAMERA,
                    preview,
                    capture
                )
                cameraProvider = provider
                imageCapture = capture
                statusView.text = "Keep the page flat and inside the frame"
                captureButton.isEnabled = true
            } catch (error: Exception) {
                statusView.text = "Camera could not be started"
                Toast.makeText(this, error.message ?: "Camera unavailable.", Toast.LENGTH_LONG).show()
            }
        }, ContextCompat.getMainExecutor(this))
    }

    private fun capturePage() {
        if (captureInProgress || composing) return
        if (pages.size >= MAX_PAGE_COUNT) {
            Toast.makeText(this, "A scan can contain up to " + MAX_PAGE_COUNT + " pages.", Toast.LENGTH_LONG).show()
            return
        }
        val capture = imageCapture
        if (capture == null) {
            Toast.makeText(this, "The camera is still starting.", Toast.LENGTH_SHORT).show()
            return
        }
        capture.targetRotation = previewView.display?.rotation ?: Surface.ROTATION_0
        val output = File(sessionDirectory, "page-" + (pages.size + 1) + "-" + UUID.randomUUID() + ".jpg")
        captureInProgress = true
        updatePageActions()
        capture.takePicture(
            ImageCapture.OutputFileOptions.Builder(output).build(),
            ContextCompat.getMainExecutor(this),
            object : ImageCapture.OnImageSavedCallback {
                override fun onImageSaved(result: ImageCapture.OutputFileResults) {
                    captureInProgress = false
                    if (output.isFile && output.length() > 0L) pages.add(output)
                    else output.delete()
                    statusView.text = "Page captured. Add another or finish the PDF."
                    updatePageActions()
                }

                override fun onError(error: ImageCaptureException) {
                    captureInProgress = false
                    output.delete()
                    statusView.text = "Capture failed. Try again."
                    updatePageActions()
                    Toast.makeText(
                        this@TabletDocumentScannerActivity,
                        error.message ?: "Could not capture this page.",
                        Toast.LENGTH_LONG
                    ).show()
                }
            }
        )
    }

    private fun importGalleryPages(uris: List<Uri>) {
        if (composing || captureInProgress) return
        val available = MAX_PAGE_COUNT - pages.size
        if (available <= 0) {
            Toast.makeText(this, "This scan already contains the maximum number of pages.", Toast.LENGTH_LONG).show()
            return
        }
        var imported = 0
        var failed = 0
        uris.take(available).forEach { uri ->
            try {
                val mime = contentResolver.getType(uri).orEmpty()
                require(mime == "image/jpeg" || mime == "image/png") {
                    "Choose JPG or PNG images."
                }
                val extension = if (mime == "image/png") "png" else "jpg"
                val destination = File(sessionDirectory, "gallery-" + UUID.randomUUID() + "." + extension)
                val currentBytes = pages.sumOf { it.length() }
                var copied = 0L
                val input = contentResolver.openInputStream(uri)
                    ?: error("The selected photo could not be opened.")
                input.use { source ->
                    FileOutputStream(destination).use { output ->
                        val buffer = ByteArray(8192)
                        while (true) {
                            val read = source.read(buffer)
                            if (read < 0) break
                            copied += read
                            require(currentBytes + copied <= ImagePdfComposer.MAX_SOURCE_BYTES) {
                                "The selected photos exceed the 100 MB scan limit."
                            }
                            output.write(buffer, 0, read)
                        }
                        output.fd.sync()
                    }
                }
                require(copied > 0L) { "The selected photo is empty." }
                pages.add(destination)
                imported++
            } catch (error: Exception) {
                failed++
            }
        }
        statusView.text = if (failed == 0) "Added " + imported + " photo page(s)"
            else "Added " + imported + "; " + failed + " photo(s) skipped"
        updatePageActions()
    }

    private fun removeLastPage() {
        if (pages.isEmpty() || composing || captureInProgress) return
        pages.removeAt(pages.lastIndex).delete()
        statusView.text = "Last page removed"
        updatePageActions()
    }

    private fun showPageReview() {
        if (pages.isEmpty() || composing || captureInProgress) return

        val pageList = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(6), dp(4), dp(6), dp(4))
        }
        val scroll = ScrollView(this).apply {
            isFillViewport = false
            addView(pageList, ViewGroup.LayoutParams(-1, -2))
            layoutParams = ViewGroup.LayoutParams(-1, dp(360))
        }
        val dialog = AlertDialog.Builder(this)
            .setTitle("Review captured pages")
            .setView(scroll)
            .setPositiveButton("Done", null)
            .create()

        fun renderPages() {
            pageList.removeAllViews()
            if (pages.isEmpty()) {
                pageList.addView(TextView(this).apply {
                    text = "No pages in this scan. Capture a page or add a photo to continue."
                    textSize = 14f
                    setTextColor(android.graphics.Color.WHITE)
                    setPadding(dp(12), dp(18), dp(12), dp(18))
                })
                return
            }

            pages.toList().forEachIndexed { index, page ->
                val row = LinearLayout(this).apply {
                    orientation = LinearLayout.HORIZONTAL
                    gravity = Gravity.CENTER_VERTICAL
                    setPadding(dp(8), dp(5), dp(8), dp(5))
                    background = GradientDrawable().apply {
                        setColor(0xFF252B33.toInt())
                        cornerRadius = dp(12).toFloat()
                    }
                }
                row.addView(TextView(this).apply {
                    text = "Page " + (index + 1)
                    textSize = 14f
                    setTextColor(android.graphics.Color.WHITE)
                    contentDescription = "Page " + (index + 1) + " of " + pages.size
                }, LinearLayout.LayoutParams(0, -2, 1f))

                row.addView(controlButton("Preview", false).apply {
                    minWidth = dp(68)
                    minHeight = dp(44)
                    contentDescription = "Preview page " + (index + 1)
                    setOnClickListener { showPagePreview(page, index + 1) }
                }, LinearLayout.LayoutParams(dp(76), dp(48)).apply {
                    marginStart = dp(4)
                })

                row.addView(controlButton("Up", false).apply {
                    minWidth = dp(40)
                    minHeight = dp(44)
                    contentDescription = "Move page " + (index + 1) + " earlier"
                    isEnabled = index > 0
                    setOnClickListener {
                        if (index in 1 until pages.size) {
                            val selected = pages.removeAt(index)
                            pages.add(index - 1, selected)
                            statusView.text = "Page order updated"
                            updatePageActions()
                            renderPages()
                        }
                    }
                }, LinearLayout.LayoutParams(dp(48), dp(48)).apply {
                    marginStart = dp(4)
                })

                row.addView(controlButton("Down", false).apply {
                    minWidth = dp(42)
                    minHeight = dp(44)
                    contentDescription = "Move page " + (index + 1) + " later"
                    isEnabled = index < pages.lastIndex
                    setOnClickListener {
                        if (index in 0 until pages.lastIndex) {
                            val selected = pages.removeAt(index)
                            pages.add(index + 1, selected)
                            statusView.text = "Page order updated"
                            updatePageActions()
                            renderPages()
                        }
                    }
                }, LinearLayout.LayoutParams(dp(54), dp(48)).apply {
                    marginStart = dp(4)
                })

                row.addView(controlButton("Remove", false).apply {
                    minWidth = dp(60)
                    minHeight = dp(44)
                    contentDescription = "Remove page " + (index + 1)
                    backgroundTintList = android.content.res.ColorStateList.valueOf(0xFF71383C.toInt())
                    setOnClickListener {
                        if (index in pages.indices) {
                            pages.removeAt(index).delete()
                            statusView.text = "Page removed"
                            updatePageActions()
                            renderPages()
                        }
                    }
                }, LinearLayout.LayoutParams(dp(70), dp(48)).apply {
                    marginStart = dp(4)
                })

                pageList.addView(row, LinearLayout.LayoutParams(-1, -2).apply {
                    bottomMargin = dp(6)
                })
            }
        }

        renderPages()
        dialog.show()
    }

    private fun showPagePreview(page: File, pageNumber: Int) {
        if (!page.isFile) return
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(page.absolutePath, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) {
            Toast.makeText(this, "This page preview is unavailable.", Toast.LENGTH_SHORT).show()
            return
        }

        var sampleSize = 1
        while (maxOf(bounds.outWidth / sampleSize, bounds.outHeight / sampleSize) > 1600) {
            sampleSize *= 2
        }
        val bitmap = BitmapFactory.decodeFile(
            page.absolutePath,
            BitmapFactory.Options().apply { inSampleSize = sampleSize }
        )
        if (bitmap == null) {
            Toast.makeText(this, "This page preview is unavailable.", Toast.LENGTH_SHORT).show()
            return
        }

        val preview = ImageView(this).apply {
            setImageBitmap(bitmap)
            adjustViewBounds = true
            maxHeight = dp(520)
            scaleType = ImageView.ScaleType.FIT_CENTER
            setPadding(dp(4), dp(6), dp(4), dp(6))
            contentDescription = "Preview of scanned page " + pageNumber
        }
        val dialog = AlertDialog.Builder(this)
            .setTitle("Page " + pageNumber + " preview")
            .setView(preview)
            .setPositiveButton("Close", null)
            .create()
        dialog.setOnDismissListener {
            preview.setImageDrawable(null)
            if (!bitmap.isRecycled) bitmap.recycle()
        }
        dialog.show()
    }

    private fun finishScan() {
        if (pages.isEmpty()) {
            Toast.makeText(this, "Capture or add at least one page first.", Toast.LENGTH_SHORT).show()
            return
        }
        if (composing || captureInProgress) return
        composing = true
        statusView.text = "Creating your PDF…"
        updatePageActions()
        val orderedPages = pages.toList()
        val output = File(sessionDirectory, "scanned-document.pdf")
        Thread {
            val result = runCatching {
                ImagePdfComposer.compose(orderedPages, output)
                output
            }
            runOnUiThread {
                if (isFinishing || isDestroyed) {
                    output.delete()
                    return@runOnUiThread
                }
                result.onSuccess { pdf ->
                    completed = true
                    setResult(RESULT_OK, Intent().putExtra(EXTRA_PDF_PATH, pdf.absolutePath))
                    finish()
                }.onFailure { error ->
                    composing = false
                    output.delete()
                    statusView.text = "PDF creation failed. You can retry."
                    updatePageActions()
                    Toast.makeText(
                        this,
                        error.message ?: "Could not create the scanned PDF.",
                        Toast.LENGTH_LONG
                    ).show()
                }
            }
        }.start()
    }

    private fun updatePageActions() {
        if (!::pageCountView.isInitialized) return
        pageCountView.text = pages.size.toString() + " / " + MAX_PAGE_COUNT + " pages"
        undoButton.isEnabled = pages.isNotEmpty() && !captureInProgress && !composing
        reviewButton.isEnabled = pages.isNotEmpty() && !captureInProgress && !composing
        galleryButton.isEnabled = !captureInProgress && !composing && pages.size < MAX_PAGE_COUNT
        captureButton.isEnabled = imageCapture != null && !captureInProgress &&
            !composing && pages.size < MAX_PAGE_COUNT
        doneButton.isEnabled = pages.isNotEmpty() && !captureInProgress && !composing
        doneButton.text = if (composing) "Creating…" else "Done"
    }

    private fun showCameraPermissionHelp() {
        AlertDialog.Builder(this)
            .setTitle("Camera permission needed")
            .setMessage("SmartPrint needs camera access to scan pages on this tablet. You can allow it in Android settings.")
            .setPositiveButton("Open settings") { _, _ ->
                startActivity(Intent(
                    Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                    Uri.parse("package:" + packageName)
                ))
                setResult(Activity.RESULT_CANCELED)
                finish()
            }
            .setNegativeButton("Cancel") { _, _ ->
                setResult(Activity.RESULT_CANCELED)
                finish()
            }
            .setOnCancelListener {
                setResult(Activity.RESULT_CANCELED)
                finish()
            }
            .show()
    }

    private fun controlButton(label: String, primary: Boolean): MaterialButton =
        MaterialButton(this).apply {
            text = label
            isAllCaps = false
            minHeight = dp(48)
            setTextColor(if (primary) android.graphics.Color.WHITE else android.graphics.Color.WHITE)
            backgroundTintList = android.content.res.ColorStateList.valueOf(
                if (primary) 0xFF1769AA.toInt() else 0xFF343A43.toInt()
            )
            cornerRadius = dp(16)
            insetTop = 0
            insetBottom = 0
        }

    private fun roundedTopOverlay(): GradientDrawable =
        GradientDrawable().apply {
            setColor(0xAA101318.toInt())
            cornerRadii = floatArrayOf(
                dp(18).toFloat(), dp(18).toFloat(),
                dp(18).toFloat(), dp(18).toFloat(),
                0f, 0f, 0f, 0f
            )
        }

    private fun roundedBottomPanel(): GradientDrawable =
        GradientDrawable().apply {
            setColor(0xEE101318.toInt())
            cornerRadii = floatArrayOf(
                dp(22).toFloat(), dp(22).toFloat(),
                dp(22).toFloat(), dp(22).toFloat(),
                0f, 0f, 0f, 0f
            )
        }

    private fun dp(value: Int): Int =
        (value * resources.displayMetrics.density).toInt()

    override fun onDestroy() {
        cameraProvider?.unbindAll()
        cameraProvider = null
        camera = null
        if (isFinishing && !completed) sessionDirectory.deleteRecursively()
        super.onDestroy()
    }

    private class DocumentFrameGuide(context: android.content.Context) : View(context) {
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = 0xD9FFFFFF.toInt()
            style = Paint.Style.STROKE
            strokeWidth = context.resources.displayMetrics.density * 2.5f
        }
        private val frame = RectF()

        override fun onDraw(canvas: Canvas) {
            super.onDraw(canvas)
            val insetX = width * 0.11f
            val insetTop = height * 0.12f
            val insetBottom = height * 0.20f
            frame.set(insetX, insetTop, width - insetX, height - insetBottom)
            canvas.drawRoundRect(frame, 14f, 14f, paint)
        }
    }
}
