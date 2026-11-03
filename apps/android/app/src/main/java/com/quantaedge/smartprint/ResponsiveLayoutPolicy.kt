package com.quantaedge.smartprint

import android.content.res.Configuration

/** Navigation follows device orientation, not tablet-sized portrait width. */
internal object ResponsiveLayoutPolicy {
    const val COMPACT_NAV_MAX_WIDTH_DP = 479

    fun useNavigationRail(orientation: Int): Boolean =
        orientation == Configuration.ORIENTATION_LANDSCAPE

    fun showLabelsOnAllBottomItems(screenWidthDp: Int): Boolean =
        screenWidthDp > COMPACT_NAV_MAX_WIDTH_DP
}
