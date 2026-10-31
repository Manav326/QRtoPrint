package in.thinkwithsujeet.qrtoprint

import android.content.ClipData
import android.content.Intent
import android.database.Cursor
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.view.Gravity
import android.view.Menu
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
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.ActivityResultLauncher
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
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import java.io.ByteArrayInputStream
import java.io.File
import java.io.FileInputStream
import java.util.Locale

class MainActivity : AppCompatActivity() {
    companion object {
        private const val FILES = 1
        private const val SCAN = 2
        private const val PRINT = 3
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

    private lateinit var importFiles: ActivityResultLauncher<Array<String>>
    private lateinit var importTree: ActivityResultLauncher<Uri?>
    private lateinit var exportOne: ActivityResultLauncher<String>
    private lateinit var exportTree: ActivityResultLauncher<Uri?>

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        store = LocalDocumentStore(filesDir)
        registerFileActions()
        buildLayout()
        showFiles()
        portalUrl = savedInstanceState?.getString("portalUrl")
        portalHost = savedInstanceState?.getString("portalHost")
        portalUrl?.let { ensureWebView().loadUrl(it) }
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
            navigation.selectedItemId == PRINT && webView?.canGoBack() == true -> webView?.goBack()
            navigation.selectedItemId == PRINT -> showFiles()
            folderPath.isNotEmpty() -> { folderPath = parent(folderPath); showFiles() }
            else -> super.onBackPressed()
        }
    }

    private fun registerFileActions() {
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
                importDirectory(tree, if (pickerMode) pickerPath else folderPath, 0, counts)
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
                copyFolder(source, destination, 0, count)
                toast("Exported ${count[0]} file(s).")
            } catch (e: Exception) { toast("Folder export failed: ${message(e)}") }
        }
    }

    private fun buildLayout() {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(getColor(R.color.qp_surface))
        }
        toolbar = MaterialToolbar(this).apply {
            title = "My documents"
            setTitleTextColor(getColor(R.color.qp_text))
            setBackgroundColor(getColor(R.color.qp_surface))
        }
        root.addView(toolbar, LinearLayout.LayoutParams(-1, dp(60)))
        content = FrameLayout(this)
        root.addView(content, LinearLayout.LayoutParams(-1, 0, 1f))
        navigation = BottomNavigationView(this).apply {
            menu.add(Menu.NONE, FILES, 0, "Files").setIcon(android.R.drawable.ic_menu_agenda)
            menu.add(Menu.NONE, SCAN, 1, "Scan QR").setIcon(android.R.drawable.ic_menu_camera)
            menu.add(Menu.NONE, PRINT, 2, "Print").setIcon(android.R.drawable.ic_menu_view)
            labelVisibilityMode = BottomNavigationView.LABEL_VISIBILITY_LABELED
            setOnItemSelectedListener { item ->
                when (item.itemId) {
                    FILES -> { showFiles(); true }
                    SCAN -> { scanQr(); false }
                    PRINT -> {
                        if (portalUrl == null) { toast("Scan a shop QR code first."); false }
                        else { showPrint(); true }
                    }
                    else -> false
                }
            }
        }
        root.addView(navigation, LinearLayout.LayoutParams(-1, ViewGroup.LayoutParams.WRAP_CONTENT))
        ViewCompat.setOnApplyWindowInsetsListener(root) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }
        setContentView(root)
        ViewCompat.requestApplyInsets(root)
    }

    private fun showFiles() {
        pickerMode = false
        toolbar.title = "My documents"
        toolbar.navigationIcon = null
        navigation.visibility = View.VISIBLE
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
        toolbar.title = portalHost?.let { "Print · $it" } ?: "Print at a shop"
        toolbar.setNavigationIcon(android.R.drawable.ic_media_previous)
        toolbar.setNavigationOnClickListener { showFiles() }
        navigation.visibility = View.VISIBLE
        navigation.menu.findItem(PRINT).isChecked = true
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
        if (entries.isEmpty()) {
            val empty = card()
            empty.addView(text(
                if (isPicker) "No printable files here. Import a document or open another folder."
                else "No documents here yet. Import files from your phone or create a folder to get started.",
                14, R.color.qp_muted, false
            ).apply { setPadding(dp(18), dp(22), dp(18), dp(22)) })
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
        if (!file.isDirectory) menu.menu.add("Share")
        menu.menu.add("Delete")
        menu.setOnMenuItemClickListener { item ->
            when (item.title.toString()) {
                "Open" -> openDocument(file)
                "Rename" -> nameDialog("Rename", file.name) { name ->
                    try { store.rename(file, name); showFiles() }
                    catch (e: Exception) { toast("Rename failed: ${message(e)}") }
                }
                "Export" -> { exportFile = file; exportOne.launch(file.name) }
                "Export folder" -> { exportFolder = file; exportTree.launch(null) }
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
                    "http" -> !allowedAgentUpload(uri)
                    "about" -> false
                    else -> true
                }
            }
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                val uri = request.url
                if (uri.scheme.equals("http", true) && !allowedAgentUpload(uri)) {
                    return WebResourceResponse(
                        "text/plain", "UTF-8", 403, "Blocked by QRtoPrint", emptyMap(),
                        ByteArrayInputStream("Only private paired-Agent upload endpoints are allowed over HTTP.".toByteArray())
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

    private fun allowedAgentUpload(uri: Uri): Boolean {
        if (!uri.scheme.equals("http", true) || uri.port != 17842 ||
            !uri.path.orEmpty().startsWith("/api/v1/local/uploads/")) return false
        val octets = uri.host.orEmpty().split('.').mapNotNull { it.toIntOrNull() }
        if (octets.size != 4 || octets.any { it !in 0..255 }) return false
        return octets[0] == 10 ||
            (octets[0] == 172 && octets[1] in 16..31) ||
            (octets[0] == 192 && octets[1] == 168)
    }

    private fun scanQr() {
        val options = GmsBarcodeScannerOptions.Builder()
            .setBarcodeFormats(Barcode.FORMAT_QR_CODE)
            .enableAutoZoom()
            .build()
        GmsBarcodeScanning.getClient(this, options).startScan()
            .addOnSuccessListener { barcode ->
                try { confirmShop(StationQrLink.parse(barcode.rawValue)) }
                catch (_: Exception) { toast("This is not a valid QRtoPrint shop QR code.") }
            }
            .addOnFailureListener { toast("QR scanner unavailable. Check Google Play services.") }
    }

    private fun confirmShop(link: StationQrLink) {
        MaterialAlertDialogBuilder(this)
            .setTitle("Connect to this print shop?")
            .setMessage("Open the secure QRtoPrint customer portal at:\n\n${link.host}\n\nThe shop's existing quote, payment and print-status workflow will be used.")
            .setNegativeButton("Cancel", null)
            .setPositiveButton("Continue") { _, _ ->
                portalHost = link.host
                portalUrl = link.url
                ensureWebView().loadUrl(link.url)
                showPrint()
            }.show()
    }

    private fun showPrint() {
        pickerMode = false
        toolbar.title = portalHost?.let { "Print · $it" } ?: "Print at a shop"
        toolbar.setNavigationIcon(android.R.drawable.ic_media_previous)
        toolbar.setNavigationOnClickListener { showFiles() }
        navigation.visibility = View.VISIBLE
        navigation.menu.findItem(PRINT).isChecked = true
        content.removeAllViews()
        val view = ensureWebView()
        (view.parent as? ViewGroup)?.removeView(view)
        content.addView(view, frameParams())
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

    private fun importDirectory(source: DocumentFile, destinationPath: String, depth: Int, counts: IntArray) {
        if (depth > MAX_TREE_DEPTH || counts.sum() >= MAX_TREE_FILES) { counts[1]++; return }
        source.listFiles().forEach { child ->
            if (counts.sum() >= MAX_TREE_FILES) { counts[1]++; return@forEach }
            try {
                val name = child.name ?: "Imported item"
                if (child.isDirectory) {
                    val folder = store.createFolder(destinationPath, name, allowExisting = true)
                    importDirectory(child, store.pathOf(folder), depth + 1, counts)
                } else if (child.isFile) {
                    val input = contentResolver.openInputStream(child.uri) ?: error("File cannot be opened.")
                    input.use { store.importFile(it, name, destinationPath) }
                    counts[0]++
                }
            } catch (_: Exception) { counts[1]++ }
        }
    }

    private fun copyFolder(source: File, targetParent: DocumentFile, depth: Int, count: IntArray) {
        require(depth <= MAX_TREE_DEPTH) { "Folder nesting exceeds the safe export limit." }
        val target = targetParent.findFile(source.name)?.takeIf { it.isDirectory }
            ?: targetParent.createDirectory(source.name) ?: error("Could not create ${source.name}.")
        store.list(source).forEach { child ->
            if (count[0] >= MAX_TREE_FILES) return@forEach
            if (child.isDirectory) copyFolder(child, target, depth + 1, count)
            else {
                val outputFile = target.createFile(DocumentNamePolicy.mimeType(child.name), uniqueName(target, child.name))
                    ?: error("Could not create ${child.name}.")
                val output = contentResolver.openOutputStream(outputFile.uri, "w") ?: error("Cannot write ${child.name}.")
                output.use { out -> FileInputStream(child).use { it.copyTo(out) } }
                count[0]++
            }
        }
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
