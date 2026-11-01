package com.quantaedge.smartprint

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.nio.charset.StandardCharsets
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Stores short-lived guest print-session capabilities per station.
 * The browser keeps only the non-secret job draft; session tokens stay encrypted
 * with an Android Keystore AES-GCM key and are scoped to the station code.
 */
class CustomerPrintSessionStore(context: Context) {
    private val preferences = context.getSharedPreferences("smartprint.customer.sessions", Context.MODE_PRIVATE)

    fun read(publicCode: String): String? {
        validateCode(publicCode)
        val encoded = preferences.getString(publicCode, null) ?: return null
        return runCatching {
            val payload = Base64.decode(encoded, Base64.NO_WRAP)
            require(payload.size > 12) { "Stored print session is invalid." }
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(
                Cipher.DECRYPT_MODE,
                getOrCreateKey(),
                GCMParameterSpec(128, payload.copyOfRange(0, 12))
            )
            String(cipher.doFinal(payload.copyOfRange(12, payload.size)), StandardCharsets.UTF_8)
                .takeIf { it.isNotBlank() && it.length <= MAX_TOKEN_LENGTH }
        }.getOrElse {
            clear(publicCode)
            null
        }
    }

    fun save(publicCode: String, token: String) {
        validateCode(publicCode)
        require(token.isNotBlank() && token.length <= MAX_TOKEN_LENGTH) {
            "Print session token is invalid."
        }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        val payload = cipher.iv + cipher.doFinal(token.toByteArray(StandardCharsets.UTF_8))
        preferences.edit()
            .putString(publicCode, Base64.encodeToString(payload, Base64.NO_WRAP))
            .apply()
    }

    fun clear(publicCode: String) {
        validateCode(publicCode)
        preferences.edit().remove(publicCode).apply()
    }

    private fun validateCode(publicCode: String) {
        require(CODE_PATTERN.matches(publicCode)) { "Station code is invalid." }
    }

    private fun getOrCreateKey(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val existing = keyStore.getKey(KEY_ALIAS, null) as? SecretKey
        if (existing != null) return existing
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setRandomizedEncryptionRequired(true)
                .build()
        )
        return generator.generateKey()
    }

    private companion object {
        const val KEY_ALIAS = "smartprint.customer.print-session.v1"
        const val MAX_TOKEN_LENGTH = 8192
        val CODE_PATTERN = Regex("^[A-Za-z0-9_-]{32}$")
    }
}
