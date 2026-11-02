package com.quantaedge.smartprint

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ResponsiveLayoutPolicyTest {
    @Test
    fun phoneWidthsKeepBottomNavigationAndGoogleScanner() {
        listOf(320, 360, 393, 430, 599).forEach { width ->
            assertFalse("Navigation rail should not be used at " + width + "dp",
                ResponsiveLayoutPolicy.useNavigationRail(width))
            assertFalse("CameraX scanner should not be used at " + width + "dp",
                ResponsiveLayoutPolicy.useCameraXDocumentScanner(width))
        }
    }

    @Test
    fun tabletWidthsUseRailAndFullScreenCameraScanner() {
        listOf(600, 720, 800, 900, 1200).forEach { width ->
            assertTrue("Navigation rail expected at " + width + "dp",
                ResponsiveLayoutPolicy.useNavigationRail(width))
            assertTrue("CameraX scanner expected at " + width + "dp",
                ResponsiveLayoutPolicy.useCameraXDocumentScanner(width))
        }
    }

    @Test
    fun onlyVeryNarrowPhonesHideUnselectedNavigationLabels() {
        assertFalse(ResponsiveLayoutPolicy.showLabelsOnAllBottomItems(320))
        assertTrue(ResponsiveLayoutPolicy.showLabelsOnAllBottomItems(360))
        assertTrue(ResponsiveLayoutPolicy.showLabelsOnAllBottomItems(599))
    }
}
