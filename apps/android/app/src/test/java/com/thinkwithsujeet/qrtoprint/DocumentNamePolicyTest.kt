package com.thinkwithsujeet.qrtoprint

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DocumentNamePolicyTest {
    @Test fun validatesPrintExtensionsAndSize() {
        assertTrue(DocumentNamePolicy.isPrintable("a.PDF", 10))
        assertTrue(DocumentNamePolicy.isPrintable("b.jpeg", 10))
        assertFalse(DocumentNamePolicy.isPrintable("a.docx", 10))
        assertFalse(DocumentNamePolicy.isPrintable("a.pdf", DocumentNamePolicy.MAX_PRINT_BYTES + 1))
        assertFalse(DocumentNamePolicy.isPrintable("a.pdf", 0))
    }
    @Test fun sanitisesNames() {
        assertEquals("_safe_.pdf", DocumentNamePolicy.safeName("../safe?.pdf"))
        assertEquals("Untitled document", DocumentNamePolicy.safeName("   "))
    }
}
