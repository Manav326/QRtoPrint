package com.thinkwithsujeet.qrtoprint

import android.content.ClipData
import android.content.Intent
import android.database.Cursor
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.view.Gravity
import android.view.Menu
import android.content.res.ColorStateList
import android.view.View
import android.view.ViewGroup
import android.webkit.CookieManager
import android.webkit.SslErrorHandler
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.EditText
import android.widget.CheckBox
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.FileProvider
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.documentfile.provider.DocumentFile
import com.google.android.material.appbar.MaterialToolbar
import com.google.android.material.bottomnavigation.BottomNavigationView
import com.google.android.material.button.MaterialButton
import com.google.android.material.card.MaterialCardView
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.google.mlkit.vision.documentscanner.GmsDocumentScannerOptions
import com.google.mlkit.vision.documentscanner.GmsDocumentScanning
import com.google.mlkit.vision.documentscanner.GmsDocumentScanningResult
import java.io.ByteArrayInputStream
import java.io.File
import java.io.FileInputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class MainActivity : AppCompatActivity() {
    companion object {
        private const val HOME = 1
        private const val FILES = 2
        private const val SCAN_PRINT = 3
        private const val ACTIVITY = 4
        private const val PROFILE = 90
        private const val MAX_TREE_FILES = 500
        private const val MAX_TREE_DEPTH = 12
    }

    private lateinit var store: LocalDocumentStore
    private lateinit var toolbar: MaterialToolbar
    private lateinit var content: FrameLayout
    private lateinit var navigation: BottomNavigationView
    private var webView: WebView? = null
    private var folderPath = ""
    private var pickerPath = ""
    private var pickerMode = false
    private var portalUrl: String? = null
    private var portalHost: String? = null
    private var fileCallback: ValueCallback<Array<Uri>>? = null
    private var exportFile: File? = null
    private var exportFolder: File? = null
    private val selectedFilePaths = linkedSetOf<String>()

    private lateinit var importFiles: ActivityResultLauncher<Array<String>>
    private lateinit var importTree: ActivityResultLauncher<Uri?>
    private lateinit var exportOne: ActivityResultLauncher<String>
    private lateinit var exportTree: ActivityResultLauncher<Uri?>
    private lateinit var scannerLauncher: ActivityResultLauncher<Intent>
    private lateinit var documentScanLauncher: ActivityResultLauncher<IntentSenderRequest>

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        store = LocalDocumentStore(filesDir)
        registerFileActions()
        buildLayout()
        showHome()
        portalUrl = savedInstanceState?.getString("portalUrl")
        portalHost = savedInstanceState?.getString("portalHost")
        portalUrl?.let { ensureWebView().loadUrl(it); showPrint() }
        handleShareIntent(intent)
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putString("portalUrl", portalUrl)
        outState.putString("portalHost", portalHost)
        super.onSaveInstanceState(outState)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleShareIntent(intent)
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        when {
            pickerMode -> cancelPicker()
            navigation.selectedItemId == SCAN_PRINT && webView?.canGoBack() == true -> webView?.goBack()
            navigation.selectedItemId == SCAN_PRINT -> showScanPrint()
            navigation.selectedItemId != HOME -> showHome()
            folderPath.isNotEmpty() -> { folderPath = parent(folderPath); showFiles() }
            else -> super.onBackPressed()
        }
    }

    private fun registerFileActions() {
        scannerLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            if (result.resultCode == RESULT_OK) {
                val rawValue = result.data?.getStringExtra(QrScannerActivity.EXTRA_RAW_VALUE)
                try {
                    confirmShop(StationQrLink.parse(rawValue, allowLocalDevelopment = BuildConfig.DEBUG))
                } catch (_: Exception) {
                    toast("This is not a valid SmartPrint shop QR code.")
                }
            }
        }
        documentScanLauncher = registerForActivityResult(
            ActivityResultContracts.StartIntentSenderForResult()
        ) { result ->
            if (result.resultCode == RESULT_OK) {
                try {
                    val scan = GmsDocumentScanningResult.fromActivityResultIntent(result.data)
                        ?: error("The scan could not be read.")
                    saveDocumentScan(scan)
                } catch (e: Exception) {
                    toast("Could not save the scan: ${message(e)}")
                }
            }
        }
        importFiles = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
            if (uris.isNullOrEmpty()) return@registerForActivityResult
            var ok = 0
            var failed = 0
            uris.forEach { uri ->
                try { importUri(uri, if (pickerMode) pickerPath else folderPath); ok++ }
                catch (_: Exception) { failed++ }
            }
            reportImport(ok, failed)
            refreshLibrary()
        }
        importTree = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
            if (uri == null) return@registerForActivityResult
            try {
                try { contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
                catch (_: SecurityException) { }
                val tree = DocumentFile.fromTreeUri(this, uri) ?: error("Folder is unavailable.")
                require(tree.isDirectory) { "Select a folder." }
                val counts = intArrayOf(0, 0)
                val completed = importDirectory(
                    tree, if (pickerMode) pickerPath else folderPath, 0, counts,
                    FolderTransferBudget(MAX_TREE_FILES, MAX_TREE_DEPTH)
                )
                if (!completed) counts[1]++
                reportImport(counts[0], counts[1])
            } catch (e: Exception) { toast("Folder import failed: ${message(e)}") }
            refreshLibrary()
        }
        exportOne = registerForActivityResult(ActivityResultContracts.CreateDocument("*/*")) { uri ->
            val source = exportFile
            exportFile = null
            if (uri == null || source == null) return@registerForActivityResult
            try {
                val output = contentResolver.openOutputStream(uri, "w") ?: error("Destination unavailable.")
                output.use { out -> FileInputStream(source).use { it.copyTo(out) } }
                toast("Exported ${source.name}")
            } catch (e: Exception) { toast("Export failed: ${message(e)}") }
        }
        exportTree = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
            val source = exportFolder
            exportFolder = null
            if (uri == null || source == null) return@registerForActivityResult
            try {
                val destination = DocumentFile.fromTreeUri(this, uri) ?: error("Destination unavailable.")
                require(destination.isDirectory) { "Select a destination folder." }
                val count = intArrayOf(0)
                val complete = copyFolder(
                    source, destination, 0, count, FolderTransferBudget(MAX_TREE_FILES, MAX_TREE_DEPTH)
                )
                toast(
                    if (complete) "Exported ${count[0]} file(s)."
                    else "Exported ${count[0]} file(s); remaining items skipped at the safe folder-transfer limit."
                )
                if (source.parentFile?.canonicalPath == cacheDir.canonicalPath) source.deleteRecursively()
            } catch (e: Exception) { toast("Folder export failed: ${message(e)}") }
        }
    }

    private fun buildLayout() {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(getColor(R.color.qp_surface))
        }
        toolbar = MaterialToolbar(this).apply {
            title = "SmartPrint"
            setTitleTextColor(getColor(R.color.qp_text))
            setBackgroundColor(getColor(R.color.qp_surface))
            elevation = dp(1).toFloat()
            menu.add(Menu.NONE, PROFILE, Menu.NONE, "Account").apply {
                setIcon(android.R.drawable.ic_menu_myplaces)
                setShowAsAction(android.view.MenuItem.SHOW_AS_ACTION_ALWAYS)
            }
            setOnMenuItemClickListener { item ->
                if (item.itemId == PROFILE) { showProfile(); true } else false
            }
        }
        root.addView(toolbar, LinearLayout.LayoutParams(-1, dp(62)))
        content = FrameLayout(this)
        root.addView(content, LinearLayout.LayoutParams(-1, 0, 1f))
        navigation = BottomNavigationView(this).apply {
            menu.add(Menu.NONE, HOME, 0, "Home").setIcon(android.R.drawable.ic_menu_view)
            menu.add(Menu.NONE, FILES, 1, "My Files").setIcon(android.R.drawable.ic_menu_agenda)
            menu.add(Menu.NONE, SCAN_PRINT, 2, "Scan & Print").setIcon(android.R.drawable.ic_menu_camera)
            menu.add(Menu.NONE, ACTIVITY, 3, "Activity").setIcon(android.R.drawable.ic_menu_recent_history)
            labelVisibilityMode = BottomNavigationView.LABEL_VISIBILITY_LABELED
            itemHorizontalTranslationEnabled = false
            backgroundTintList = ColorStateList.valueOf(getColor(R.color.qp_card))
            itemIconTintList = ColorStateList(
                arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
                intArrayOf(getColor(R.color.qp_primary), getColor(R.color.qp_muted))
            )
            itemTextColor = itemIconTintList
            itemRippleColor = ColorStateList.valueOf(getColor(R.color.qp_soft))
            elevation = dp(12).toFloat()
            setOnItemSelectedListener { item ->
                when (item.itemId) {
                    HOME -> { showHome(); true }
                    FILES -> { showFiles(); true }
                    SCAN_PRINT -> { showScanPrint(); true }
                    ACTIVITY -> { showActivity(); true }
                    else -> false
                }
            }
        }
        root.addView(navigation, LinearLayout.LayoutParams(-1, dp(70)))
        ViewCompat.setOnApplyWindowInsetsListener(root) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }
        setContentView(root)
        ViewCompat.requestApplyInsets(root)
    }

    private fun resetToolbar(title: String) {
        toolbar.title = title
        toolbar.navigationIcon = null
        toolbar.setNavigationOnClickListener(null)
        navigation.visibility = View.VISIBLE
    }

    private fun showProfile() {
        MaterialAlertDialogBuilder(this)
            .setTitle("SmartPrint account")
            .setMessage(
                "You're using SmartPrint as a guest. No account is required to prepare documents or connect to a print shop. " +
                    "Your document library is stored privately on this device. Customer sign-in and account sync are not enabled yet."
            )
            .setPositiveButton("Continue as guest", null)
            .setNeutralButton("Sign in / Create account") { _, _ ->
                MaterialAlertDialogBuilder(this)
                    .setTitle("Customer accounts")
                    .setMessage("Customer sign-in and registration will be available when the dedicated customer-account service is enabled. Shop-owner credentials are not used for customer accounts.")
                    .setPositiveButton("Got it", null)
                    .show()
            }
            .show()
    }

    private fun showHome() {
        pickerMode = false
        resetToolbar("SmartPrint")
        navigation.menu.findItem(HOME).isChecked = true
        content.removeAllViews()
        content.addView(homeView(), frameParams())
    }

    private fun homeView(): View {
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(18), dp(20), dp(28))
        }
        scroll.addView(column)
        val brand = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        val brandMark = TextView(this).apply {
            text = "S"
            textSize = 25f
            gravity = Gravity.CENTER
            setTypeface(typeface, android.graphics.Typeface.BOLD)
            setTextColor(getColor(android.R.color.white))
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(getColor(R.color.qp_primary))
                cornerRadius = dp(15).toFloat()
            }
        }
        brand.addView(brandMark, LinearLayout.LayoutParams(dp(48), dp(48)))
        val wordmark = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(12), 0, 0, 0)
        }
        wordmark.addView(text("SmartPrint", 19, R.color.qp_text, true))
        wordmark.addView(text("by QuantaEdge", 12, R.color.qp_muted, false).apply { setPadding(0, dp(2), 0, 0) })
        brand.addView(wordmark)
        column.addView(brand, linearParams().apply { bottomMargin = dp(18) })

        val hero = card().apply {
            radius = dp(24).toFloat()
            setCardBackgroundColor(getColor(R.color.qp_hero))
            strokeWidth = 0
        }
        val heroContent = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(21), dp(22), dp(21), dp(22))
        }
        heroContent.addView(text("Your documents.\nReady when you are.", 27, R.color.qp_hero_text, true))
        heroContent.addView(text(
            "Scan, organise and prepare everything before you reach the print shop.",
            14, R.color.qp_hero_muted, false
        ).apply { setPadding(0, dp(10), 0, dp(18)) })
        val scanButton = button("Scan a document", true).apply { setOnClickListener { startDocumentScan() } }
        heroContent.addView(scanButton, linearParams())
        hero.addView(heroContent)
        column.addView(hero, linearParams())

        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("Import files", false).apply {
            setOnClickListener { importFiles.launch(arrayOf("*/*")); navigation.menu.findItem(FILES).isChecked = true }
        }, LinearLayout.LayoutParams(0, dp(50), 1f))
        actions.addView(button("My files", false).apply { setOnClickListener { showFiles() } },
            LinearLayout.LayoutParams(0, dp(50), 1f).apply { marginStart = dp(10) })
        column.addView(actions, linearParams().apply { topMargin = dp(12) })

        val journey = card()
        val journeyContent = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(17), dp(18), dp(17))
        }
        journeyContent.addView(text("From phone to print shop", 17, R.color.qp_text, true))
        journeyContent.addView(text(
            "Build your collection here. Scan the shop's SmartPrint QR code when you arrive, then review the quote and follow the shop's print progress.",
            13, R.color.qp_muted, false
        ).apply { setPadding(0, dp(6), 0, 0) })
        journey.addView(journeyContent)
        column.addView(journey, linearParams().apply { topMargin = dp(18) })
        column.addView(button("Scan & Print  →", true).apply { setOnClickListener { showScanPrint() } },
            linearParams().apply { topMargin = dp(12) })
        return scroll
    }

    private fun showScanPrint() {
        pickerMode = false
        if (portalUrl != null) { showPrint(); return }
        resetToolbar("Scan & Print")
        navigation.menu.findItem(SCAN_PRINT).isChecked = true
        content.removeAllViews()
        content.addView(scanPrintView(), frameParams())
    }

    private fun scanPrintView(): View {
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(18), dp(20), dp(28))
        }
        scroll.addView(column)
        val hero = card().apply {
            radius = dp(22).toFloat()
            setCardBackgroundColor(getColor(R.color.qp_hero))
            strokeWidth = 0
        }
        val body = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(22), dp(20), dp(20))
        }
        body.addView(text("Print nearby.\nWithout the back-and-forth.", 25, R.color.qp_hero_text, true))
        body.addView(text(
            "Prepare your files first. At a participating shop, scan the SmartPrint QR code displayed at the counter to connect securely.",
            14, R.color.qp_hero_muted, false
        ).apply { setPadding(0, dp(9), 0, dp(18)) })
        body.addView(button("Scan shop QR code", true).apply { setOnClickListener { scanQr() } }, linearParams())
        hero.addView(body)
        column.addView(hero, linearParams())
        column.addView(text("How it works", 18, R.color.qp_text, true).apply {
            setPadding(dp(2), dp(22), 0, dp(10))
        })
        val steps = listOf(
            Triple("01", "Prepare", "Scan paper pages or import PDFs and photos. Arrange your collection before you visit."),
            Triple("02", "Connect", "Scan the QR code shown by the print shop. SmartPrint opens that shop's secure print session."),
            Triple("03", "Review & collect", "Choose available print settings, review the server-calculated quote, pay and follow job progress.")
        )
        steps.forEach { (number, title, description) ->
            val step = card()
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(15), dp(15), dp(15), dp(15))
            }
            row.addView(text(number, 15, R.color.qp_primary, true), LinearLayout.LayoutParams(dp(42), -2))
            val copy = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            copy.addView(text(title, 15, R.color.qp_text, true))
            copy.addView(text(description, 13, R.color.qp_muted, false).apply { setPadding(0, dp(4), 0, 0) })
            row.addView(copy, LinearLayout.LayoutParams(0, -2, 1f))
            step.addView(row)
            column.addView(step, linearParams().apply { bottomMargin = dp(9) })
        }
        column.addView(text(
            "Your files stay on this phone until you choose to send them. Shop connection and printing require an internet connection.",
            12, R.color.qp_muted, false
        ).apply { setPadding(dp(3), dp(8), dp(3), 0) })
        column.addView(button("Prepare documents first", false).apply { setOnClickListener { showFiles() } },
            linearParams().apply { topMargin = dp(13) })
        return scroll
    }

    private fun showActivity() {
        pickerMode = false
        resetToolbar("Activity")
        navigation.menu.findItem(ACTIVITY).isChecked = true
        content.removeAllViews()
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(dp(28), dp(24), dp(28), dp(24))
        }
        column.addView(text("Your print journey starts here", 20, R.color.qp_text, true).apply {
            gravity = Gravity.CENTER
        })
        column.addView(text(
            if (portalUrl == null) "Connect to a participating print shop to begin a print session. Your live job status will be shown in the secure shop workflow."
            else "Your shop connection is ready. Open the current print session to review your quote, payment and job progress.",
            14, R.color.qp_muted, false
        ).apply { gravity = Gravity.CENTER; setPadding(0, dp(9), 0, dp(18)) })
        if (portalUrl != null) column.addView(button("Open print session", true).apply { setOnClickListener { showPrint() } })
        else column.addView(button("Scan & Print", true).apply { setOnClickListener { showScanPrint() } })
        content.addView(column, frameParams())
    }

    private fun showFiles() {
        pickerMode = false
        resetToolbar("My files")
        navigation.menu.findItem(FILES).isChecked = true
        content.removeAllViews()
        content.addView(libraryView(false), frameParams())
    }

    private fun showPicker() {
        pickerMode = true
        pickerPath = ""
        toolbar.title = "Choose a document"
        toolbar.setNavigationIcon(android.R.drawable.ic_media_previous)
        toolbar.setNavigationOnClickListener { cancelPicker() }
        navigation.visibility = View.GONE
        content.removeAllViews()
        content.addView(libraryView(true), frameParams())
    }

    private fun showPrint() {
        pickerMode = false
        toolbar.title = portalHost?.let { "SmartPrint · $it" } ?: "SmartPrint at a shop"
        toolbar.setNavigationIcon(android.R.drawable.ic_media_previous)
        toolbar.setNavigationOnClickListener { showFiles() }
        navigation.visibility = View.VISIBLE
        navigation.menu.findItem(SCAN_PRINT).isChecked = true
        content.removeAllViews()
        val view = ensureWebView()
        (view.parent as? ViewGroup)?.removeView(view)
        content.addView(view, frameParams())
    }

    private fun refreshLibrary() { if (pickerMode) showPicker() else showFiles() }

    private fun libraryView(isPicker: Boolean): View {
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(16), dp(18), dp(26))
        }
        scroll.addView(column)
        val hero = card()
        val heroText = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(18), dp(18), dp(18))
        }
        heroText.addView(text(
            if (isPicker) "Choose something ready to print" else "Keep everything ready",
            21, R.color.qp_text, true
        ))
        heroText.addView(text(
            if (isPicker) "Saved documents stay on this phone until you send one to the shop."
            else "Prepare documents before visiting the shop. Organise files in folders, then send them from this library.",
            14, R.color.qp_muted, false
        ).apply { setPadding(0, dp(7), 0, 0) })
        hero.addView(heroText)
        column.addView(hero, linearParams())

        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        val importButton = button("Import files", false)
        val folderButton = button("Import folder", false)
        actions.addView(importButton, LinearLayout.LayoutParams(0, dp(48), 1f))
        actions.addView(folderButton, LinearLayout.LayoutParams(0, dp(48), 1f).apply { marginStart = dp(8) })
        column.addView(actions, linearParams().apply { topMargin = dp(14) })
        importButton.setOnClickListener { importFiles.launch(arrayOf("*/*")) }
        folderButton.setOnClickListener { importTree.launch(null) }

        if (!isPicker) {
            val create = button("＋  New folder", true)
            column.addView(create, linearParams().apply { topMargin = dp(8) })
            create.setOnClickListener {
                nameDialog("Create folder", "") { name ->
                    try { store.createFolder(folderPath, name); showFiles() }
                    catch (e: Exception) { toast("Could not create folder: ${message(e)}") }
                }
            }
        }

        val path = if (isPicker) pickerPath else folderPath
        val crumb = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        crumb.addView(text(
            "My files" + if (path.isBlank()) "" else "  /  " + path.replace("/", "  /  "),
            13, R.color.qp_muted, true
        ), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        if (path.isNotEmpty()) {
            val up = button("Up", false)
            crumb.addView(up, LinearLayout.LayoutParams(-2, dp(40)))
            up.setOnClickListener {
                if (isPicker) pickerPath = parent(pickerPath) else folderPath = parent(folderPath)
                refreshLibrary()
            }
        }
        column.addView(crumb, linearParams().apply { topMargin = dp(20); bottomMargin = dp(9) })

        val folder = try { store.folder(path) } catch (_: Exception) { store.root }
        val entries = store.list(folder)
        val selectedHere = entries.filter { !it.isDirectory && selectedFilePaths.contains(it.canonicalPath) }
        if (!isPicker && selectedHere.isNotEmpty()) {
            val selectionCard = card()
            val selectionContent = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(14), dp(12), dp(14), dp(12))
            }
            selectionContent.addView(text("${selectedHere.size} selected", 14, R.color.qp_text, true))
            val selectionActions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            selectionActions.addView(button("Export", false).apply {
                setOnClickListener { exportSelected(selectedHere) }
            }, LinearLayout.LayoutParams(0, dp(44), 1f))
            val allPhotos = selectedHere.all { it.extension.lowercase(Locale.ROOT) in setOf("jpg", "jpeg", "png") }
            selectionActions.addView(button("Make PDF", true).apply {
                isEnabled = allPhotos
                alpha = if (allPhotos) 1f else 0.45f
                setOnClickListener { makePdfFromSelected(selectedHere) }
            }, LinearLayout.LayoutParams(0, dp(44), 1f).apply { marginStart = dp(8) })
            selectionContent.addView(selectionActions, linearParams().apply { topMargin = dp(8) })
            selectionContent.addView(text(
                if (allPhotos) "Photos are combined in the order shown. Original images remain unchanged."
                else "PDF creation is available when only JPG, JPEG or PNG photos are selected.",
                11, R.color.qp_muted, false
            ).apply { setPadding(0, dp(7), 0, 0) })
            selectionCard.addView(selectionContent)
            column.addView(selectionCard, linearParams().apply { bottomMargin = dp(12) })
        }
        if (entries.isEmpty()) {
            val empty = card()
            empty.addView(text(
                if (isPicker) "No printable files here. Import a document or open another folder."
                else "No documents here yet. Scan paper pages, import files or create a folder to get started.",
                14, R.color.qp_muted, false
            ).apply { setPadding(dp(18), dp(22), dp(18), dp(22) })
            column.addView(empty, linearParams())
        } else {
            entries.forEach { entry ->
                if (!isPicker || entry.isDirectory || DocumentNamePolicy.isPrintable(entry.name, entry.length())) {
                    column.addView(entryView(entry, isPicker), linearParams().apply { bottomMargin = dp(8) })
                }
            }
        }
        return scroll
    }

    private fun entryView(file: File, isPicker: Boolean): View {
        val outer = card()
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(12), dp(10), dp(8), dp(10))
        }
        row.addView(text(
            if (file.isDirectory) "▰" else file.extension.uppercase(Locale.ROOT).ifBlank { "FILE" },
            13, R.color.qp_primary, true
        ).apply { gravity = Gravity.CENTER }, LinearLayout.LayoutParams(dp(44), dp(44)))
        val details = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        details.addView(text(file.name, 14, R.color.qp_text, true).apply { maxLines = 2 })
        val info = if (file.isDirectory) "Folder" else
            "${android.text.format.Formatter.formatShortFileSize(this@MainActivity, file.length())}" +
                if (DocumentNamePolicy.isPrintable(file.name, file.length())) " · Ready to print" else " · Stored locally"
        details.addView(text(info, 12, R.color.qp_muted, false).apply { setPadding(0, dp(3), 0, 0) })
        row.addView(details, LinearLayout.LayoutParams(0, -2, 1f).apply { marginStart = dp(12) })

        if (isPicker && !file.isDirectory) {
            row.addView(button("Select", true).apply {
                minWidth = dp(72)
                setOnClickListener { chooseFile(file) }
            })
        } else if (!isPicker && !file.isDirectory) {
            val selected = CheckBox(this).apply {
                isChecked = selectedFilePaths.contains(file.canonicalPath)
                contentDescription = "Select ${file.name}"
                setOnCheckedChangeListener { _, checked ->
                    if (checked) selectedFilePaths.add(file.canonicalPath)
                    else selectedFilePaths.remove(file.canonicalPath)
                    refreshLibrary()
                }
            }
            row.addView(selected, LinearLayout.LayoutParams(dp(42), dp(44)))
            val more = text("⋮", 24, R.color.qp_muted, false).apply { gravity = Gravity.CENTER }
            row.addView(more, LinearLayout.LayoutParams(dp(36), dp(44)))
            more.setOnClickListener { showEntryMenu(more, file) }
        } else if (!isPicker) {
            val more = text("⋮", 24, R.color.qp_muted, false).apply { gravity = Gravity.CENTER }
            row.addView(more, LinearLayout.LayoutParams(dp(42), dp(44)))
            more.setOnClickListener { showEntryMenu(more, file) }
        }
        outer.addView(row)
        outer.setOnClickListener {
            if (file.isDirectory) {
                if (isPicker) pickerPath = store.pathOf(file) else folderPath = store.pathOf(file)
                refreshLibrary()
            } else if (isPicker) chooseFile(file) else openDocument(file)
        }
        return outer
    }

    private fun showEntryMenu(anchor: View, file: File) {
        val menu = PopupMenu(this, anchor)
        if (!file.isDirectory) menu.menu.add("Open")
        menu.menu.add("Rename")
        menu.menu.add(if (file.isDirectory) "Export folder" else "Export")
        menu.menu.add("Move to folder")
        val siblingItems = file.parentFile?.let { store.list(it) }.orEmpty()
        val position = siblingItems.indexOfFirst { it.canonicalPath == file.canonicalPath }
        if (position > 0) menu.menu.add("Move earlier")
        if (position >= 0 && position < siblingItems.lastIndex) menu.menu.add("Move later")
        if (!file.isDirectory) menu.menu.add("Share")
        menu.menu.add("Delete")
        menu.setOnMenuItemClickListener { item ->
            when (item.title.toString()) {
                "Open" -> openDocument(file)
                "Rename" -> nameDialog("Rename", file.name) { name ->
                    try { store.rename(file, name); selectedFilePaths.remove(file.canonicalPath); showFiles() }
                    catch (e: Exception) { toast("Rename failed: ${message(e)}") }
                }
                "Export" -> { exportFile = file; exportOne.launch(file.name) }
                "Export folder" -> { exportFolder = file; exportTree.launch(null) }
                "Move to folder" -> nameDialog("Destination folder path", file.parentFile?.let { store.pathOf(it) }.orEmpty()) { path ->
                    try { store.move(file, path); selectedFilePaths.remove(file.canonicalPath); showFiles() }
                    catch (e: Exception) { toast("Move failed: ${message(e)}") }
                }
                "Move earlier" -> { try { store.reorder(file, -1); showFiles() } catch (e: Exception) { toast("Reorder failed: ${message(e)}") } }
                "Move later" -> { try { store.reorder(file, 1); showFiles() } catch (e: Exception) { toast("Reorder failed: ${message(e)}") } }
                "Share" -> shareDocument(file)
                "Delete" -> MaterialAlertDialogBuilder(this)
                    .setTitle("Delete ${if (file.isDirectory) "folder" else "document"}?")
                    .setMessage("This removes the saved copy from this phone only.")
                    .setNegativeButton("Cancel", null)
                    .setPositiveButton("Delete") { _, _ ->
                        try { store.delete(file); showFiles() }
                        catch (e: Exception) { toast("Delete failed: ${message(e)}") }
                    }.show()
            }
            true
        }
        menu.show()
    }

    private fun exportSelected(files: List<File>) {
        if (files.isEmpty()) return
        try {
            val staging = File(cacheDir, "SmartPrint export ${System.currentTimeMillis()}")
            check(staging.mkdirs()) { "Temporary export folder could not be created." }
            files.forEach { source ->
                val target = File(staging, source.name)
                check(!target.exists()) { "Duplicate selected filenames cannot be exported together." }
                FileInputStream(source).use { input -> target.outputStream().use { output -> input.copyTo(output) } }
            }
            selectedFilePaths.clear()
            exportFolder = staging
            exportTree.launch(null)
        } catch (e: Exception) {
            toast("Could not prepare selected files: ${message(e)}")
        }
    }

    private fun makePdfFromSelected(files: List<File>) {
        val pdf = File(cacheDir, "SmartPrint photo collection ${System.currentTimeMillis()}.pdf")
        try {
            val pages = ImagePdfComposer.compose(files, pdf)
            FileInputStream(pdf).use { input -> store.importFile(input, "Photo collection.pdf", folderPath) }
            selectedFilePaths.clear()
            showFiles()
            toast("Created a $pages-page PDF. Original photos are unchanged.")
        } catch (e: Exception) {
            toast("PDF creation failed: ${message(e)}")
        } finally {
            pdf.delete()
        }
    }

    private fun nameDialog(title: String, initial: String, onSave: (String) -> Unit) {
        val input = EditText(this).apply {
            setSingleLine(true)
            setText(initial)
            setSelection(text.length)
            hint = "Name"
        }
        MaterialAlertDialogBuilder(this).setTitle(title).setView(input)
            .setNegativeButton("Cancel", null)
            .setPositiveButton("Save") { _, _ -> onSave(input.text.toString()) }
            .show()
    }

    private fun ensureWebView(): WebView {
        webView?.let { return it }
        val view = WebView(this)
        view.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            allowContentAccess = true
            allowFileAccess = false
            javaScriptCanOpenWindowsAutomatically = false
            setSupportMultipleWindows(false)
            mixedContentMode = WebSettings.MIXED_CONTENT_COMPATIBILITY_MODE
            safeBrowsingEnabled = true
        }
        CookieManager.getInstance().setAcceptCookie(true)
        view.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                val uri = request.url
                return when (uri.scheme?.lowercase(Locale.ROOT)) {
                    "https" -> if (uri.host.equals(portalHost, true)) false else { launchExternal(uri); true }
                    "upi", "tel", "mailto" -> { launchExternal(uri); true }
                    "intent" -> { launchIntent(uri.toString()); true }
                    "http" -> !(allowedAgentUpload(uri) || allowedLocalDevelopmentEndpoint(uri))
                    "about" -> false
                    else -> true
                }
            }
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                val uri = request.url
                if (uri.scheme.equals("http", true) &&
                    !allowedAgentUpload(uri) &&
                    !allowedLocalDevelopmentEndpoint(uri)) {
                    return WebResourceResponse(
                        "text/plain", "UTF-8", 403, "Blocked by SmartPrint", emptyMap(),
                        ByteArrayInputStream("Only the selected private-LAN development portal and paired-Agent upload endpoints are allowed over HTTP.".toByteArray())
                    )
                }
                return super.shouldInterceptRequest(view, request)
            }
            override fun onReceivedSslError(view: WebView, handler: SslErrorHandler, error: android.net.http.SslError) {
                handler.cancel()
                toast("The shop connection could not be verified securely.")
            }
        }
        view.webChromeClient = object : WebChromeClient() {
            override fun onShowFileChooser(
                webView: WebView,
                callback: ValueCallback<Array<Uri>>,
                params: FileChooserParams
            ): Boolean {
                fileCallback?.onReceiveValue(null)
                fileCallback = callback
                showPicker()
                return true
            }
        }
        webView = view
        return view
    }

    private fun allowedAgentUpload(uri: Uri): Boolean =
        LocalAgentUploadPolicy.allows(uri.scheme.orEmpty(), uri.host.orEmpty(), uri.port, uri.path.orEmpty())

    private fun allowedLocalDevelopmentEndpoint(uri: Uri): Boolean =
        BuildConfig.DEBUG &&
            uri.scheme.equals("http", true) &&
            uri.port in setOf(3000, 8080) &&
            uri.host.equals(portalHost, true) &&
            LocalAgentUploadPolicy.isPrivateIpv4Host(uri.host.orEmpty())

    private fun scanQr() {
        navigation.menu.findItem(SCAN_PRINT).isChecked = true
        scannerLauncher.launch(Intent(this, QrScannerActivity::class.java))
    }

    private fun startDocumentScan() {
        val options = GmsDocumentScannerOptions.Builder()
            .setGalleryImportAllowed(true)
            .setPageLimit(50)
            .setResultFormats(
                GmsDocumentScannerOptions.RESULT_FORMAT_PDF,
                GmsDocumentScannerOptions.RESULT_FORMAT_JPEG
            )
            .setScannerMode(GmsDocumentScannerOptions.SCANNER_MODE_FULL)
            .build()
        GmsDocumentScanning.getClient(options)
            .getStartScanIntent(this)
            .addOnSuccessListener { sender ->
                documentScanLauncher.launch(IntentSenderRequest.Builder(sender).build())
            }
            .addOnFailureListener { error ->
                toast("Document scanner is unavailable on this device. You can still import photos or PDFs. ${message(error)}")
            }
    }

    private fun saveDocumentScan(scan: GmsDocumentScanningResult) {
        val label = SimpleDateFormat("yyyy-MM-dd HHmm", Locale.ROOT).format(Date())
        val folder = store.createFolder("", "Scan $label", allowExisting = false)
        val targetPath = store.pathOf(folder)
        var saved = 0
        scan.getPdf()?.let { pdf ->
            val input = contentResolver.openInputStream(pdf.getUri()) ?: error("Scanned PDF is unavailable.")
            input.use { store.importFile(it, "Scanned document.pdf", targetPath) }
            saved++
        }
        scan.getPages().orEmpty().forEachIndexed { index, page ->
            val input = contentResolver.openInputStream(page.getImageUri()) ?: error("Scanned page ${index + 1} is unavailable.")
            input.use { store.importFile(it, "Page ${(index + 1).toString().padStart(2, '0')}.jpg", targetPath) }
            saved++
        }
        require(saved > 0) { "The scanner returned no pages." }
        folderPath = targetPath
        showFiles()
        toast("Saved your scan in My files.")
    }

    private fun confirmShop(link: StationQrLink) {
        MaterialAlertDialogBuilder(this)
            .setTitle("Connect to this print shop?")
            .setMessage(
                "Open the SmartPrint customer portal at:\n\n${link.host}\n\n" +
                    (if (link.url.startsWith("http://")) "LOCAL DEVELOPMENT ONLY — use a trusted private Wi-Fi network.\n\n" else "") +
                    "The shop's existing quote, payment and print-status workflow will be used."
            )
            .setNegativeButton("Cancel", null)
            .setPositiveButton("Continue") { _, _ ->
                portalHost = link.host
                portalUrl = link.url
                ensureWebView().loadUrl(link.url)
                showPrint()
            }.show()
    }

    private fun chooseFile(file: File) {
        if (!DocumentNamePolicy.isPrintable(file.name, file.length())) {
            toast("Printing accepts PDF, JPG and PNG files up to 20 MB.")
            return
        }
        val callback = fileCallback
        if (callback == null) { toast("Open a document field in the print page first."); return }
        try {
            val uri = FileProvider.getUriForFile(this, "$packageName.documents", file)
            fileCallback = null
            showPrint()
            callback.onReceiveValue(arrayOf(uri))
        } catch (e: Exception) {
            cancelPicker()
            toast("Could not attach the document: ${message(e)}")
        }
    }

    private fun cancelPicker() {
        fileCallback?.onReceiveValue(null)
        fileCallback = null
        showPrint()
    }

    private fun importUri(uri: Uri, destination: String) {
        val name = queryName(uri)
        val input = contentResolver.openInputStream(uri) ?: error("Selected file cannot be opened.")
        input.use { store.importFile(it, name, destination) }
    }

    private fun queryName(uri: Uri): String {
        try {
            contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null).use { cursor: Cursor? ->
                if (cursor != null && cursor.moveToFirst()) {
                    val i = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    if (i >= 0) cursor.getString(i)?.takeIf { it.isNotBlank() }?.let { return it }
                }
            }
        } catch (_: Exception) { }
        return uri.lastPathSegment?.substringAfterLast('/')?.ifBlank { null } ?: "Imported document"
    }

    private fun importDirectory(
        source: DocumentFile,
        destinationPath: String,
        depth: Int,
        counts: IntArray,
        budget: FolderTransferBudget
    ): Boolean {
        for (child in source.listFiles()) {
            // Count folders as well as files so empty-directory trees cannot bypass the cap.
            if (!budget.tryTake(depth)) return false
            try {
                val name = child.name ?: "Imported item"
                if (child.isDirectory) {
                    if (depth >= MAX_TREE_DEPTH) return false
                    val folder = store.createFolder(destinationPath, name, allowExisting = true)
                    if (!importDirectory(child, store.pathOf(folder), depth + 1, counts, budget)) return false
                } else if (child.isFile) {
                    val input = contentResolver.openInputStream(child.uri) ?: error("File cannot be opened.")
                    input.use { store.importFile(it, name, destinationPath) }
                    counts[0]++
                }
            } catch (_: Exception) {
                counts[1]++
            }
        }
        return true
    }

    private fun copyFolder(
        source: File,
        targetParent: DocumentFile,
        depth: Int,
        count: IntArray,
        budget: FolderTransferBudget
    ): Boolean {
        if (depth > MAX_TREE_DEPTH) return false
        val target = targetParent.findFile(source.name)?.takeIf { it.isDirectory }
            ?: targetParent.createDirectory(source.name) ?: error("Could not create ${source.name}.")
        for (child in store.list(source)) {
            // Count folders as well as files so empty-folder trees stay bounded too.
            if (!budget.tryTake(depth)) return false
            if (child.isDirectory) {
                if (!copyFolder(child, target, depth + 1, count, budget)) return false
            } else {
                val outputFile = target.createFile(DocumentNamePolicy.mimeType(child.name), uniqueName(target, child.name))
                    ?: error("Could not create ${child.name}.")
                val output = contentResolver.openOutputStream(outputFile.uri, "w") ?: error("Cannot write ${child.name}.")
                output.use { out -> FileInputStream(child).use { it.copyTo(out) } }
                count[0]++
            }
        }
        return true
    }

    private fun uniqueName(folder: DocumentFile, name: String): String {
        if (folder.findFile(name) == null) return name
        val extAt = name.lastIndexOf('.')
        val base = if (extAt > 0) name.substring(0, extAt) else name
        val ext = if (extAt > 0) name.substring(extAt) else ""
        for (n in 2 until 10_000) {
            val candidate = "$base ($n)$ext"
            if (folder.findFile(candidate) == null) return candidate
        }
        return "$base-${System.currentTimeMillis()}$ext"
    }

    private fun openDocument(file: File) {
        try {
            val uri = FileProvider.getUriForFile(this, "$packageName.documents", file)
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, DocumentNamePolicy.mimeType(file.name))
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            startActivity(Intent.createChooser(intent, "Open document"))
        } catch (_: Exception) { toast("No app could open this document.") }
    }

    private fun shareDocument(file: File) {
        try {
            val uri = FileProvider.getUriForFile(this, "$packageName.documents", file)
            val intent = Intent(Intent.ACTION_SEND).apply {
                type = DocumentNamePolicy.mimeType(file.name)
                putExtra(Intent.EXTRA_STREAM, uri)
                clipData = ClipData.newUri(contentResolver, file.name, uri)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            startActivity(Intent.createChooser(intent, "Share document"))
        } catch (_: Exception) { toast("Could not share this document.") }
    }

    private fun handleShareIntent(intent: Intent?) {
        if (intent == null) return
        val uris = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))
            Intent.ACTION_SEND_MULTIPLE -> {
                @Suppress("DEPRECATION")
                intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.toList().orEmpty()
            }
            else -> emptyList()
        }
        if (uris.isEmpty()) return
        var ok = 0
        var failed = 0
        uris.forEach { uri ->
            try { importUri(uri, folderPath); ok++ } catch (_: Exception) { failed++ }
        }
        reportImport(ok, failed)
        showFiles()
    }

    private fun launchExternal(uri: Uri) {
        try { startActivity(Intent(Intent.ACTION_VIEW, uri)) }
        catch (_: Exception) { toast("No app is available to open this link.") }
    }

    private fun launchIntent(raw: String) {
        try { startActivity(Intent.parseUri(raw, Intent.URI_INTENT_SCHEME).apply { component = null }) }
        catch (_: Exception) { toast("No app is available to open this payment link.") }
    }

    private fun reportImport(ok: Int, failed: Int) {
        when {
            ok == 0 && failed > 0 -> toast("No files imported. Check file access and free storage.")
            failed > 0 -> toast("Imported $ok file(s); $failed could not be imported.")
            ok > 0 -> toast("Imported $ok file(s).")
        }
    }

    private fun parent(path: String) = path.substringBeforeLast('/', "").trim('/')
    private fun card() = MaterialCardView(this).apply {
        radius = dp(14).toFloat()
        cardElevation = 0f
        strokeWidth = dp(1)
        strokeColor = getColor(R.color.qp_outline)
        setCardBackgroundColor(getColor(R.color.qp_card))
    }
    private fun text(value: String, size: Int, color: Int, bold: Boolean) = TextView(this).apply {
        text = value
        textSize = size.toFloat()
        setTextColor(getColor(color))
        if (bold) setTypeface(typeface, android.graphics.Typeface.BOLD)
    }
    private fun button(value: String, primary: Boolean) = MaterialButton(this).apply {
        text = value
        isAllCaps = false
        cornerRadius = dp(12)
        if (primary) {
            backgroundTintList = android.content.res.ColorStateList.valueOf(getColor(R.color.qp_primary))
            setTextColor(getColor(android.R.color.white))
        } else {
            strokeWidth = dp(1)
            strokeColor = android.content.res.ColorStateList.valueOf(getColor(R.color.qp_outline))
            backgroundTintList = android.content.res.ColorStateList.valueOf(getColor(R.color.qp_card))
            setTextColor(getColor(R.color.qp_text))
        }
    }
    private fun linearParams() = LinearLayout.LayoutParams(-1, -2)
    private fun frameParams() = FrameLayout.LayoutParams(-1, -1)
    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()
    private fun toast(value: String) = Toast.makeText(this, value, Toast.LENGTH_LONG).show()
    private fun message(e: Exception) = e.message?.takeIf { it.isNotBlank() } ?: "Unexpected error"
}
