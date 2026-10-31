package com.thinkwithsujeet.qrtoprint

/**
 * Bounds recursive folder import/export by item count and nesting depth.
 * Every child file or directory consumes one slot from the shared budget.
 */
internal class FolderTransferBudget(
    private val maxItems: Int = 500,
    private val maxDepth: Int = 12
) {
    var itemsVisited: Int = 0
        private set

    init {
        require(maxItems > 0) { "maxItems must be positive." }
        require(maxDepth >= 0) { "maxDepth cannot be negative." }
    }

    fun tryTake(depth: Int): Boolean {
        if (depth < 0 || depth > maxDepth || itemsVisited >= maxItems) return false
        itemsVisited++
        return true
    }
}
