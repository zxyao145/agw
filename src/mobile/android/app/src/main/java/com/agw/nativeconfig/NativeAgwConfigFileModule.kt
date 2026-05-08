package com.agw.nativeconfig

import com.facebook.fbreact.specs.NativeAgwConfigFileSpec
import com.facebook.react.bridge.ReactApplicationContext
import java.io.File

class NativeAgwConfigFileModule(
    reactContext: ReactApplicationContext
) : NativeAgwConfigFileSpec(reactContext) {
    override fun getName(): String = NAME

    override fun readConfig(): String? {
        return try {
            val file = configFile()

            if (file.exists()) {
                file.readText(Charsets.UTF_8)
            } else {
                null
            }
        } catch (_: Exception) {
            null
        }
    }

    override fun writeConfig(value: String): String? {
        return try {
            val file = configFile()
            val directory = file.parentFile

            if (directory != null && !directory.exists()) {
                directory.mkdirs()
            }

            val tempFile = File("${file.absolutePath}.tmp")
            tempFile.writeText(value, Charsets.UTF_8)

            if (!tempFile.renameTo(file)) {
                file.writeText(value, Charsets.UTF_8)
                tempFile.delete()
            }

            null
        } catch (error: Exception) {
            error.message ?: "Failed to write local configuration."
        }
    }

    override fun deleteConfig(): String? {
        return try {
            val file = configFile()

            if (file.exists()) {
                file.delete()
            }

            null
        } catch (error: Exception) {
            error.message ?: "Failed to delete local configuration."
        }
    }

    private fun configFile(): File {
        val directory = File(reactApplicationContext.filesDir, "agw")
        return File(directory, "config.json")
    }

    companion object {
        const val NAME = "NativeAgwConfigFile"
    }
}
