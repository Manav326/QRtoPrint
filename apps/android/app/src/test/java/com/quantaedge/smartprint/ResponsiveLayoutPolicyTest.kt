package com.quantaedge.smartprint

import android.content.res.Configuration
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ResponsiveLayoutPolicyTest {
    @Test
    fun portraitAlwaysUsesBottomNavigationIncludingWideTablets() {
        listOf(320, 393, 600, 800, 1200).forEach { width ->
            assertFalse(
                "Portrait navigation must remain at the bottom at ${width}dp",
                ResponsiveLayoutPolicy.useNavigationRail(Configuration.ORIENTATION_PORTRAIT)
            )
        }
    }

    @Test
    fun landscapeAlwaysUsesLeftNavigationRail() {
        listOf(360, 480, 600, 800, 1200).forEach { width ->
            assertTrue(
                "Landscape navigation rail expected at ${width}dp",
                ResponsiveLayoutPolicy.useNavigationRail(Configuration.ORIENTATION_LANDSCAPE)
            )
        }
    }

    @Test
    fun compactBottomNavigationKeepsTheActiveLabelBesideItsIcon() {
        assertFalse(ResponsiveLayoutPolicy.showLabelsOnAllBottomItems(320))
        assertFalse(ResponsiveLayoutPolicy.showLabelsOnAllBottomItems(360))
        assertFalse(ResponsiveLayoutPolicy.showLabelsOnAllBottomItems(479))
        assertTrue(ResponsiveLayoutPolicy.showLabelsOnAllBottomItems(480))
        assertTrue(ResponsiveLayoutPolicy.showLabelsOnAllBottomItems(599))
    }
}
