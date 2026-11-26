package com.n8.glucose

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONObject
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** What the widget and notification show; saved so they can be redrawn after the app process restarts. */
class Snapshot(
    val reading: Reading? = null,
    val iob: Double? = null,
    val iobTime: Long = 0,
    val status: String = "Open N8 Glucose to connect",
    val error: Boolean = false,
) {
    companion object {
        const val IOB_SHOW_MS = 25 * 60_000L
        const val STALE_MS = 10 * 60_000L
    }
    fun iobShowable(now: Long) = iob != null && now - iobTime <= IOB_SHOW_MS

    fun toJson(): String = JSONObject().apply {
        reading?.let { r ->
            put("mgdl", r.mgdl); put("time", r.time); put("direction", r.direction ?: "")
            r.delta?.let { put("delta", it) }; r.forecast?.let { put("forecast", it) }
            r.forecastLow?.let { put("lo", it) }; r.forecastHigh?.let { put("hi", it) }
            put("noisy", r.noisy); put("lowNow", r.lowNow); r.lowInMinutes?.let { put("lowIn", it) }; r.highInMinutes?.let { put("highIn", it) }
        }
        iob?.let { put("iob", it) }; put("iobTime", iobTime); put("status", status); put("error", error)
    }.toString()

    fun with(reading: Reading? = this.reading, iob: Double? = this.iob, iobTime: Long = this.iobTime, status: String = this.status, error: Boolean = this.error) =
        Snapshot(reading, iob, iobTime, status, error)
}

object Store {
    private const val KEY_ALIAS = "n8-gluroo-header"
    fun prefs(c: Context): SharedPreferences = c.getSharedPreferences("n8", Context.MODE_PRIVATE)

    fun server(c: Context): String? = prefs(c).getString("server", null)
    fun mmol(c: Context) = prefs(c).getBoolean("mmol", false)
    fun enabled(c: Context) = prefs(c).getBoolean("enabled", false)
    fun setEnabled(c: Context, on: Boolean) = prefs(c).edit().putBoolean("enabled", on).apply()

    fun header(c: Context): String? = try { prefs(c).getString("header", null)?.let { decrypt(it) } } catch (e: Exception) { null }

    fun saveConnection(c: Context, server: String, header: String, mmol: Boolean) {
        prefs(c).edit().putString("server", server).putString("header", encrypt(header)).putBoolean("mmol", mmol)
            .putBoolean("unauthorized", false).putLong("next", 0).apply()
    }
    fun saveUnits(c: Context, mmol: Boolean) = prefs(c).edit().putBoolean("mmol", mmol).apply()

    fun loadPolicy(c: Context) = prefs(c).let { PollPolicy(it.getLong("next", 0), it.getLong("lastAttempt", 0), it.getInt("failures", 0), it.getBoolean("unauthorized", false)) }
    fun savePolicy(c: Context, p: PollPolicy) = prefs(c).edit().putLong("next", p.next).putLong("lastAttempt", p.lastAttempt)
        .putInt("failures", p.failures).putBoolean("unauthorized", p.unauthorized).apply()

    fun saveSnapshot(c: Context, s: Snapshot) = prefs(c).edit().putString("snapshot", s.toJson()).apply()
    fun loadSnapshot(c: Context): Snapshot {
        val text = prefs(c).getString("snapshot", null) ?: return Snapshot()
        return try {
            val o = JSONObject(text)
            val reading = if (o.has("mgdl")) Reading(o.getInt("mgdl"), o.getLong("time"), o.optString("direction").ifEmpty { null }).apply {
                if (o.has("delta")) delta = o.getDouble("delta"); if (o.has("forecast")) forecast = o.getDouble("forecast")
                if (o.has("lo")) forecastLow = o.getDouble("lo"); if (o.has("hi")) forecastHigh = o.getDouble("hi")
                noisy = o.optBoolean("noisy"); lowNow = o.optBoolean("lowNow"); if (o.has("lowIn")) lowInMinutes = o.getInt("lowIn"); if (o.has("highIn")) highInMinutes = o.getInt("highIn")
            } else null
            Snapshot(reading, if (o.has("iob")) o.getDouble("iob") else null, o.optLong("iobTime"), o.optString("status"), o.optBoolean("error"))
        } catch (e: Exception) { Snapshot() }
    }

    fun clear(c: Context) {
        prefs(c).edit().clear().apply()
        try { KeyStore.getInstance("AndroidKeyStore").apply { load(null) }.deleteEntry(KEY_ALIAS) } catch (e: Exception) { }
    }

    // The api-secret header is stored encrypted with a key kept in the Android Keystore; it never leaves this phone.
    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(KEY_ALIAS, null) as SecretKey?)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())
        return generator.generateKey()
    }
    private fun encrypt(text: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        return Base64.encodeToString(cipher.iv + cipher.doFinal(text.toByteArray(Charsets.UTF_8)), Base64.NO_WRAP)
    }
    private fun decrypt(stored: String): String {
        val bytes = Base64.decode(stored, Base64.NO_WRAP)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes, 0, 12)) }
        return String(cipher.doFinal(bytes, 12, bytes.size - 12), Charsets.UTF_8)
    }
}
