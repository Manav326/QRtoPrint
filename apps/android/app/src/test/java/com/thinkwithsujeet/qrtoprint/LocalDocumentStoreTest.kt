package com.thinkwithsujeet.qrtoprint

import java.io.ByteArrayInputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class LocalDocumentStoreTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    @Test
    fun importsDuplicateFilesWithoutOverwriting() {
        val store = LocalDocumentStore(temporaryFolder.newFolder("private"))
        store.createFolder("", "School")

        val first = store.importFile(ByteArrayInputStream("first".toByteArray()), "homework.pdf", "School")
        val second = store.importFile(ByteArrayInputStream("second".toByteArray()), "homework.pdf", "School")

        assertEquals("homework.pdf", first.name)
        assertEquals("homework (2).pdf", second.name)
        assertEquals("first", first.readText())
        assertEquals("second", second.readText())
    }

    @Test
    fun rejectsPathTraversalAndCleansEmptyImports() {
        val store = LocalDocumentStore(temporaryFolder.newFolder("private"))

        assertThrows(IllegalArgumentException::class.java) { store.folder("../outside") }
        assertThrows(IllegalArgumentException::class.java) {
            store.importFile(ByteArrayInputStream(byteArrayOf()), "empty.pdf", "")
        }
        assertFalse(store.list(store.root).isNotEmpty())
    }

    @Test
    fun listsFoldersBeforeFilesAndKeepsNestedStructure() {
        val store = LocalDocumentStore(temporaryFolder.newFolder("private"))
        store.importFile(ByteArrayInputStream("file".toByteArray()), "a.pdf", "")
        val folder = store.createFolder("", "Documents")
        store.createFolder("Documents", "School")

        val entries = store.list(store.root)
        assertTrue(entries.first().isDirectory)
        assertEquals("Documents", entries.first().name)
        assertEquals("Documents/School", store.pathOf(store.folder("Documents/School")))
    }

    @Test
    fun folderTransferBudgetCountsDirectoriesAndEnforcesDepth() {
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
