package com.thinkwithsujeet.qrtoprint

import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.util.Locale

class LocalDocumentStore(filesDir: File) {
    val root = File(filesDir, "documents")

    init {
        check(root.exists() || root.mkdirs()) { "Document storage could not be created." }
        check(root.isDirectory) { "Document storage is not a directory." }
    }

    fun folder(path: String): File {
        if (path.isBlank()) return root
        val parts = path.replace('\\', '/').split('/').filter { it.isNotBlank() }
        require(parts.none { it == "." || it == ".." }) { "Invalid folder path." }
        val result = File(root, parts.joinToString(File.separator)).canonicalFile
        val base = root.canonicalFile
        require(result.path == base.path || result.path.startsWith(base.path + File.separator)) {
            "Folder is outside QRtoPrint storage."
        }
        require(result.isDirectory) { "Folder is no longer available." }
        return result
    }

    fun createFolder(parentPath: String, name: String, allowExisting: Boolean = false): File {
        val parent = folder(parentPath)
        val target = File(parent, cleanName(name))
        if (allowExisting && target.isDirectory) return target
        require(!target.exists()) { "An item with that name already exists." }
        check(target.mkdir()) { "Folder could not be created." }
        return target
    }

    fun importFile(input: InputStream, name: String, destinationPath: String): File {
        val target = uniqueFile(folder(destinationPath), DocumentNamePolicy.safeName(name))
        var total = 0L
        try {
            FileOutputStream(target).use { output ->
                val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
                while (true) {
                    val n = input.read(buffer)
                    if (n < 0) break
                    total += n
                    require(total <= DocumentNamePolicy.MAX_IMPORT_BYTES) { "Imported files must be 100 MB or smaller." }
                    output.write(buffer, 0, n)
                }
                output.fd.sync()
            }
            require(total > 0) { "The selected file is empty." }
            return target
        } catch (e: Exception) {
            target.delete()
            throw e
        }
    }

    fun list(directory: File): Array<File> {
        val base = root.canonicalPath
        val path = directory.canonicalPath
        require(path == base || path.startsWith(base + File.separator)) { "Folder is outside local storage." }
        return (directory.listFiles() ?: emptyArray()).sortedWith(
            compareBy<File>({ !it.isDirectory }, { it.name.lowercase(Locale.ROOT) })
        ).toTypedArray()
    }

    fun pathOf(file: File): String {
        val base = root.canonicalPath
        val path = file.canonicalPath
        require(path.startsWith(base + File.separator)) { "Item is outside local storage." }
        return path.removePrefix(base + File.separator).replace(File.separatorChar, '/')
    }

    fun rename(file: File, name: String) {
        require(file.canonicalPath.startsWith(root.canonicalPath + File.separator)) { "Item is outside local storage." }
        val target = File(file.parentFile, cleanName(name))
        require(!target.exists()) { "An item with that name already exists." }
        check(file.renameTo(target)) { "Item could not be renamed." }
    }

    fun delete(file: File) {
        require(file.canonicalPath.startsWith(root.canonicalPath + File.separator)) { "Cannot delete the library root." }
        check(file.deleteRecursively()) { "Item could not be deleted." }
    }

    private fun cleanName(name: String): String {
        val safe = DocumentNamePolicy.safeName(name)
        require(safe != "." && safe != "..") { "Name is invalid." }
        return safe
    }

    private fun uniqueFile(parent: File, name: String): File {
        val dot = name.lastIndexOf('.')
        val base = if (dot > 0) name.substring(0, dot) else name
        val ext = if (dot > 0) name.substring(dot) else ""
        var file = File(parent, name)
        var i = 2
        while (file.exists()) {
            file = File(parent, "$base ($i)$ext")
            i++
            require(i < 10_000) { "Too many duplicate file names." }
        }
        return file
    }
}
