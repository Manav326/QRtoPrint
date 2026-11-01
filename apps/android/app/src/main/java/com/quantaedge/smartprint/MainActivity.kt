package com.quantaedge.smartprint

import android.content.ClipData
import android.content.ClipDescription
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
import android.text.InputType
import android.widget.CheckBox
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
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
import androidx.core.view.WindowInsetsControllerCompat
import androidx.documentfile.provider.DocumentFile
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import com.google.android.material.appbar.MaterialToolbar
import com.google.android.material.bottomnavigation.BottomNavigationView
import com.google.android.material.button.MaterialButton
import com.google.android.material.card.MaterialCardView
import com.google.android.material.bottomsheet.BottomSheetDialog
import eightbitlab.com.blurview.BlurTarget
import eightbitlab.com.blurview.BlurView
import com.google.mlkit.vision.documentscanner.GmsDocumentScannerOptions
import com.google.mlkit.vision.documentscanner.GmsDocumentScanning
import com.google.mlkit.vision.documentscanner.GmsDocumentScanningResult
import org.json.JSONArray
import java.io.ByteArrayInputStream
import java.io.File
import java.io.FileInputStream
import java.text.SimpleDateFormat
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Date
import java.util.Locale
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

class MainActivity : AppCompatActivity() {
    companion object {
        private const val HOME = 1
        private const val FILES = 2
        private const val SCAN_PRINT = 3
        private const val ACTIVITY = 4
        private const val SWITCH_SHOP = 91
        private const val OPEN_WITH = 92
        private const val MAX_TREE_FILES = 500
        private const val MAX_TREE_DEPTH = 12
        private const val FOLDER_PICKER_ACCEPT = "application/x-smartprint-folder-selection"
    }

    private lateinit var store: LocalDocumentStore
    private lateinit var customerAccountStore: CustomerAccountStore
    private lateinit var customerPrintSessionStore: CustomerPrintSessionStore
    private lateinit var toolbar: MaterialToolbar
    private lateinit var content: FrameLayout
    private lateinit var navigation: BottomNavigationView
    private lateinit var bottomGlass: BlurView
    private lateinit var accountAction: ImageView
    private var documentViewer: SmartPrintDocumentViewer? = null
    private var viewingDocument: File? = null
    private var webView: WebView? = null
    private var folderPath = ""
    private var pickerPath = ""
    private var pickerMode = false
    private var pendingImportDestinationPath = ""
    private var portalUrl: String? = null
    private var portalHost: String? = null
    private var fileCallback: ValueCallback<Array<Uri>>? = null
    private var exportFile: File? = null
    private var exportFolder: File? = null
    private var archiveFiles: List<File> = emptyList()
    private var accountCreateMode = false
    private var editingPdf: File? = null
    private val selectedFilePaths = linkedSetOf<String>()
    private val chooserSelectedFilePaths = linkedSetOf<String>()
    private val chooserSelectedFolderPaths = linkedSetOf<String>()
    private val expandedFolderPaths = linkedSetOf<String>()
    private var fileChooserAllowsMultiple = false
    private var fileChooserSelectFolders = false
    private var pendingScanNeedsDestination = false
    private var pendingScanDestinationPath = ""
    private var profileVisible = false
    private var profileReturnDestination = HOME
    private var librarySearchQuery = ""

