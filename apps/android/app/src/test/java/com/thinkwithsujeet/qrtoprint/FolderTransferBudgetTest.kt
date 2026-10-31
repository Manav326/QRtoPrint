package com.thinkwithsujeet.qrtoprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FolderTransferBudgetTest {
    @Test
    fun includesDirectoriesInItemLimitAndEnforcesDepth() {
        val budget = FolderTransferBudget(maxItems = 3, maxDepth = 2)
        assertTrue(budget.tryTake(depth = 0))
        assertTrue(budget.tryTake(depth = 1))
        assertFalse(budget.tryTake(depth = 3))
        assertEquals(2, budget.itemsVisited)
        assertTrue(budget.tryTake(depth = 2))
        assertFalse(budget.tryTake(depth = 0))
        assertEquals(3, budget.itemsVisited)
    }
}
