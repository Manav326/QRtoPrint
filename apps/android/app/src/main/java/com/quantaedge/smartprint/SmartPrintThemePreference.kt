package com.quantaedge.smartprint

import android.content.Context
import androidx.appcompat.app.AppCompatDelegate

/**
 * Theme choice is intentionally stored outside customer authentication/session
 * preferences so sign-out never resets the user's appearance choice.
 */
enum class SmartPrintTheme(val storedValue: String) {
    LIGHT("light"),
    DARK("dark");

    companion object {
        fun fromStoredValue(value: String?): SmartPrintTheme =
            if (value == DARK.storedValue) DARK else LIGHT
    }
}

object SmartPrintThemePreference {
    const val PREFERENCES_NAME = "smartprint.ui-preferences"
    private const val THEME_KEY = "theme"

    fun read(context: Context): SmartPrintTheme =
        SmartPrintTheme.fromStoredValue(
            context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
                .getString(THEME_KEY, SmartPrintTheme.LIGHT.storedValue)
        )

    fun apply(context: Context) {
        applyMode(read(context))
    }

    fun save(context: Context, theme: SmartPrintTheme) {
        context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
            .edit()
            .putString(THEME_KEY, theme.storedValue)
            .apply()
        applyMode(theme)
    }

    private fun applyMode(theme: SmartPrintTheme) {
        val mode = if (theme == SmartPrintTheme.DARK) {
            AppCompatDelegate.MODE_NIGHT_YES
        } else {
            AppCompatDelegate.MODE_NIGHT_NO
        }
        if (AppCompatDelegate.getDefaultNightMode() != mode) {
            AppCompatDelegate.setDefaultNightMode(mode)
        }
    }
}