    private lateinit var importFiles: ActivityResultLauncher<Array<String>>
    private lateinit var importTree: ActivityResultLauncher<Uri?>
    private lateinit var exportOne: ActivityResultLauncher<String>
    private lateinit var exportTree: ActivityResultLauncher<Uri?>
    private lateinit var exportArchive: ActivityResultLauncher<String>
    private lateinit var scannerLauncher: ActivityResultLauncher<Intent>
    private lateinit var documentScanLauncher: ActivityResultLauncher<IntentSenderRequest>

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        val lightSystemBars = (resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) ==
            android.content.res.Configuration.UI_MODE_NIGHT_NO
        window.statusBarColor = getColor(R.color.qp_surface)
        window.navigationBarColor = getColor(R.color.qp_surface)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            isAppearanceLightStatusBars = lightSystemBars
            isAppearanceLightNavigationBars = lightSystemBars
        }
        store = LocalDocumentStore(filesDir)
        customerAccountStore = CustomerAccountStore(this)
        customerPrintSessionStore = CustomerPrintSessionStore(this)
        pendingScanNeedsDestination = savedInstanceState?.getBoolean("pendingScanNeedsDestination") ?: false
        pendingScanDestinationPath = savedInstanceState?.getString("pendingScanDestinationPath").orEmpty()
        folderPath = savedInstanceState?.getString("folderPath").orEmpty()
        folderPath = runCatching { store.pathOf(store.folder(folderPath)) }.getOrDefault("")
        pendingImportDestinationPath = savedInstanceState?.getString("pendingImportDestinationPath").orEmpty()
        librarySearchQuery = savedInstanceState?.getString("librarySearchQuery").orEmpty()
        registerFileActions()
        buildLayout()
        showHome()
        val savedShop = getSharedPreferences("smartprint.customer.last-shop", MODE_PRIVATE)
        portalUrl = savedInstanceState?.getString("portalUrl")
            ?: savedShop.getString("portalUrl", null)
        portalHost = savedInstanceState?.getString("portalHost")
            ?: savedShop.getString("portalHost", null)
            ?: portalUrl?.let { Uri.parse(it).host }
        portalUrl?.let { url ->
            val view = ensureWebView()
            val webState = savedInstanceState?.getBundle("webViewState")
            val restored = webState?.let { view.restoreState(it) != null } ?: false
            if (!restored) view.loadUrl(url)
        }

        val restoredNavigation = savedInstanceState?.getInt("selectedNavigation", HOME) ?: HOME
        when (restoredNavigation) {
            FILES -> showFiles()
            SCAN_PRINT -> showScanPrint()
            ACTIVITY -> showActivity()
            else -> if (savedInstanceState == null && portalUrl != null) showPrint() else showHome()
        }
        val restoredDocumentPath = savedInstanceState?.getString("viewingDocumentPath")
        if (!restoredDocumentPath.isNullOrBlank()) {
            val restoredDocument = runCatching {
                File(restoredDocumentPath).canonicalFile.also { store.pathOf(it) }
            }.getOrNull()?.takeIf { it.isFile }
            if (restoredDocument != null) {
                folderPath = restoredDocument.parentFile?.let { store.pathOf(it) }.orEmpty()
                showFiles()
                showDocumentViewer(
                    restoredDocument,
                    savedInstanceState?.getInt("viewerPosition", 0) ?: 0
                )
            }
        } else if (savedInstanceState?.getBoolean("profileVisible") == true) {
            profileReturnDestination = savedInstanceState.getInt(
                "profileReturnDestination",
                navigation.selectedItemId
            )
            showProfile()
        }
        handleShareIntent(intent)
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putBoolean("pendingScanNeedsDestination", pendingScanNeedsDestination)
        outState.putString("pendingScanDestinationPath", pendingScanDestinationPath)
        outState.putString("folderPath", folderPath)
        outState.putString("pendingImportDestinationPath", pendingImportDestinationPath)
        outState.putString("librarySearchQuery", librarySearchQuery)
        outState.putInt("selectedNavigation", if (::navigation.isInitialized) navigation.selectedItemId else HOME)
        outState.putBoolean("profileVisible", profileVisible)
        outState.putInt("profileReturnDestination", profileReturnDestination)
        outState.putString("viewingDocumentPath", viewingDocument?.absolutePath)
        outState.putInt("viewerPosition", documentViewer?.currentPosition() ?: 0)
        outState.putString("portalUrl", portalUrl)
        outState.putString("portalHost", portalHost)
        webView?.let { view ->
            val webState = Bundle()
            view.saveState(webState)
            outState.putBundle("webViewState", webState)
        }
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
            profileVisible -> returnFromProfile()
            pickerMode && pickerPath.isNotBlank() -> { pickerPath = parent(pickerPath); refreshLibrary(preserveScroll = false) }
            pickerMode -> cancelPicker()
            documentViewer != null -> closeDocumentViewer()
            editingPdf != null -> showFiles()
            navigation.selectedItemId == SCAN_PRINT && webView?.canGoBack() == true -> webView?.goBack()
            navigation.selectedItemId == FILES && folderPath.isNotEmpty() -> {
                folderPath = parent(folderPath)
                showFiles()
            }
            navigation.selectedItemId == SCAN_PRINT -> showHome()
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
                    confirmShop(StationQrLink.parse(
                        rawValue,
                        expectedServiceUrl = BuildConfig.SERVICE_BASE_URL,
                        allowLocalDevelopment = BuildConfig.DEBUG
                    ))
                } catch (_: Exception) {
                    toast("This is not a valid Smart Print shop QR code.")
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
                    if (pendingScanNeedsDestination) showScanDestinationPicker(scan)
                    else saveDocumentScan(scan, pendingScanDestinationPath)
                } catch (e: Exception) {
                    pendingScanNeedsDestination = false
                    pendingScanDestinationPath = ""
                    toast("Could not save the scan: ${message(e)}")
                }
            } else {
                pendingScanNeedsDestination = false
                pendingScanDestinationPath = ""
            }
        }
        importFiles = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
            if (uris.isNullOrEmpty()) return@registerForActivityResult
            var ok = 0
            var failed = 0
            uris.forEach { uri ->
                try { importUri(uri, pendingImportDestinationPath); ok++ }
                catch (_: Exception) { failed++ }
            }
            pendingImportDestinationPath = ""
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
            if (uri == null || source == null) {
                if (source?.parentFile?.canonicalPath == cacheDir.canonicalPath) source.deleteRecursively()
                return@registerForActivityResult
            }
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
            } catch (e: Exception) { toast("Folder export failed: ${message(e)}") }
            finally {
                if (source.parentFile?.canonicalPath == cacheDir.canonicalPath) source.deleteRecursively()
            }
        }
        exportArchive = registerForActivityResult(ActivityResultContracts.CreateDocument("application/zip")) { uri ->
            val files = archiveFiles
            archiveFiles = emptyList()
            if (uri == null || files.isEmpty()) return@registerForActivityResult
            try {
                val output = contentResolver.openOutputStream(uri, "w") ?: error("Destination unavailable.")
                ZipOutputStream(output.buffered()).use { zip ->
                    files.forEach { source ->
                        require(source.isFile && source.exists()) { "${source.name} is no longer available." }
                        val relativePath = store.pathOf(source).replace("\\", "/")
                        zip.putNextEntry(ZipEntry(relativePath))
                        FileInputStream(source).use { it.copyTo(zip) }
                        zip.closeEntry()
                    }
                }
                toast("Exported ${files.size} file(s) as a ZIP archive.")
            } catch (e: Exception) { toast("ZIP export failed: ${message(e)}") }
        }
    }

    override fun onDestroy() {
        documentViewer?.dispose()
        documentViewer = null
        super.onDestroy()
    }

    private fun buildLayout() {
        window.statusBarColor = getColor(R.color.qp_surface)
        window.navigationBarColor = getColor(R.color.qp_surface)
        val backdrop = SmartPrintGlass.backdrop(this)
        val root = FrameLayout(this).apply {
            clipChildren = false
            clipToPadding = false
            background = backdrop
        }

        val blurTarget = BlurTarget(this)
        content = FrameLayout(this).apply {
            clipToPadding = false
            setPadding(0, dp(64), 0, dp(76))
            setBackgroundColor(android.graphics.Color.TRANSPARENT)
        }
        blurTarget.addView(content, FrameLayout.LayoutParams(-1, -1))
        root.addView(blurTarget, FrameLayout.LayoutParams(-1, -1))

        toolbar = MaterialToolbar(this).apply {
            title = "Home"
            setTitleTextColor(getColor(R.color.qp_text))
            setTitleTextAppearance(this@MainActivity, R.style.TextAppearance_SmartPrint_Toolbar)
            setBackgroundColor(android.graphics.Color.TRANSPARENT)
            elevation = 0f
            menu.add(Menu.NONE, SWITCH_SHOP, Menu.NONE, "Switch shop").apply {
                setIcon(android.R.drawable.ic_menu_camera)
                setContentDescription("Switch print shop")
                setTooltipText("Scan another shop QR code")
                setShowAsAction(android.view.MenuItem.SHOW_AS_ACTION_NEVER)
                isVisible = false
                setOnMenuItemClickListener { switchShop(); true }
            }
            menu.add(Menu.NONE, OPEN_WITH, Menu.NONE, "Open with another app").apply {
                setShowAsAction(android.view.MenuItem.SHOW_AS_ACTION_NEVER)
                isVisible = false
                setOnMenuItemClickListener {
                    viewingDocument?.let(::openDocumentExternally)
                    true
                }
            }
            accountAction = ImageView(this@MainActivity).apply {
                contentDescription = "Account"
                setImageResource(R.drawable.ic_account)
                imageTintList = ColorStateList.valueOf(this@MainActivity.getColor(R.color.qp_text))
                scaleType = ImageView.ScaleType.CENTER_INSIDE
                setPadding(dp(12), dp(12), dp(12), dp(12))
                val selectableBackground = android.util.TypedValue()
                if (this@MainActivity.theme.resolveAttribute(
                        android.R.attr.selectableItemBackgroundBorderless,
                        selectableBackground,
                        true
                    )
                ) setBackgroundResource(selectableBackground.resourceId)
                isClickable = true
                isFocusable = true
                setOnClickListener { showProfile() }
            }
            addView(accountAction, androidx.appcompat.widget.Toolbar.LayoutParams(
                dp(44), dp(44), Gravity.END or Gravity.CENTER_VERTICAL
            ))
        }

        val topGlass = BlurView(this).apply {
            foreground = SmartPrintGlass.edge(this@MainActivity)
            addView(toolbar, FrameLayout.LayoutParams(-1, dp(56), Gravity.BOTTOM))
        }
        root.addView(topGlass, FrameLayout.LayoutParams(-1, dp(64), Gravity.TOP))

        navigation = BottomNavigationView(this).apply {
            menu.add(Menu.NONE, HOME, 0, "Home").apply {
                setIcon(R.drawable.ic_nav_home); setContentDescription("Home")
            }
            menu.add(Menu.NONE, FILES, 1, "Files").apply {
                setIcon(R.drawable.ic_nav_files); setContentDescription("My Files")
            }
            menu.add(Menu.NONE, SCAN_PRINT, 2, "Print").apply {
                setIcon(R.drawable.ic_nav_scan_print); setContentDescription("Print at a shop")
            }
            menu.add(Menu.NONE, ACTIVITY, 3, "Activity").apply {
                setIcon(R.drawable.ic_nav_activity); setContentDescription("Activity")
            }
            labelVisibilityMode = BottomNavigationView.LABEL_VISIBILITY_LABELED
            setItemHorizontalTranslationEnabled(false)
            setItemTextAppearanceActive(R.style.TextAppearance_SmartPrint_Navigation)
            setItemTextAppearanceInactive(R.style.TextAppearance_SmartPrint_Navigation)
            itemIconSize = dp(21)
            setItemPaddingTop(dp(4))
            setItemPaddingBottom(dp(4))
            minimumHeight = dp(64)
            backgroundTintList = ColorStateList.valueOf(android.graphics.Color.TRANSPARENT)
            setBackgroundColor(android.graphics.Color.TRANSPARENT)
            itemIconTintList = ColorStateList(
                arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
                intArrayOf(getColor(R.color.qp_primary), getColor(R.color.qp_muted))
            )
            itemTextColor = itemIconTintList
            itemRippleColor = ColorStateList.valueOf(getColor(R.color.qp_soft))
            elevation = 0f
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

        bottomGlass = BlurView(this).apply {
            foreground = SmartPrintGlass.edge(this@MainActivity)
            addView(navigation, FrameLayout.LayoutParams(-1, dp(66), Gravity.BOTTOM))
        }
        root.addView(bottomGlass, FrameLayout.LayoutParams(-1, dp(76), Gravity.BOTTOM))

        ViewCompat.setOnApplyWindowInsetsListener(root) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }
        setContentView(root)
        SmartPrintGlass.bindBlur(topGlass, blurTarget, this, R.color.qp_glass_top_overlay, backdrop)
        SmartPrintGlass.bindBlur(bottomGlass, blurTarget, this, R.color.qp_glass_bottom_overlay, backdrop)
        ViewCompat.requestApplyInsets(root)
    }

    private fun setBottomNavigationVisible(visible: Boolean) {
        val visibility = if (visible) View.VISIBLE else View.GONE
        navigation.visibility = visibility
        if (::bottomGlass.isInitialized) bottomGlass.visibility = visibility
        if (::content.isInitialized) content.setPadding(0, dp(64), 0, if (visible) dp(76) else dp(8))
    }

    private fun resetToolbar(title: String) {
        profileVisible = false
        toolbar.title = title
        toolbar.navigationIcon = null
        toolbar.setNavigationOnClickListener(null)
        toolbar.menu.findItem(SWITCH_SHOP)?.isVisible = portalUrl != null
        toolbar.menu.findItem(OPEN_WITH)?.isVisible = false
        accountAction.visibility = View.VISIBLE
        setBottomNavigationVisible(true)
    }

    private fun showProfile() {
        pickerMode = false
        if (!profileVisible) profileReturnDestination = navigation.selectedItemId
        profileVisible = true
        toolbar.title = "Account"
        toolbar.setNavigationIcon(android.R.drawable.ic_media_previous)
        toolbar.setNavigationOnClickListener { returnFromProfile() }
        accountAction.visibility = View.GONE
        setBottomNavigationVisible(false)
        content.removeAllViews()
        content.addView(accountView(), frameParams())
    }

    private fun returnFromProfile() {
        if (!profileVisible) return
        profileVisible = false
        when (profileReturnDestination) {
            FILES -> showFiles()
            SCAN_PRINT -> if (portalUrl != null) showPrint() else showScanPrint()
            ACTIVITY -> showActivity()
            else -> showHome()
        }
    }

    private fun accountView(): View {
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(18), dp(18), dp(28))
        }
        scroll.addView(column)
        val auth = customerAccountStore.read()

        column.addView(text(
            if (auth == null) "Guest access is ready" else "Smart Print customer",
            12, R.color.qp_muted, false
        ).apply { setPadding(0, dp(4), 0, dp(16)) })

        if (auth != null) {
            val profileCard = card()
            val profile = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(14), dp(14), dp(14), dp(14))
            }
            val avatar = TextView(this).apply {
                text = auth.displayName.trim().take(1).uppercase(Locale.ROOT).ifBlank { "S" }
                textSize = 19f
                setTypeface(typeface, android.graphics.Typeface.BOLD)
                gravity = Gravity.CENTER
                setTextColor(getColor(R.color.qp_primary_content))
                background = roundedDrawable(getColor(R.color.qp_primary), dp(18))
            }
            profile.addView(avatar, LinearLayout.LayoutParams(dp(48), dp(48)))
            val details = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(12), 0, 0, 0)
            }
            details.addView(text(auth.displayName.ifBlank { "Smart Print customer" }, 16, R.color.qp_text, true))
            details.addView(text(auth.email, 12, R.color.qp_muted, false).apply {
                setPadding(0, dp(3), 0, 0)
            })
            profile.addView(details, LinearLayout.LayoutParams(0, -2, 1f))
            profileCard.addView(profile)
            column.addView(profileCard, linearParams())
            column.addView(button("Print history", true).apply {
                setOnClickListener { showActivity() }
            }, linearParams().apply { topMargin = dp(12) })
            column.addView(button("Sign out", false).apply {
                setOnClickListener {
                    showConfirmSheet("Sign out?", "Local files stay on this phone.", "Sign out") {
                        signOutCustomer()
                    }
                }
            }, linearParams().apply { topMargin = dp(8) })
            return scroll
        }

        val modeRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        modeRow.addView(button("Sign in", !accountCreateMode).apply {
            setOnClickListener { accountCreateMode = false; showProfile() }
        }, LinearLayout.LayoutParams(0, dp(42), 1f))
        modeRow.addView(button("Create account", accountCreateMode).apply {
            setOnClickListener { accountCreateMode = true; showProfile() }
        }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(8) })
        column.addView(modeRow, linearParams().apply { bottomMargin = dp(12) })

        val formCard = card()
        val form = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(14), dp(14), dp(14), dp(14))
        }
        val displayName = EditText(this).apply {
            hint = "Name"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_WORDS
            visibility = if (accountCreateMode) View.VISIBLE else View.GONE
            maxLines = 1
            setTextColor(getColor(R.color.qp_text))
            setHintTextColor(getColor(R.color.qp_muted))
            background = roundedDrawable(getColor(R.color.qp_surface), dp(10), getColor(R.color.qp_outline))
            setPadding(dp(12), dp(11), dp(12), dp(11))
        }
        val email = EditText(this).apply {
            hint = "Email address"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_EMAIL_ADDRESS
            maxLines = 1
            setTextColor(getColor(R.color.qp_text))
            setHintTextColor(getColor(R.color.qp_muted))
            background = roundedDrawable(getColor(R.color.qp_surface), dp(10), getColor(R.color.qp_outline))
            setPadding(dp(12), dp(11), dp(12), dp(11))
        }
        val password = EditText(this).apply {
            hint = if (accountCreateMode) "Password · 12+ chars" else "Password"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            maxLines = 1
            setTextColor(getColor(R.color.qp_text))
            setHintTextColor(getColor(R.color.qp_muted))
            background = roundedDrawable(getColor(R.color.qp_surface), dp(10), getColor(R.color.qp_outline))
            setPadding(dp(12), dp(11), dp(12), dp(11))
        }
        if (accountCreateMode) form.addView(displayName, linearParams().apply { bottomMargin = dp(8) })
        form.addView(email, linearParams().apply { bottomMargin = dp(8) })
        form.addView(password, linearParams())
        form.addView(button(if (accountCreateMode) "Create account" else "Sign in", true).apply {
            setOnClickListener {
                val name = displayName.text.toString().trim()
                val emailValue = email.text.toString().trim()
                val passwordValue = password.text.toString()
                if (emailValue.isBlank() || passwordValue.isBlank() || (accountCreateMode && name.isBlank())) {
                    toast("Complete the required fields.")
                    return@setOnClickListener
                }
                authenticateCustomer(name, emailValue, passwordValue, accountCreateMode)
            }
        }, linearParams().apply { topMargin = dp(12) })
        formCard.addView(form)
        column.addView(formCard, linearParams())
        if (accountCreateMode) {
            column.addView(text("12+ chars · Upper · Lower · Number · Symbol", 11, R.color.qp_muted, false)
                .apply { setPadding(dp(3), dp(9), dp(3), 0) })
        }
        return scroll
    }

    private fun accountServiceUrl(): String = BuildConfig.SERVICE_BASE_URL

    private fun authenticateCustomer(name: String, email: String, password: String, create: Boolean) {
        toast(if (create) "Creating your Smart Print account…" else "Signing in…")
        Thread {
            try {
                val auth = if (create) {
                    CustomerAccountClient.register(accountServiceUrl(), name, email, password)
                } else {
                    CustomerAccountClient.signIn(accountServiceUrl(), email, password)
                }
                val saved = auth.copy(displayName = if (create) name else auth.displayName.ifBlank { email.substringBefore('@') })
                customerAccountStore.save(saved)
                runOnUiThread {
                    accountCreateMode = false
                    showProfile()
                    toast(if (create) "Your Smart Print account is ready." else "You're signed in.")
                }
            } catch (e: Exception) {
                runOnUiThread { toast("Account request failed: ${message(e)}") }
            }
        }.start()
    }

    private fun signOutCustomer() {
        val auth = customerAccountStore.read()
        customerAccountStore.clear()
        showProfile()
        if (auth != null) Thread {
            try { CustomerAccountClient.logout(accountServiceUrl(), auth) } catch (_: Exception) { }
        }.start()
        toast("Signed out. Your local documents are unchanged.")
    }

    private fun showHome() {
        pickerMode = false
        resetToolbar("Home")
        navigation.menu.findItem(HOME).isChecked = true
        content.removeAllViews()
        content.addView(homeView(), frameParams())
    }

    private fun homeView(): View {
        val scroll = ScrollView(this).apply {
            isFillViewport = true
            clipToPadding = false
        }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(15), dp(18), dp(24))
        }
        scroll.addView(column)

        val recentFiles = store.root.walkTopDown().filter { it.isFile }.toList()
        val used = android.text.format.Formatter.formatShortFileSize(this, store.storageUsageBytes())
        val heading = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }
        heading.addView(text("Your space", 23, R.color.qp_text, true))
        heading.addView(text("${recentFiles.size} files · $used used", 12, R.color.qp_muted, false).apply {
            setPadding(0, dp(3), 0, 0)
        })
        column.addView(heading, linearParams().apply { bottomMargin = dp(16) })

        val quickActions = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        quickActions.addView(button("Scan document", true).apply {
            icon = getDrawable(R.drawable.ic_document_scan)
            iconTint = ColorStateList.valueOf(getColor(android.R.color.white))
            iconPadding = dp(8)
            iconGravity = MaterialButton.ICON_GRAVITY_TEXT_START
            setOnClickListener { startDocumentScan() }
        }, LinearLayout.LayoutParams(0, dp(48), 1f))
        quickActions.addView(button("Import", false).apply {
            icon = getDrawable(R.drawable.ic_file)
            iconTint = ColorStateList.valueOf(getColor(R.color.qp_primary))
            iconPadding = dp(8)
            iconGravity = MaterialButton.ICON_GRAVITY_TEXT_START
            setOnClickListener {
                folderPath = ""
                pendingImportDestinationPath = ""
                importFiles.launch(arrayOf("*/*"))
                navigation.selectedItemId = FILES
            }
        }, LinearLayout.LayoutParams(0, dp(48), 1f).apply { marginStart = dp(9) })
        column.addView(quickActions, linearParams())

        val shopCard = card().apply {
            strokeWidth = dp(1)
            setOnClickListener { if (portalUrl != null) showPrint() else showScanPrint() }
        }
        val shopRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(13), dp(12), dp(13), dp(12))
        }
        val shopGlyph = ImageView(this).apply {
            setImageResource(R.drawable.ic_nav_scan_print)
            imageTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
            setPadding(dp(10), dp(10), dp(10), dp(10))
            background = roundedDrawable(getColor(R.color.qp_soft), dp(13))
        }
        shopRow.addView(shopGlyph, LinearLayout.LayoutParams(dp(42), dp(42)))
        val shopCopy = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(11), 0, 0, 0)
        }
        shopCopy.addView(text(
            if (portalUrl == null) "Print at a shop" else "Shop connected",
            14, R.color.qp_text, true
        ))
        shopCopy.addView(text(
            if (portalUrl == null) "Scan a shop QR" else (portalHost ?: "Print session"),
            12, R.color.qp_muted, false
        ).apply { setPadding(0, dp(2), 0, 0) })
        shopRow.addView(shopCopy, LinearLayout.LayoutParams(0, -2, 1f))
        shopRow.addView(text("›", 25, R.color.qp_muted, false))
        shopCard.addView(shopRow)
        column.addView(shopCard, linearParams().apply { topMargin = dp(12); bottomMargin = dp(20) })

        val recentHeader = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        recentHeader.addView(text("Recent", 17, R.color.qp_text, true),
            LinearLayout.LayoutParams(0, -2, 1f))
        recentHeader.addView(text("See all", 12, R.color.qp_primary, true).apply {
            setOnClickListener { showFiles() }
            isClickable = true
        })
        column.addView(recentHeader, linearParams().apply { bottomMargin = dp(8) })

        val recent = recentFiles.sortedByDescending { it.lastModified() }.take(6)
        if (recent.isEmpty()) {
            column.addView(text("No files yet", 13, R.color.qp_muted, false).apply {
                setPadding(dp(2), dp(16), 0, dp(16))
            })
        } else {
            recent.forEach { file ->
                column.addView(entryView(file, false, showActions = false), linearParams().apply { bottomMargin = dp(4) })
            }
        }
        return scroll
    }

    private fun showScanPrint() {
        pickerMode = false
        if (portalUrl != null) { showPrint(); return }
        resetToolbar("Print")
        navigation.menu.findItem(SCAN_PRINT).isChecked = true
        content.removeAllViews()
        content.addView(scanPrintView(), frameParams())
    }

    private fun scanPrintView(): View {
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(24), dp(34), dp(24), dp(28))
        }
        val icon = ImageView(this).apply {
            setImageResource(R.drawable.ic_nav_scan_print)
            imageTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
            setPadding(dp(20), dp(20), dp(20), dp(20))
            background = roundedDrawable(getColor(R.color.qp_soft), dp(24))
        }
        column.addView(icon, LinearLayout.LayoutParams(dp(88), dp(88)))
        column.addView(text("Connect to a shop", 22, R.color.qp_text, true).apply {
            gravity = Gravity.CENTER
            setPadding(0, dp(18), 0, dp(6))
        })
        column.addView(text("Scan the counter QR code.", 13, R.color.qp_muted, false).apply {
            gravity = Gravity.CENTER
        })
        column.addView(button("Scan QR code", true).apply {
            setOnClickListener { scanQr() }
        }, LinearLayout.LayoutParams(-1, dp(48)).apply { topMargin = dp(22) })
        column.addView(button("Open My Files", false).apply {
            setOnClickListener { showFiles() }
        }, LinearLayout.LayoutParams(-1, dp(44)).apply { topMargin = dp(8) })
        return column
    }

    private fun showActivity() {
        pickerMode = false
        resetToolbar("Activity")
        navigation.menu.findItem(ACTIVITY).isChecked = true
        content.removeAllViews()
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(16), dp(18), dp(24))
        }
        scroll.addView(column)
        column.addView(text("Print history", 22, R.color.qp_text, true))
        val auth = customerAccountStore.read()
        if (auth == null) {
            column.addView(text("Sign in to sync jobs.", 13, R.color.qp_muted, false)
                .apply { setPadding(0, dp(5), 0, dp(16)) })
            column.addView(button("Sign in", true).apply {
                setOnClickListener { showProfile() }
            }, linearParams())
        } else {
            column.addView(text(auth.email, 12, R.color.qp_muted, false)
                .apply { setPadding(0, dp(4), 0, dp(14)) })
            val history = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            history.addView(text("Loading…", 12, R.color.qp_muted, false))
            column.addView(history, linearParams())
            loadCustomerHistory(history)
        }
        content.addView(scroll, frameParams())
    }

    private fun activityStatusLabel(status: String): String = when (status.uppercase(Locale.ROOT)) {
        "AWAITING_SHOP_APPROVAL" -> "Shop review"
        "AWAITING_PAYMENT", "AWAITING_CUSTOMER_PAYMENT" -> "Payment due"
        "AWAITING_COUNTER_PAYMENT" -> "Pay at counter"
        "PAYMENT_REPORTED" -> "Verify pending"
        "PAYMENT_REJECTED" -> "Payment declined"
        "PAID_AUTHORISED" -> "Paid"
        "LOCAL_QUEUE_CLAIMED" -> "Transferring"
        "LOCAL_QUEUE_READY" -> "Queued"
        "PRINT_AUTHORISED" -> "Print approved"
        "PRINT_SUBMITTING" -> "Sending"
        "SPOOLER_ACCEPTED" -> "Queue accepted"
        "PRINTING" -> "Printing"
        "OUTPUT_UNCONFIRMED" -> "Output check"
        "COMPLETED" -> "Completed"
        "REJECTED" -> "Rejected"
        "QUOTE_EXPIRED" -> "Quote expired"
        else -> "In progress"
    }

    private fun loadCustomerHistory(container: LinearLayout) {
        Thread {
            try {
                var auth = customerAccountStore.read() ?: error("Sign in to view history.")
                val jobs = try {
                    if (CustomerAccountClient.accessTokenIsExpired(auth)) {
                        auth = CustomerAccountClient.refresh(accountServiceUrl(), auth)
                        customerAccountStore.save(auth)
                    }
                    CustomerAccountClient.history(accountServiceUrl(), auth.accessToken)
                } catch (error: CustomerApiException) {
                    if (error.statusCode != 401) throw error
                    auth = CustomerAccountClient.refresh(accountServiceUrl(), auth)
                    customerAccountStore.save(auth)
                    CustomerAccountClient.history(accountServiceUrl(), auth.accessToken)
                }
                runOnUiThread {
                    if (navigation.selectedItemId != ACTIVITY || isFinishing) return@runOnUiThread
                    container.removeAllViews()
                    if (jobs.isEmpty()) {
                        container.addView(text("No jobs yet", 13, R.color.qp_muted, false)
                            .apply { setPadding(0, dp(14), 0, dp(14)) })
                    } else {
                        jobs.forEach { job ->
                            val item = card()
                            val body = LinearLayout(this).apply {
                                orientation = LinearLayout.VERTICAL
                                setPadding(dp(12), dp(11), dp(12), dp(11))
                            }
                            val top = LinearLayout(this).apply {
                                orientation = LinearLayout.HORIZONTAL
                                gravity = Gravity.CENTER_VERTICAL
                            }
                            top.addView(text(job.jobNumber, 13, R.color.qp_text, true),
                                LinearLayout.LayoutParams(0, -2, 1f))
                            top.addView(text(activityStatusLabel(job.status), 10, R.color.qp_primary, true))
                            body.addView(top)
                            val createdLabel = runCatching {
                                Instant.parse(job.createdUtc)
                                    .atZone(ZoneId.systemDefault())
                                    .format(DateTimeFormatter.ofPattern("dd MMM yyyy · hh:mm a", Locale.ROOT))
                            }.getOrDefault("Date unavailable")
                            body.addView(text(job.shopName + " · " + createdLabel, 10, R.color.qp_muted, false)
                                .apply { setPadding(0, dp(3), 0, 0) })
                            body.addView(text(job.originalFileName, 12, R.color.qp_text, true)
                                .apply { setPadding(0, dp(7), 0, 0) })
                            val quote = "%.2f".format(Locale.ROOT, job.quotedAmountMinor / 100.0)
                            body.addView(text(
                                job.pageCount.toString() + " pages · " + job.copies + " copies · Quote " + job.currency + " " + quote,
                                10, R.color.qp_muted, false
                            ).apply { setPadding(0, dp(3), 0, 0) })
                            val paid = "%.2f".format(Locale.ROOT, job.paidAmountMinor / 100.0)
                            val returned = "%.2f".format(Locale.ROOT, job.refundedAmountMinor / 100.0)
                            val net = "%.2f".format(Locale.ROOT, maxOf(0L, job.paidAmountMinor - job.refundedAmountMinor) / 100.0)
                            body.addView(text(
                                "Paid " + paid + " · Returned " + returned + " · Net " + net + " " + job.currency,
                                10, R.color.qp_muted, false
                            ).apply { setPadding(0, dp(3), 0, 0) })
                            if (job.rejectedPageNumbersJson != "[]" && job.rejectedPageNumbersJson.isNotBlank()) {
                                body.addView(text("Rejected: " + job.rejectedPageNumbersJson, 10, R.color.qp_muted, false)
                                    .apply { setPadding(0, dp(3), 0, 0) })
                            }
                            job.outputCompletedUtc?.let { outputUtc ->
                                val outputLabel = runCatching {
                                    Instant.parse(outputUtc).atZone(ZoneId.systemDefault())
                                        .format(DateTimeFormatter.ofPattern("dd MMM · hh:mm a", Locale.ROOT))
                                }.getOrDefault(outputUtc)
                                body.addView(text("Output confirmed · " + outputLabel, 10, R.color.qp_muted, false)
                                    .apply { setPadding(0, dp(3), 0, 0) })
                            }
                            item.addView(body)
                            container.addView(item, linearParams().apply { bottomMargin = dp(6) })
                        }
                    }
                }
            } catch (e: Exception) {
                runOnUiThread {
                    if (navigation.selectedItemId != ACTIVITY || isFinishing) return@runOnUiThread
                    container.removeAllViews()
                    container.addView(text("History unavailable.", 13, R.color.qp_muted, false))
                    container.addView(button("Retry", false).apply {
                        setOnClickListener { showActivity() }
                    }, linearParams().apply { topMargin = dp(8) })
                }
            }
        }.start()
    }

    private fun showFiles() {
        pickerMode = false
        editingPdf = null
        resetToolbar("Files")
        navigation.menu.findItem(FILES).isChecked = true
        content.removeAllViews()
        content.addView(libraryView(false), frameParams())
    }

    private fun showPicker(resetPath: Boolean = true) {
        pickerMode = true
        if (resetPath) pickerPath = ""
        toolbar.title = when {
            fileChooserSelectFolders -> "Select folders"
            fileChooserAllowsMultiple -> "Select files"
            else -> "Select file"
        }
        toolbar.setNavigationIcon(android.R.drawable.ic_media_previous)
        toolbar.setNavigationOnClickListener { cancelPicker() }
        setBottomNavigationVisible(false)
        content.removeAllViews()
        content.addView(libraryView(true), frameParams())
    }

    private fun showPrint() {
        pickerMode = false
        profileVisible = false
        toolbar.menu.findItem(SWITCH_SHOP)?.isVisible = portalUrl != null
        toolbar.title = "Print"
        toolbar.setNavigationIcon(android.R.drawable.ic_media_previous)
        toolbar.setNavigationOnClickListener { showFiles() }
        accountAction.visibility = View.VISIBLE
        setBottomNavigationVisible(true)
        navigation.menu.findItem(SCAN_PRINT).isChecked = true
        content.removeAllViews()
        val view = ensureWebView()
        (view.parent as? ViewGroup)?.removeView(view)
        content.addView(view, frameParams())
    }

    private fun refreshLibrary(preserveScroll: Boolean = true) {
        val previousScroll = if (preserveScroll) (content.getChildAt(0) as? ScrollView)?.scrollY ?: 0 else 0
        if (pickerMode) showPicker(resetPath = false) else showFiles()
        if (preserveScroll) {
            content.post {
                (content.getChildAt(0) as? ScrollView)?.scrollTo(0, previousScroll)
            }
        }
    }

    private inner class FloatingItemDragShadowBuilder(
        private val dragView: View
    ) : View.DragShadowBuilder(dragView) {
        override fun onProvideShadowMetrics(
            outShadowSize: android.graphics.Point,
            outShadowTouchPoint: android.graphics.Point
        ) {
            outShadowSize.set(dragView.width, dragView.height)
            outShadowTouchPoint.set(dragView.width / 2, dp(24))
        }

        override fun onDrawShadow(canvas: android.graphics.Canvas) {
            val shadowPaint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
                color = getColor(R.color.qp_card)
                setShadowLayer(dp(9).toFloat(), 0f, dp(4).toFloat(), 0x66000000)
            }
            canvas.drawRoundRect(
                0f, 0f, dragView.width.toFloat(), dragView.height.toFloat(),
                dp(11).toFloat(), dp(11).toFloat(), shadowPaint
            )
            dragView.draw(canvas)
        }
    }

    private fun addFolderBreadcrumbs(parentView: LinearLayout, path: String, isPicker: Boolean) {
        val scroll = HorizontalScrollView(this).apply {
            isHorizontalScrollBarEnabled = false
            clipToPadding = false
            overScrollMode = View.OVER_SCROLL_NEVER
            setPadding(dp(2), dp(2), dp(2), dp(7))
        }
        val segments = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }

        fun addSeparator() {
            if (segments.childCount > 0) {
                segments.addView(text("›", 13, R.color.qp_muted, false).apply {
                    setPadding(dp(7), 0, dp(7), 0)
                }, LinearLayout.LayoutParams(-2, -2))
            }
        }

        fun addFolder(label: String, targetPath: String) {
            addSeparator()
            segments.addView(text(label, 12, R.color.qp_primary, true).apply {
                contentDescription = "Open folder $label"
                isClickable = true
                isFocusable = true
                setPadding(dp(2), dp(5), dp(2), dp(5))
                val selectable = android.util.TypedValue()
                if (theme.resolveAttribute(android.R.attr.selectableItemBackground, selectable, true)) {
                    setBackgroundResource(selectable.resourceId)
                }
                setOnClickListener {
                    if (isPicker) pickerPath = targetPath else folderPath = targetPath
                    refreshLibrary(preserveScroll = false)
                }
            }, LinearLayout.LayoutParams(-2, -2))
        }

        addFolder("My Files", "")
        var currentPath = ""
        path.trim('/').split('/').filter { it.isNotBlank() }.forEach { segment ->
            currentPath = if (currentPath.isBlank()) segment else "$currentPath/$segment"
            addFolder(segment, currentPath)
        }
        scroll.addView(segments, HorizontalScrollView.LayoutParams(-2, -1))
        parentView.addView(scroll, LinearLayout.LayoutParams(-1, dp(35)))
        scroll.post { scroll.fullScroll(View.FOCUS_RIGHT) }
    }

    private fun libraryView(isPicker: Boolean): View {
        val scroll = ScrollView(this).apply { isFillViewport = true; clipToPadding = false }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(12), dp(16), dp(22))
        }
        scroll.addView(column)
        val path = if (isPicker) pickerPath else folderPath
        val rootFolder = try { store.folder(path) } catch (_: Exception) { store.root }
        val allEntries = store.list(rootFolder)
        val visibleEntries = allEntries.filter {
            when {
                isPicker && fileChooserSelectFolders -> it.isDirectory
                isPicker -> it.isDirectory || DocumentNamePolicy.isPrintable(it.name, it.length())
                librarySearchQuery.isBlank() -> true
                else -> it.name.contains(librarySearchQuery, ignoreCase = true)
            }
        }
        val heading = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        val copy = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        copy.addView(text(
            if (!isPicker) "My Files" else if (fileChooserSelectFolders) "Choose folders" else "Choose files",
            21, R.color.qp_text, true
        ))
        copy.addView(text(
            when {
                !isPicker -> "${allEntries.count { it.isDirectory }} folders · ${allEntries.count { !it.isDirectory }} files"
                fileChooserSelectFolders -> "Select one or more folders. Only files directly inside each folder are added."
                else -> "${visibleEntries.count { !it.isDirectory }} printable files available"
            },
            11, R.color.qp_muted, false
        ).apply { setPadding(0, dp(2), 0, 0) })
        heading.addView(copy, LinearLayout.LayoutParams(0, -2, 1f))
        column.addView(heading, linearParams().apply { bottomMargin = dp(9) })
        if (!isPicker) {
            val folderActions = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            folderActions.addView(button("Scan here", false).apply {
                minHeight = dp(40)
                setOnClickListener { startDocumentScan(path) }
            }, LinearLayout.LayoutParams(0, dp(40), 1f))
            folderActions.addView(button("New folder", false).apply {
                minHeight = dp(40)
                setOnClickListener {
                    nameDialog("New folder", "") { name ->
                        try { store.createFolder(folderPath, name); showFiles() }
                        catch (e: Exception) { toast("Folder not created: ${message(e)}") }
                    }
                }
            }, LinearLayout.LayoutParams(0, dp(40), 1f).apply { marginStart = dp(8) })
            column.addView(folderActions, linearParams().apply { bottomMargin = dp(12) })
        }

        if (!isPicker) {
            val searchRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
            val search = EditText(this).apply {
                hint = "Search this folder"
                maxLines = 1
                setSingleLine(true)
                textSize = 14f
                setTextColor(getColor(R.color.qp_text))
                setHintTextColor(getColor(R.color.qp_muted))
                background = roundedDrawable(getColor(R.color.qp_card), dp(11), getColor(R.color.qp_outline))
                setPadding(dp(12), 0, dp(12), 0)
                setText(librarySearchQuery)
            }
            searchRow.addView(search, LinearLayout.LayoutParams(0, dp(42), 1f))
            searchRow.addView(button("Go", false).apply {
                minHeight = dp(40)
                setOnClickListener { librarySearchQuery = search.text.toString().trim(); showFiles() }
            }, LinearLayout.LayoutParams(dp(54), dp(40)).apply { marginStart = dp(8) })
            column.addView(searchRow, linearParams().apply { bottomMargin = dp(10) })
        }

        addFolderBreadcrumbs(column, path, isPicker)

        if (!isPicker) {
            val actions = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            actions.addView(button("Import files", false).apply {
                minHeight = dp(38)
                setOnClickListener {
                    pendingImportDestinationPath = if (pickerMode) pickerPath else folderPath
                    importFiles.launch(arrayOf("application/pdf", "image/jpeg", "image/png"))
                }
            }, LinearLayout.LayoutParams(0, dp(38), 1f))
            actions.addView(button("Import folder", false).apply {
                minHeight = dp(38)
                setOnClickListener { importTree.launch(null) }
            }, LinearLayout.LayoutParams(0, dp(38), 1f).apply { marginStart = dp(7) })
            column.addView(actions, linearParams().apply { bottomMargin = dp(12) })
        }

        val selectedHere = selectedFilePaths.mapNotNull { runCatching { File(it).canonicalFile }.getOrNull() }.filter { it.isFile && it.exists() }
        val chooserSelection = if (isPicker && fileChooserAllowsMultiple && !fileChooserSelectFolders) selectedChooserFiles() else emptyList()
        val chooserFolderSelection = if (isPicker && fileChooserSelectFolders) selectedChooserFolders() else emptyList()
        if (isPicker && fileChooserSelectFolders) {
            val selectionRow = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(12), dp(8), dp(12), dp(8))
                background = roundedDrawable(getColor(R.color.qp_soft), dp(12))
            }
            selectionRow.addView(text("${chooserFolderSelection.size} folders selected", 12, R.color.qp_text, true),
                LinearLayout.LayoutParams(0, -2, 1f))
            selectionRow.addView(button("Clear", false).apply {
                minHeight = dp(34)
                isEnabled = chooserFolderSelection.isNotEmpty()
                setOnClickListener { chooserSelectedFolderPaths.clear(); refreshLibrary() }
            })
            selectionRow.addView(button("Add folders", true).apply {
                minHeight = dp(34)
                isEnabled = chooserFolderSelection.isNotEmpty()
                setOnClickListener { attachSelectedFolders() }
            }, LinearLayout.LayoutParams(-2, dp(34)).apply { marginStart = dp(6) })
            column.addView(selectionRow, linearParams().apply { bottomMargin = dp(10) })
        } else if (isPicker && fileChooserAllowsMultiple) {
            val selectionRow = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(12), dp(8), dp(12), dp(8))
                background = roundedDrawable(getColor(R.color.qp_soft), dp(12))
            }
            selectionRow.addView(text("${chooserSelection.size} selected", 12, R.color.qp_text, true),
                LinearLayout.LayoutParams(0, -2, 1f))
            selectionRow.addView(button("Clear", false).apply {
                minHeight = dp(34)
                isEnabled = chooserSelection.isNotEmpty()
                setOnClickListener { chooserSelectedFilePaths.clear(); refreshLibrary() }
            })
            selectionRow.addView(button("Attach", true).apply {
                minHeight = dp(34)
                isEnabled = chooserSelection.isNotEmpty() &&
                    PrintBatchSelectionPolicy.validationError(chooserSelection) == null
                setOnClickListener {
                    chooseFiles(chooserSelection, chooserSelection.map(::printBatchGroupName))
                }
            }, LinearLayout.LayoutParams(-2, dp(34)).apply { marginStart = dp(6) })
            column.addView(selectionRow, linearParams().apply { bottomMargin = dp(10) })
        }
        if (!isPicker && selectedHere.isNotEmpty()) {
            val selectionRow = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(12), dp(8), dp(12), dp(8))
                background = roundedDrawable(getColor(R.color.qp_soft), dp(12))
            }
            selectionRow.addView(text("${selectedHere.size} selected", 12, R.color.qp_text, true),
                LinearLayout.LayoutParams(0, -2, 1f))
            selectionRow.addView(button("ZIP", false).apply {
                minHeight = dp(34)
                setOnClickListener { exportSelected(selectedHere) }
            })
            val supportedBatch = selectedHere.size in 2..20 &&
                selectedHere.all {
                    it.extension.lowercase(Locale.ROOT) in setOf("pdf", "jpg", "jpeg", "png") &&
                        it.length() in 1..(20L * 1024L * 1024L)
                } && selectedHere.sumOf { it.length() } <= DocumentBatchComposer.MAX_SOURCE_BYTES
            selectionRow.addView(button("PDF", true).apply {
                minHeight = dp(34)
                isEnabled = supportedBatch
                alpha = if (supportedBatch) 1f else 0.45f
                setOnClickListener { preparePrintBatch(selectedHere) }
            }, LinearLayout.LayoutParams(-2, dp(34)).apply { marginStart = dp(6) })
            column.addView(selectionRow, linearParams().apply { bottomMargin = dp(10) })
            column.addView(text("PDF merge rasterizes pages.", 10, R.color.qp_muted, false)
                .apply { setPadding(dp(2), 0, 0, dp(6)) })
        }

        if (visibleEntries.isEmpty()) {
            column.addView(text(
                when {
                    librarySearchQuery.isNotBlank() && !isPicker -> "No matches"
                    isPicker && fileChooserSelectFolders -> "No folders yet"
                    isPicker -> "No printable files"
                    else -> "This folder is empty"
                },
                13, R.color.qp_muted, false
            ).apply { setPadding(dp(4), dp(24), dp(4), dp(24)) })
        } else {
            addLibraryEntries(column, visibleEntries, isPicker, 0)
        }
        if (!isPicker) {
            val used = android.text.format.Formatter.formatShortFileSize(this, store.storageUsageBytes())
            column.addView(text("$used used on this device", 10, R.color.qp_muted, false)
                .apply { setPadding(dp(2), dp(12), 0, 0) })
        }
        return scroll
    }

    private fun addLibraryEntries(parentView: LinearLayout, entries: List<File>, isPicker: Boolean, depth: Int) {
        entries.forEach { entry ->
            if (!isPicker || entry.isDirectory || (!fileChooserSelectFolders && DocumentNamePolicy.isPrintable(entry.name, entry.length()))) {
                parentView.addView(entryView(entry, isPicker, depth), linearParams())
                if (entry.isDirectory && expandedFolderPaths.contains(entry.canonicalPath) && depth < MAX_TREE_DEPTH) {
                    val children = runCatching { store.list(entry).toList() }.getOrDefault(emptyList())
                    if (children.isNotEmpty()) addLibraryEntries(parentView, children, isPicker, depth + 1)
                }
            }
        }
    }

    private fun entryView(file: File, isPicker: Boolean, depth: Int = 0, showActions: Boolean = true): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(8 + depth * 15), dp(7), dp(5), dp(7))
            minimumHeight = dp(52)
            elevation = dp(3).toFloat()
            background = roundedDrawable(
                if (file.isDirectory && expandedFolderPaths.contains(file.canonicalPath)) getColor(R.color.qp_soft) else getColor(R.color.qp_surface),
                dp(9)
            )
        }
        val icon = ImageView(this).apply {
            setImageResource(fileIconResource(file))
            imageTintList = ColorStateList.valueOf(
                getColor(if (file.isDirectory) R.color.qp_primary else R.color.qp_muted)
            )
            scaleType = ImageView.ScaleType.CENTER_INSIDE
            setPadding(dp(7), dp(7), dp(7), dp(7))
            background = roundedDrawable(
                getColor(if (file.isDirectory) R.color.qp_soft else R.color.qp_surface), dp(10)
            )
        }
        row.addView(icon, LinearLayout.LayoutParams(dp(38), dp(38)))
        val details = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(10), 0, dp(4), 0)
        }
        details.addView(text(file.name, 13, R.color.qp_text, true).apply {
            maxLines = 1
            ellipsize = android.text.TextUtils.TruncateAt.END
        })
        val info = if (file.isDirectory) {
            val count = runCatching { store.list(file).size }.getOrDefault(0)
            "$count items"
        } else android.text.format.Formatter.formatShortFileSize(this@MainActivity, file.length())
        details.addView(text(info, 10, R.color.qp_muted, false).apply { setPadding(0, dp(2), 0, 0) })
        row.addView(details, LinearLayout.LayoutParams(0, -2, 1f))

        if (file.isDirectory && isPicker) {
            val expanded = expandedFolderPaths.contains(file.canonicalPath)
            val chevron = ImageView(this).apply {
                setImageResource(R.drawable.ic_chevron_right)
                imageTintList = ColorStateList.valueOf(getColor(R.color.qp_muted))
                rotation = if (expanded) 90f else 0f
                contentDescription = if (expanded) "Collapse ${file.name}" else "Expand ${file.name}"
                setPadding(dp(7), dp(7), dp(7), dp(7))
                isClickable = true
                isFocusable = true
                setOnClickListener {
                    if (!expandedFolderPaths.add(file.canonicalPath)) expandedFolderPaths.remove(file.canonicalPath)
                    refreshLibrary()
                }
            }
            row.addView(chevron, LinearLayout.LayoutParams(dp(38), dp(40)))
        } else if (isPicker && fileChooserSelectFolders) {
            val selected = CheckBox(this).apply {
                isChecked = chooserSelectedFolderPaths.contains(file.canonicalPath)
                contentDescription = "Select folder " + file.name
                buttonTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
                setOnCheckedChangeListener { _, checked -> setChooserFolderSelected(file, checked) }
            }
            row.addView(selected, LinearLayout.LayoutParams(dp(40), dp(40)))
        } else if (isPicker && fileChooserAllowsMultiple) {
            val selected = CheckBox(this).apply {
                isChecked = chooserSelectedFilePaths.contains(file.canonicalPath)
                contentDescription = "Select " + file.name
                setOnCheckedChangeListener { _, checked -> setChooserFileSelected(file, checked) }
            }
            row.addView(selected, LinearLayout.LayoutParams(dp(40), dp(40)))
        } else if (!isPicker && showActions) {
            val selected = CheckBox(this).apply {
                isChecked = selectedFilePaths.contains(file.canonicalPath)
                contentDescription = "Select " + file.name
                buttonTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
                setOnCheckedChangeListener { _, checked ->
                    if (checked) selectedFilePaths.add(file.canonicalPath) else selectedFilePaths.remove(file.canonicalPath)
                    refreshLibrary()
                }
            }
            row.addView(selected, LinearLayout.LayoutParams(dp(38), dp(40)))
        }

        if (!isPicker && showActions) {
            val more = ImageView(this).apply {
                setImageResource(R.drawable.ic_more_vertical)
                imageTintList = ColorStateList.valueOf(getColor(R.color.qp_muted))
                contentDescription = "More options for " + file.name
                setPadding(dp(8), dp(8), dp(8), dp(8))
                isClickable = true
                isFocusable = true
                setOnClickListener { showEntryMenu(this, file) }
            }
            row.addView(more, LinearLayout.LayoutParams(dp(36), dp(40)))
        }
        row.setOnClickListener {
            when {
                file.isDirectory && isPicker && fileChooserSelectFolders ->
                    setChooserFolderSelected(file, !chooserSelectedFolderPaths.contains(file.canonicalPath))
                file.isDirectory -> {
                    if (isPicker) pickerPath = store.pathOf(file) else folderPath = store.pathOf(file)
                    refreshLibrary(preserveScroll = false)
                }
                isPicker && fileChooserAllowsMultiple -> setChooserFileSelected(
                    file, !chooserSelectedFilePaths.contains(file.canonicalPath)
                )
                isPicker -> chooseFile(file)
                else -> openDocument(file)
            }
        }
        if (!isPicker && showActions) {
            row.setOnLongClickListener {
                val clip = ClipData.newPlainText("SmartPrint item order", file.name)
                row.startDragAndDrop(clip, FloatingItemDragShadowBuilder(row), file.canonicalPath, 0)
            }
            row.setOnDragListener { _, event ->
                when (event.action) {
                    android.view.DragEvent.ACTION_DRAG_STARTED ->
                        event.clipDescription?.hasMimeType(ClipDescription.MIMETYPE_TEXT_PLAIN) == true
                    android.view.DragEvent.ACTION_DRAG_ENTERED -> { row.alpha = 0.65f; true }
                    android.view.DragEvent.ACTION_DRAG_EXITED -> { row.alpha = 1f; true }
                    android.view.DragEvent.ACTION_DROP -> {
                        val sourcePath = event.localState as? String
                        val source = sourcePath?.let(::File)
                        if (source != null) {
                            try {
                                if (store.moveBefore(source, file)) {
                                    selectedFilePaths.remove(source.canonicalPath)
                                    showFiles()
                                }
                            } catch (e: Exception) { toast("Could not reorder: " + message(e)) }
                        }
                        true
                    }
                    android.view.DragEvent.ACTION_DRAG_ENDED -> { row.alpha = 1f; true }
                    else -> true
                }
            }
        }
        return row
    }

    private fun fileIconResource(file: File): Int {
        if (file.isDirectory) return R.drawable.ic_folder
        return when (file.extension.lowercase(Locale.ROOT)) {
            "pdf" -> R.drawable.ic_file_pdf
            "jpg", "jpeg", "png", "webp", "bmp", "gif" -> R.drawable.ic_file_image
            else -> R.drawable.ic_file
        }
    }

    private fun showEntryMenu(anchor: View, file: File) {
        val actions = mutableListOf<Pair<String, () -> Unit>>()
        if (file.isDirectory && runCatching { store.list(file).isNotEmpty() }.getOrDefault(false)) {
            val isExpanded = expandedFolderPaths.contains(file.canonicalPath)
            actions += (if (isExpanded) "Collapse in list" else "Expand in list") to {
                if (isExpanded) expandedFolderPaths.remove(file.canonicalPath)
                else expandedFolderPaths.add(file.canonicalPath)
                refreshLibrary()
            }
        }
        if (!file.isDirectory) actions += "Open" to { openDocument(file) }
        if (!file.isDirectory && supportsInAppViewer(file)) actions += "Open with another app" to { openDocumentExternally(file) }
        if (!file.isDirectory && file.extension.equals("pdf", true)) {
            actions += "Edit PDF" to { showPdfEditor(file) }
        }
        actions += "Rename" to {
            nameDialog("Rename", file.name) { name ->
                try {
                    store.rename(file, name)
                    selectedFilePaths.remove(file.canonicalPath)
                    expandedFolderPaths.remove(file.canonicalPath)
                    showFiles()
                } catch (e: Exception) { toast("Rename failed: " + message(e)) }
            }
        }
        actions += (if (file.isDirectory) "Export folder" else "Export") to {
            if (file.isDirectory) { exportFolder = file; exportTree.launch(null) }
            else { exportFile = file; exportOne.launch(file.name) }
        }
        actions += "Move to folder" to { showMoveDestinationPicker(file) }
        val siblings = file.parentFile?.let { store.list(it) }.orEmpty()
        val position = siblings.indexOfFirst { it.canonicalPath == file.canonicalPath }
        if (position > 0) actions += "Move earlier" to {
            try { store.reorder(file, -1); showFiles() }
            catch (e: Exception) { toast("Reorder failed: " + message(e)) }
        }
        if (position >= 0 && position < siblings.lastIndex) actions += "Move later" to {
            try { store.reorder(file, 1); showFiles() }
            catch (e: Exception) { toast("Reorder failed: " + message(e)) }
        }
        if (!file.isDirectory) actions += "Share" to { shareDocument(file) }
        actions += "Delete" to {
            showConfirmSheet(
                if (file.isDirectory) "Delete folder?" else "Delete file?",
                "This removes the local copy.",
                "Delete",
                destructive = true
            ) {
                try {
                    store.delete(file)
                    selectedFilePaths.remove(file.canonicalPath)
                    expandedFolderPaths.remove(file.canonicalPath)
                    showFiles()
                } catch (e: Exception) { toast("Delete failed: " + message(e)) }
            }
        }
        showOptionSheet(if (file.isDirectory) "Folder actions" else "File actions", actions)
    }

    private fun showMoveDestinationPicker(file: File) {
        val sourcePath = runCatching { store.pathOf(file) }.getOrDefault("")
        val currentParent = file.parentFile?.let { store.pathOf(it) }.orEmpty()
        val destinations = mutableListOf("")
        store.root.walkTopDown()
            .filter { it.isDirectory && it.canonicalPath != store.root.canonicalPath }
            .take(MAX_TREE_FILES)
            .forEach { directory ->
                val path = runCatching { store.pathOf(directory) }.getOrNull() ?: return@forEach
                if (path != currentParent &&
                    !(file.isDirectory && (path == sourcePath || path.startsWith("$sourcePath/")))) {
                    destinations.add(path)
                }
            }
        val labels = destinations.map { if (it.isBlank()) "My Files" else it.replace("/", "  /  ") }
        val actions = labels.mapIndexed { index, label ->
            label to {
                try {
                    store.move(file, destinations[index])
                    selectedFilePaths.remove(file.canonicalPath)
                    expandedFolderPaths.remove(file.canonicalPath)
                    showFiles()
                } catch (e: Exception) { toast("Move failed: " + message(e)) }
            }
        }
        showOptionSheet("Move to folder", actions)
    }

    private fun exportSelected(files: List<File>) {
        if (files.isEmpty()) return
        archiveFiles = files.toList()
        selectedFilePaths.clear()
        exportArchive.launch("Smart Print files ${SimpleDateFormat("yyyyMMdd-HHmmss", Locale.ROOT).format(Date())}.zip")
    }

    private fun preparePrintBatch(files: List<File>) {
        val orderedFiles = files.toList()
        val output = File(cacheDir, "SmartPrint-batch-${System.currentTimeMillis()}.pdf")
        val destinationPath = orderedFiles.firstOrNull()?.parentFile?.let { store.pathOf(it) }.orEmpty()
        toast("Preparing your ordered print batch…")
        Thread {
            try {
                val pages = DocumentBatchComposer.compose(orderedFiles, output)
                val name = "Smart Print print batch ${SimpleDateFormat("yyyyMMdd-HHmmss", Locale.ROOT).format(Date())}.pdf"
                val saved = FileInputStream(output).use { store.importFile(it, name, destinationPath) }
                runOnUiThread {
                    folderPath = destinationPath
                    selectedFilePaths.clear()
                    if (portalUrl != null) showPrint() else showFiles()
                    toast("Prepared ${orderedFiles.size} documents as one $pages-page PDF: ${saved.name}. Attach this PDF to one Smart Print print order.")
                }
            } catch (e: Exception) {
                runOnUiThread { toast("Batch preparation failed: ${message(e)}") }
            } finally {
                output.delete()
            }
        }.start()
    }

    private fun showPdfEditor(file: File) {
        try {
            require(file.isFile && file.extension.equals("pdf", true)) {
                "Only saved PDFs can be edited here."
            }
            val pages = DocumentBatchComposer.pageEdits(file).toMutableList()
            editingPdf = file
            renderPdfEditor(file, pages)
        } catch (e: Exception) {
            toast("Could not open the PDF editor: ${message(e)}")
        }
    }

    private fun renderPdfEditor(file: File, pages: MutableList<PdfPageEdit>) {
        pickerMode = false
        toolbar.title = "Edit PDF"
        toolbar.setNavigationIcon(android.R.drawable.ic_media_previous)
        toolbar.setNavigationOnClickListener { showFiles() }
        setBottomNavigationVisible(false)
        content.removeAllViews()

        val scroll = ScrollView(this).apply { isFillViewport = true; clipToPadding = false }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(14), dp(16), dp(24))
        }
        scroll.addView(column)

        column.addView(text(file.name, 15, R.color.qp_text, true))
        column.addView(text(
            "Rasterized copy · Original kept.",
            11, R.color.qp_muted, false
        ).apply { setPadding(0, dp(4), 0, dp(12)) })
        column.addView(button("Save copy", true).apply {
            setOnClickListener { savePdfEdits(file, pages.toList()) }
        }, linearParams().apply { bottomMargin = dp(12) })

        pages.forEachIndexed { position, page ->
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(9), dp(8), dp(7), dp(8))
                background = roundedDrawable(getColor(R.color.qp_card), dp(11), getColor(R.color.qp_outline))
            }
            val pageHeader = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            val pageMark = TextView(this).apply {
                text = (position + 1).toString()
                textSize = 13f
                gravity = Gravity.CENTER
                setTypeface(typeface, android.graphics.Typeface.BOLD)
                setTextColor(getColor(R.color.qp_primary))
                background = roundedDrawable(getColor(R.color.qp_soft), dp(9))
            }
            pageHeader.addView(pageMark, LinearLayout.LayoutParams(dp(36), dp(40)))
            val meta = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(10), 0, dp(6), 0)
            }
            meta.addView(text("Page ${position + 1}", 12, R.color.qp_text, true))
            meta.addView(text(
                if (page.rotationDegrees == 0) "Original" else "${page.rotationDegrees}°",
                10, R.color.qp_muted, false
            ).apply { setPadding(0, dp(2), 0, 0) })
            pageHeader.addView(meta, LinearLayout.LayoutParams(0, -2, 1f))
            row.addView(pageHeader, linearParams())

            // Keep every page action on its own full-width row. A horizontal
            // action strip below the metadata avoids clipping the final action
            // on narrow phones while preserving generous touch targets.
            val actions = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            actions.addView(iconActionButton(R.drawable.ic_preview, "Preview page ${position + 1}").apply {
                setOnClickListener {
                    try {
                        showImageSheet("Page ${position + 1}", DocumentBatchComposer.renderPagePreview(file, page))
                    } catch (e: Exception) {
                        toast("Preview failed: ${message(e)}")
                    }
                }
            }, LinearLayout.LayoutParams(0, dp(42), 1f))
            actions.addView(iconActionButton(R.drawable.ic_rotate, "Rotate page ${position + 1}").apply {
                setOnClickListener {
                    pages[position] = page.copy(rotationDegrees = (page.rotationDegrees + 90) % 360)
                    renderPdfEditor(file, pages)
                }
            }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(5) })
            actions.addView(iconActionButton(R.drawable.ic_arrow_up, "Move page ${position + 1} up").apply {
                isEnabled = position > 0
                alpha = if (position > 0) 1f else 0.4f
                setOnClickListener {
                    if (position > 0) {
                        val value = pages.removeAt(position)
                        pages.add(position - 1, value)
                        renderPdfEditor(file, pages)
                    }
                }
            }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(5) })
            actions.addView(iconActionButton(R.drawable.ic_arrow_down, "Move page ${position + 1} down").apply {
                isEnabled = position < pages.lastIndex
                alpha = if (position < pages.lastIndex) 1f else 0.4f
                setOnClickListener {
                    if (position < pages.lastIndex) {
                        val value = pages.removeAt(position)
                        pages.add(position + 1, value)
                        renderPdfEditor(file, pages)
                    }
                }
            }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(5) })
            actions.addView(iconActionButton(R.drawable.ic_delete, "Remove page ${position + 1}").apply {
                setOnClickListener {
                    if (pages.size <= 1) {
                        toast("A PDF needs one page.")
                    } else {
                        pages.removeAt(position)
                        renderPdfEditor(file, pages)
                    }
                }
            }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginStart = dp(5) })
            row.addView(actions, LinearLayout.LayoutParams(-1, dp(42)).apply { topMargin = dp(8) })
            row.setOnLongClickListener {
                showConfirmSheet("Remove page?", "The original stays unchanged.", "Remove", destructive = true) {
                    if (pages.size <= 1) {
                        toast("A PDF needs one page.")
                    } else {
                        pages.removeAt(position)
                        renderPdfEditor(file, pages)
                    }
                }
                true
            }
            column.addView(row, linearParams().apply { bottomMargin = dp(5) })
        }
        column.addView(text("Use the page controls to preview, rotate, reorder or remove pages.", 10, R.color.qp_muted, false)
            .apply { setPadding(dp(2), dp(8), 0, 0) })
        content.addView(scroll, frameParams())
    }

    private fun savePdfEdits(source: File, edits: List<PdfPageEdit>) {
        val output = File(cacheDir, "SmartPrint-edited-${System.currentTimeMillis()}.pdf")
        val destinationPath = source.parentFile?.let { store.pathOf(it) }.orEmpty()
        toast("Saving a new edited PDF…")
        Thread {
            try {
                val pageCount = DocumentBatchComposer.editPdf(source, edits, output)
                val editedName = "${source.nameWithoutExtension}_edited.pdf"
                val saved = FileInputStream(output).use { store.importFile(it, editedName, destinationPath) }
                runOnUiThread {
                    folderPath = destinationPath
                    editingPdf = null
                    showFiles()
                    toast("Saved ${saved.name} with $pageCount pages. The original PDF is unchanged.")
                }
            } catch (e: Exception) {
                runOnUiThread { toast("Could not save edited PDF: ${message(e)}") }
            } finally {
                output.delete()
            }
        }.start()
    }

    private fun iconActionButton(iconResource: Int, description: String): ImageView =
        ImageView(this).apply {
            setImageResource(iconResource)
            imageTintList = ColorStateList.valueOf(getColor(R.color.qp_text))
            contentDescription = description
            scaleType = ImageView.ScaleType.CENTER
            setPadding(dp(9), dp(9), dp(9), dp(9))
            background = roundedDrawable(
                getColor(R.color.qp_surface), dp(11), getColor(R.color.qp_outline)
            )
            isClickable = true
            isFocusable = true
            minimumWidth = dp(42)
            minimumHeight = dp(42)
        }

    private fun sheetRoot(title: String, subtitle: String? = null): Pair<BottomSheetDialog, LinearLayout> {
        val dialog = BottomSheetDialog(this)
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(10), dp(20), dp(22))
            background = SmartPrintGlass.surface(this@MainActivity, 22)
        }
        val handle = View(this).apply {
            background = roundedDrawable(getColor(R.color.qp_outline), dp(2))
        }
        root.addView(handle, LinearLayout.LayoutParams(dp(36), dp(4)).apply {
            gravity = Gravity.CENTER_HORIZONTAL
            bottomMargin = dp(15)
        })
        root.addView(text(title, 18, R.color.qp_text, true))
        subtitle?.takeIf { it.isNotBlank() }?.let {
            root.addView(text(it, 12, R.color.qp_muted, false).apply {
                setPadding(0, dp(5), 0, dp(12))
            })
        } ?: run { root.addView(View(this), LinearLayout.LayoutParams(1, dp(10))) }
        dialog.setContentView(root)
        return dialog to root
    }

    private fun showOptionSheet(title: String, actions: List<Pair<String, () -> Unit>>) {
        val (dialog, root) = sheetRoot(title)
        val scroll = ScrollView(this).apply { isFillViewport = false }
        val list = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }
        actions.forEach { (label, action) ->
            val item = button(label, false).apply {
                minHeight = dp(46)
                gravity = Gravity.CENTER_VERTICAL
                setOnClickListener { dialog.dismiss(); action() }
            }
            list.addView(item, linearParams().apply { bottomMargin = dp(6) })
        }
        scroll.addView(list)
        root.addView(scroll, LinearLayout.LayoutParams(-1, -2))
        root.addView(button("Cancel", false).apply {
            minHeight = dp(42)
            setOnClickListener { dialog.dismiss() }
        }, linearParams().apply { topMargin = dp(6) })
        dialog.show()
    }

    private fun showConfirmSheet(
        title: String,
        detail: String,
        confirmLabel: String,
        destructive: Boolean = false,
        onConfirm: () -> Unit
    ) {
        val (dialog, root) = sheetRoot(title, detail)
        val actions = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        actions.addView(button("Cancel", false).apply {
            minHeight = dp(44)
            setOnClickListener { dialog.dismiss() }
        }, LinearLayout.LayoutParams(0, dp(44), 1f))
        actions.addView(button(confirmLabel, true).apply {
            minHeight = dp(44)
            if (destructive) {
                backgroundTintList = ColorStateList.valueOf(getColor(R.color.qp_danger))
                setTextColor(getColor(R.color.qp_danger_content))
            }
            setOnClickListener { dialog.dismiss(); onConfirm() }
        }, LinearLayout.LayoutParams(0, dp(44), 1f).apply { marginStart = dp(8) })
        root.addView(actions, linearParams().apply { topMargin = dp(12) })
        dialog.show()
    }

    private fun showImageSheet(title: String, bitmap: android.graphics.Bitmap) {
        val (dialog, root) = sheetRoot(title)
        val preview = ImageView(this).apply {
            setImageBitmap(bitmap)
            adjustViewBounds = true
            scaleType = ImageView.ScaleType.FIT_CENTER
            setPadding(dp(4), dp(6), dp(4), dp(6))
        }
        root.addView(preview, LinearLayout.LayoutParams(-1, -2))
        root.addView(button("Close", false).apply {
            minHeight = dp(42)
            setOnClickListener { dialog.dismiss() }
        }, linearParams().apply { topMargin = dp(8) })
        dialog.setOnDismissListener { if (!bitmap.isRecycled) bitmap.recycle() }
        dialog.show()
    }

    private fun nameDialog(title: String, initial: String, onSave: (String) -> Unit) {
        val (dialog, root) = sheetRoot(title)
        val input = EditText(this).apply {
            setSingleLine(true)
            setText(initial)
            setSelection(text.length)
            hint = "Name"
            textSize = 14f
            setTextColor(getColor(R.color.qp_text))
            setHintTextColor(getColor(R.color.qp_muted))
            background = roundedDrawable(getColor(R.color.qp_surface), dp(11), getColor(R.color.qp_outline))
            setPadding(dp(12), dp(11), dp(12), dp(11))
        }
        root.addView(input, linearParams())
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("Cancel", false).apply {
            minHeight = dp(44)
            setOnClickListener { dialog.dismiss() }
        }, LinearLayout.LayoutParams(0, dp(44), 1f))
        actions.addView(button("Save", true).apply {
            minHeight = dp(44)
            setOnClickListener {
                val value = input.text.toString().trim()
                if (value.isBlank()) {
                    input.error = "Enter a name"
                    return@setOnClickListener
                }
                dialog.dismiss()
                onSave(value)
            }
        }, LinearLayout.LayoutParams(0, dp(44), 1f).apply { marginStart = dp(8) })
        root.addView(actions, linearParams().apply { topMargin = dp(12) })
        dialog.show()
    }

    private fun roundedDrawable(fillColor: Int, radiusDp: Int, strokeColor: Int? = null): android.graphics.drawable.GradientDrawable =
        android.graphics.drawable.GradientDrawable().apply {
            setColor(fillColor)
            cornerRadius = dp(radiusDp).toFloat()
            strokeColor?.let { setStroke(dp(1), it) }
        }

    private fun ensureWebView(): WebView {
        webView?.let { return it }
        val view = WebView(this).apply {
            setBackgroundColor(getColor(R.color.qp_surface))
            isVerticalScrollBarEnabled = false
            isHorizontalScrollBarEnabled = false
            overScrollMode = View.OVER_SCROLL_NEVER
        }
        view.settings.userAgentString = "${view.settings.userAgentString} SmartPrintAndroid/1"
        // Never trust the QR-selected route as a credential-sharing origin.
        // The bridge is pinned to the configured service origin for this build.
        val trustedPage = Uri.parse(BuildConfig.SERVICE_BASE_URL)
        val trustedOrigin = "${trustedPage.scheme}://${trustedPage.authority}"
        if (trustedPage.scheme in setOf("https", "http") && trustedPage.host != null &&
            WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)) {
            WebViewCompat.addWebMessageListener(
                view,
                "SmartPrintNative",
                setOf(trustedOrigin)
            ) { _, message, sourceOrigin, isMainFrame, replyProxy ->
                // Only the fixed configured origin and the top-level document may use this bridge.
                if (isMainFrame &&
                    sourceOrigin.scheme == trustedPage.scheme &&
                    sourceOrigin.host.equals(trustedPage.host, true) &&
                    sourceOrigin.port == trustedPage.port) {
                    Thread {
                        val response = try {
                            val parts = message.data.orEmpty().split("|", limit = 3)
                            when {
                                message.data == "getCustomerAccessToken" -> {
                                    var auth = customerAccountStore.read()
                                    if (auth != null && CustomerAccountClient.accessTokenIsExpired(auth)) {
                                        auth = CustomerAccountClient.refresh(BuildConfig.SERVICE_BASE_URL, auth)
                                        customerAccountStore.save(auth)
                                    }
                                    auth?.accessToken.orEmpty()
                                }
                                parts.size == 2 && parts[0] == "getCustomerPrintSession" && isActiveStationCode(parts[1]) ->
                                    customerPrintSessionStore.read(parts[1]).orEmpty()
                                parts.size == 3 && parts[0] == "saveCustomerPrintSession" && isActiveStationCode(parts[1]) -> {
                                    customerPrintSessionStore.save(parts[1], parts[2]); "OK"
                                }
                                parts.size == 2 && parts[0] == "clearCustomerPrintSession" && isActiveStationCode(parts[1]) -> {
                                    customerPrintSessionStore.clear(parts[1]); "OK"
                                }
                                else -> ""
                            }
                        } catch (_: Exception) { "" }
                        runOnUiThread {
                            if (!isFinishing && !isDestroyed) replyProxy.postMessage(response)
                        }
                    }.start()
                }
            }
        }
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
                        "text/plain", "UTF-8", 403, "Blocked by Smart Print", emptyMap(),
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
                fileChooserSelectFolders = params.acceptTypes.any { accepted ->
                    accepted.split(',').any { it.trim().equals(FOLDER_PICKER_ACCEPT, ignoreCase = true) }
                }
                fileChooserAllowsMultiple = fileChooserSelectFolders ||
                    params.mode == FileChooserParams.MODE_OPEN_MULTIPLE
                chooserSelectedFilePaths.clear()
                chooserSelectedFolderPaths.clear()
                showPicker()
                return true
            }
        }
        webView = view
        return view
    }

    private fun isActiveStationCode(candidate: String): Boolean {
        val activeCode = portalUrl?.let { Uri.parse(it).lastPathSegment } ?: return false
        return candidate.matches(Regex("^[A-Za-z0-9_-]{32}$")) && candidate == activeCode
    }

    private fun allowedAgentUpload(uri: Uri): Boolean =
        LocalAgentUploadPolicy.allows(uri.scheme.orEmpty(), uri.host.orEmpty(), uri.port, uri.path.orEmpty())

    private fun allowedLocalDevelopmentEndpoint(uri: Uri): Boolean =
        BuildConfig.DEBUG &&
            uri.scheme.equals("http", true) &&
            uri.port in setOf(3000, 8080) &&
            uri.host.equals(portalHost, true) &&
            LocalAgentUploadPolicy.isPrivateIpv4Host(uri.host.orEmpty())

    private fun switchShop() {
        if (portalUrl == null) { scanQr(); return }
        showConfirmSheet(
            "Switch shop?",
            "Current orders stay saved.",
            "Scan new shop"
        ) { scanQr() }
    }

    private fun scanQr() {
        navigation.menu.findItem(SCAN_PRINT).isChecked = true
        scannerLauncher.launch(Intent(this, QrScannerActivity::class.java))
    }

    private fun startDocumentScan(destinationPath: String? = null) {
        pendingScanNeedsDestination = destinationPath == null
        pendingScanDestinationPath = destinationPath.orEmpty()
        val options = GmsDocumentScannerOptions.Builder()
            .setGalleryImportAllowed(true)
            .setPageLimit(50)
            .setResultFormats(GmsDocumentScannerOptions.RESULT_FORMAT_PDF)
            .setScannerMode(GmsDocumentScannerOptions.SCANNER_MODE_FULL)
            .build()
        GmsDocumentScanning.getClient(options)
            .getStartScanIntent(this)
            .addOnSuccessListener { sender ->
                documentScanLauncher.launch(IntentSenderRequest.Builder(sender).build())
            }
            .addOnFailureListener { error ->
                pendingScanNeedsDestination = false
                pendingScanDestinationPath = ""
                toast("Document scanner is unavailable on this device. You can still import photos or PDFs. ${message(error)}")
            }
    }

    private fun showScanDestinationPicker(scan: GmsDocumentScanningResult) {
        val (dialog, root) = sheetRoot("Save scanned document", "Choose where this scan should stay in My Files.")
        root.addView(button("My Files", true).apply {
            minHeight = dp(46)
            setOnClickListener { dialog.dismiss(); saveDocumentScan(scan, "") }
        }, linearParams().apply { bottomMargin = dp(7) })
        root.addView(button("Create a new folder", false).apply {
            minHeight = dp(42)
            setOnClickListener {
                nameDialog("New folder for this scan", "") { name ->
                    try {
                        val folder = store.createFolder("", name)
                        dialog.dismiss()
                        saveDocumentScan(scan, store.pathOf(folder))
                    } catch (error: Exception) {
                        toast("Folder not created: ${message(error)}")
                    }
                }
            }
        }, linearParams().apply { bottomMargin = dp(8) })
        val folders = store.root.walkTopDown()
            .filter { it.isDirectory && it.canonicalPath != store.root.canonicalPath }
            .sortedBy { runCatching { store.pathOf(it).lowercase(Locale.ROOT) }.getOrDefault(it.name.lowercase(Locale.ROOT)) }
            .toList()
        if (folders.isEmpty()) {
            root.addView(text("No saved folders yet. You can create one above.", 12, R.color.qp_muted, false)
                .apply { setPadding(dp(2), dp(8), 0, dp(8)) })
        } else {
            root.addView(text("Existing folders", 12, R.color.qp_muted, true)
                .apply { setPadding(dp(2), dp(4), 0, dp(7)) })
            val scroll = ScrollView(this).apply { isFillViewport = false }
            val list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            folders.forEach { folder ->
                val path = store.pathOf(folder)
                val label = "My Files  ›  " + path.replace("/", "  ›  ")
                list.addView(button(label, false).apply {
                    minHeight = dp(44)
                    gravity = Gravity.START or Gravity.CENTER_VERTICAL
                    setOnClickListener { dialog.dismiss(); saveDocumentScan(scan, path) }
                }, linearParams().apply { bottomMargin = dp(5) })
            }
            scroll.addView(list)
            root.addView(scroll, LinearLayout.LayoutParams(-1, dp(260)))
        }
        root.addView(button("Cancel scan", false).apply {
            minHeight = dp(40)
            setOnClickListener {
                dialog.dismiss()
                pendingScanNeedsDestination = false
                pendingScanDestinationPath = ""
                toast("Scan not saved.")
            }
        }, linearParams().apply { topMargin = dp(8) })
        dialog.show()
    }

    private fun saveDocumentScan(scan: GmsDocumentScanningResult, destinationPath: String) {
        try {
            val destination = store.folder(destinationPath)
            val pdf = scan.getPdf() ?: error("The scanner did not return a PDF.")
            val input = contentResolver.openInputStream(pdf.getUri())
                ?: error("Scanned PDF is unavailable.")
            val saved = input.use { store.importFile(it, "Scanned document.pdf", destinationPath) }
            pendingScanNeedsDestination = false
            pendingScanDestinationPath = ""
            folderPath = store.pathOf(destination)
            showFiles()
            val location = if (destinationPath.isBlank()) "My Files" else "My Files / " + destinationPath.replace("/", " / ")
            toast("Saved ${saved.name} in $location.")
        } catch (error: Exception) {
            toast("Could not save the scan: ${message(error)}")
            showScanDestinationPicker(scan)
        }
    }

    private fun confirmShop(link: StationQrLink) {
        val detail = if (link.url.startsWith("http://")) {
            "LOCAL TEST · Trusted shop Wi-Fi only"
        } else {
            "Secure shop session"
        }
        showConfirmSheet("Connect to shop?", detail, "Connect") {
            portalHost = link.host
            portalUrl = link.url
            getSharedPreferences("smartprint.customer.last-shop", MODE_PRIVATE).edit()
                .putString("portalUrl", link.url)
                .putString("portalHost", link.host)
                .apply()
            toolbar.menu.findItem(SWITCH_SHOP)?.isVisible = true
            ensureWebView().loadUrl(link.url)
            showPrint()
        }
    }

    private fun selectedChooserFiles(): List<File> = chooserSelectedFilePaths.mapNotNull { path ->
        runCatching { File(path).canonicalFile }.getOrNull()
    }.filter { it.isFile }

    private fun selectedChooserFolders(): List<File> = chooserSelectedFolderPaths.mapNotNull { path ->
        runCatching { File(path).canonicalFile }.getOrNull()
    }.filter { it.isDirectory }

    private fun setChooserFolderSelected(folder: File, checked: Boolean) {
        val path = folder.canonicalPath
        if (checked) chooserSelectedFolderPaths.add(path) else chooserSelectedFolderPaths.remove(path)
        refreshLibrary()
    }

    private fun attachSelectedFolders() {
        val selectedFolders = selectedChooserFolders()
        if (selectedFolders.isEmpty()) { toast("Select at least one folder."); return }
        val documents = selectedFolders.flatMap { folder ->
            val groupName = store.pathOf(folder).replace("/", " - ").take(80).ifBlank { "Loose files" }
            store.list(folder).filter { file ->
                file.isFile && DocumentNamePolicy.isPrintable(file.name, file.length())
            }.map { file -> file to groupName }
        }.distinctBy { it.first.canonicalPath }
        if (documents.isEmpty()) {
            toast("No printable PDF, JPG or PNG files were found directly inside these folders. Nested folders are not included.")
            return
        }
        val error = PrintBatchSelectionPolicy.validationError(documents.map { it.first })
        if (error != null) { toast(error); return }
        if (selectedFolders.any { folder -> store.list(folder).any { it.isDirectory } }) {
            toast("Only files directly inside each selected folder are included. Nested folders are skipped.")
        }
        chooseFiles(documents.map { it.first }, documents.map { it.second })
    }

    private fun setChooserFileSelected(file: File, checked: Boolean) {
        val path = file.canonicalPath
        if (!checked) {
            chooserSelectedFilePaths.remove(path)
            refreshLibrary()
            return
        }
        val candidate = selectedChooserFiles().filterNot { it.canonicalPath == path } + file
        val error = PrintBatchSelectionPolicy.validationError(candidate)
        if (error != null) {
            toast(error)
            return
        }
        chooserSelectedFilePaths.add(path)
        refreshLibrary()
    }

    private fun printBatchGroupName(file: File): String {
        val parentPath = file.parentFile?.let { runCatching { store.pathOf(it) }.getOrDefault("") }.orEmpty()
        return parentPath.replace("/", " - ").take(80).ifBlank { "Loose files" }
    }

    private fun chooseFile(file: File) = chooseFiles(listOf(file), listOf(printBatchGroupName(file)))

    private fun chooseFiles(files: List<File>, groupNames: List<String>? = null) {
        val callback = fileCallback
        if (callback == null) { toast("Open a document field in the print page first."); return }
        val error = PrintBatchSelectionPolicy.validationError(files)
        if (error != null) { toast(error); return }
        if (!fileChooserAllowsMultiple && files.size != 1) {
            toast("This print field accepts one document at a time.")
            return
        }
        try {
            val uris = files.map { file ->
                FileProvider.getUriForFile(this, "$packageName.documents", file)
            }.toTypedArray()
            val safeGroupNames = groupNames?.takeIf { it.size == files.size }
                ?: files.map(::printBatchGroupName)
            val metadataScript = "window.__smartPrintNativePickerGroups = " +
                JSONArray().apply { safeGroupNames.forEach { put(it) } }.toString() + ";"
            val deliverSelection = {
                fileCallback = null
                chooserSelectedFilePaths.clear()
                chooserSelectedFolderPaths.clear()
                fileChooserAllowsMultiple = false
                fileChooserSelectFolders = false
                showPrint()
                val activeWebView = webView
                if (activeWebView != null) {
                    activeWebView.evaluateJavascript(metadataScript) { callback.onReceiveValue(uris) }
                } else {
                    callback.onReceiveValue(uris)
                }
            }
            deliverSelection()
        } catch (e: Exception) {
            cancelPicker()
            toast("Could not attach the selected documents: " + message(e))
        }
    }

    private fun cancelPicker() {
        fileCallback?.onReceiveValue(null)
        fileCallback = null
        chooserSelectedFilePaths.clear()
        chooserSelectedFolderPaths.clear()
        fileChooserAllowsMultiple = false
        fileChooserSelectFolders = false
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

    private fun supportsInAppViewer(file: File): Boolean {
        if (!file.isFile) return false
        return file.extension.lowercase(Locale.ROOT) in setOf(
            "pdf", "jpg", "jpeg", "png", "webp", "bmp", "gif"
        )
    }

    private fun showDocumentViewer(file: File, startPosition: Int? = null) {
        if (!supportsInAppViewer(file)) {
            openDocumentExternally(file)
            return
        }
        if (!file.exists() || !file.isFile) {
            toast("This document is no longer available.")
            return
        }

        documentViewer?.dispose()
        viewingDocument = file
        pickerMode = false
        profileVisible = false
        toolbar.title = ""
        toolbar.setNavigationIcon(android.R.drawable.ic_media_previous)
        toolbar.navigationContentDescription = "Back to My Files"
        toolbar.setNavigationOnClickListener { closeDocumentViewer() }
        toolbar.menu.findItem(SWITCH_SHOP)?.isVisible = false
        toolbar.menu.findItem(OPEN_WITH)?.isVisible = true
        accountAction.visibility = View.GONE
        setBottomNavigationVisible(false)
        content.removeAllViews()

        val images = if (file.extension.equals("pdf", true)) {
            emptyList()
        } else {
            val siblings = file.parentFile?.let { folder ->
                runCatching {
                    store.list(folder).filter { candidate ->
                        candidate.isFile && candidate.extension.lowercase(Locale.ROOT) in
                            setOf("jpg", "jpeg", "png", "webp", "bmp", "gif")
                    }
                }.getOrDefault(emptyList())
            }.orEmpty()
            (if (siblings.any { sameDocument(it, file) }) siblings else siblings + file)
        }
        val folderPath = file.parentFile?.let { store.pathOf(it) }.orEmpty()
        val breadcrumbs = mutableListOf("My Files" to "")
        var breadcrumbPath = ""
        folderPath.split('/').filter { it.isNotBlank() }.forEach { segment ->
            breadcrumbPath = if (breadcrumbPath.isBlank()) segment else "$breadcrumbPath/$segment"
            breadcrumbs += segment to breadcrumbPath
        }
        val reader = SmartPrintDocumentViewer(
            this,
            file,
            images,
            startPosition,
            onNavigateImage = { current -> viewingDocument = current },
            folderBreadcrumbs = breadcrumbs,
            onNavigateFolder = { destination ->
                documentViewer?.dispose()
                documentViewer = null
                viewingDocument = null
                this@MainActivity.folderPath = destination
                showFiles()
            }
        )
        documentViewer = reader
        content.addView(reader, frameParams())
    }

    private fun sameDocument(left: File, right: File): Boolean =
        runCatching { left.canonicalPath == right.canonicalPath }.getOrDefault(false)

    private fun closeDocumentViewer() {
        documentViewer?.dispose()
        documentViewer = null
        viewingDocument = null
        showFiles()
    }

    private fun openDocument(file: File) {
        if (supportsInAppViewer(file)) showDocumentViewer(file)
        else openDocumentExternally(file)
    }

    private fun openDocumentExternally(file: File) {
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
        radius = dp(21).toFloat()
        cardElevation = dp(4).toFloat()
        strokeWidth = dp(1)
        strokeColor = getColor(R.color.qp_glass_border)
        setCardBackgroundColor(getColor(R.color.qp_glass_card))
        useCompatPadding = false
    }
    private fun text(value: String, size: Int, color: Int, bold: Boolean) = TextView(this).apply {
        text = value
        textSize = maxOf(size, 11).toFloat()
        setTextColor(getColor(color))
        if (bold) setTypeface(typeface, android.graphics.Typeface.BOLD)
        includeFontPadding = false
    }
    private fun button(value: String, primary: Boolean) = MaterialButton(this).apply {
        text = value
        isAllCaps = false
        minHeight = dp(42)
        minWidth = dp(0)
        insetTop = 0
        insetBottom = 0
        cornerRadius = dp(16)
        setPadding(dp(12), dp(7), dp(12), dp(7))
        textSize = 13f
        if (primary) {
            backgroundTintList = ColorStateList.valueOf(getColor(R.color.qp_primary))
            setTextColor(getColor(R.color.qp_primary_content))
        } else {
            strokeWidth = dp(1)
            strokeColor = ColorStateList.valueOf(getColor(R.color.qp_glass_border))
            backgroundTintList = ColorStateList.valueOf(getColor(R.color.qp_glass_inset))
            setTextColor(getColor(R.color.qp_text))
        }
    }
    private fun linearParams() = LinearLayout.LayoutParams(-1, -2)
    private fun frameParams() = FrameLayout.LayoutParams(-1, -1)
    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()
    private fun toast(value: String) = Toast.makeText(this, value, Toast.LENGTH_LONG).show()
    private fun message(e: Exception) = e.message?.takeIf { it.isNotBlank() } ?: "Unexpected error"
}
