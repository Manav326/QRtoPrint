package com.quantaedge.smartprint

/** Shared width breakpoints for native navigation and the tablet document-scanner experience. */
internal object ResponsiveLayoutPolicy {
    const val TABLET_MIN_WIDTH_DP = 600
    const val COMPACT_NAV_MAX_WIDTH_DP = 359

    fun useNavigationRail(screenWidthDp: Int): Boolean =
        screenWidthDp >= TABLET_MIN_WIDTH_DP

    fun useCameraXDocumentScanner(screenWidthDp: Int): Boolean =
        screenWidthDp >= TABLET_MIN_WIDTH_DP

    fun showLabelsOnAllBottomItems(screenWidthDp: Int): Boolean =
        screenWidthDp > COMPACT_NAV_MAX_WIDTH_DP
}
