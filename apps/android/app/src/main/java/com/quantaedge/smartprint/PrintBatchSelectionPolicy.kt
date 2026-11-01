package com.quantaedge.smartprint

import java.io.File

/** Validation rules shared by the native Android picker and the customer batch upload. */
object PrintBatchSelectionPolicy {
    const val MAX_FILES = 20
    const val MAX_TOTAL_BYTES = 50L * 1024L * 1024L

    /** Returns a customer-safe reason when the selected files cannot be uploaded together. */
    fun validationError(files: List<File>): String? {
        if (files.isEmpty()) return "Select at least one document."
        if (files.size > MAX_FILES) return "A print batch can contain up to $MAX_FILES files."

        val paths = files.map { file -> runCatching { file.canonicalPath }.getOrDefault(file.absolutePath) }
        if (paths.distinct().size != paths.size) return "A document can only be selected once in a print batch."

        var totalBytes = 0L
        for (file in files) {
            if (!file.isFile) return "A selected document is no longer available."
            val size = file.length()
            if (!DocumentNamePolicy.isPrintable(file.name, size)) {
                return file.name + " is not a supported PDF/JPG/PNG file or exceeds the 20 MB per-file limit."
            }
            if (size > MAX_TOTAL_BYTES - totalBytes) {
                return "A print batch can contain up to 50 MB in total. Remove some files and try again."
            }
            totalBytes += size
        }
        return null
    }
}
