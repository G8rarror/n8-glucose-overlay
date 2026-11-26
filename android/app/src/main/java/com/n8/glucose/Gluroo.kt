package com.n8.glucose

import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL
import java.security.MessageDigest

class ApiException(val status: Int) : Exception("HTTP $status")

/** Read-only Gluroo (Nightscout-compatible) client. Only GET requests, only the documented endpoints the PC overlay uses. */
object Gluroo {
    private val HOST = Regex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.ns\\.gluroo\\.com$", RegexOption.IGNORE_CASE)
    private val HEX40 = Regex("^[0-9a-fA-F]{40}$")
    const val HISTORY_COUNT = 6

    /** Accepts only the exact https://your-id.ns.gluroo.com base URL. */
    fun validateServer(text: String): String {
        val uri = try { URI(text.trim()) } catch (e: Exception) { null }
        val host = uri?.host
        if (uri == null || uri.scheme != "https" || uri.port != -1 || uri.rawUserInfo != null || uri.rawQuery != null ||
            uri.rawFragment != null || !(uri.rawPath.isNullOrEmpty() || uri.rawPath == "/") || host == null || !HOST.matches(host))
            throw IllegalArgumentException("Enter the exact Gluroo Nightscout URL, such as https://your-id.ns.gluroo.com")
        return "https://" + host.lowercase()
    }

    /** The api-secret header: SHA-1 of the plain API Secret Token, or the 40-character header itself. */
    fun header(input: String, alreadyHashed: Boolean): String {
        val text = input.trim()
        if (alreadyHashed) {
            require(HEX40.matches(text)) { "The API Secret Header must be exactly 40 hexadecimal characters." }
            return text.lowercase()
        }
        require(text.isNotEmpty() && text.length <= 512 && text.none { it.isISOControl() }) { "Enter the API Secret Token from Gluroo." }
        return MessageDigest.getInstance("SHA-1").digest(text.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
    }

    fun entries(server: String, header: String, now: Long): Reading? =
        Reading.parse(get(server, header, "/api/v1/entries.json?count=$HISTORY_COUNT"), now)

    /** Gluroo's own insulin on board, or null if it reports none. */
    fun iob(server: String, header: String): Double? = parseIob(get(server, header, "/api/v1/devicestatus.json?count=1"))

    fun parseIob(json: String): Double? {
        val trimmed = json.trim()
        val obj: JSONObject = when {
            trimmed.startsWith("[") -> JSONArray(trimmed).optJSONObject(0) ?: return null
            trimmed.startsWith("{") -> JSONObject(trimmed)
            else -> throw IllegalArgumentException("Invalid JSON")
        }
        if (!obj.has("glurooIob") || obj.isNull("glurooIob")) return null
        val iob = obj.get("glurooIob").toString().toDoubleOrNull() ?: throw IllegalArgumentException("IOB not a number")
        require(iob in -10.0..100.0) { "Implausible IOB" }
        return iob
    }

    private fun get(server: String, header: String, path: String, maxBytes: Int = 65536): String {
        val connection = URL(server + path).openConnection() as HttpURLConnection
        try {
            connection.requestMethod = "GET"
            connection.instanceFollowRedirects = false
            connection.useCaches = false
            connection.connectTimeout = 15_000
            connection.readTimeout = 15_000
            connection.setRequestProperty("api-secret", header)
            connection.setRequestProperty("Accept", "application/json")
            connection.setRequestProperty("User-Agent", "N8GlucoseAndroid/1.1.0")
            val status = connection.responseCode
            if (status != 200) throw ApiException(status)
            connection.inputStream.use { input ->
                val out = ByteArrayOutputStream()
                val buffer = ByteArray(8192)
                while (true) {
                    val n = input.read(buffer)
                    if (n < 0) break
                    out.write(buffer, 0, n)
                    if (out.size() > maxBytes) throw IllegalArgumentException("Response too large")
                }
                return out.toString("UTF-8")
            }
        } finally {
            connection.disconnect()
        }
    }
}

/** Request pacing, matching the PC overlay: one poll per CGM reading, never closer than 2 minutes, backing off on errors. */
class PollPolicy(var next: Long = 0, var lastAttempt: Long = 0, var failures: Int = 0, var unauthorized: Boolean = false) {
    companion object {
        const val MIN_GAP = 120_000L
        const val CGM_CYCLE = 315_000L
    }
    fun canPoll(now: Long) = !unauthorized && now >= next && now - lastAttempt >= MIN_GAP
    fun begin(now: Long) { lastAttempt = now; next = now + MIN_GAP }
    fun success(now: Long, newest: Long?) {
        failures = 0
        next = when {
            newest == null || now - newest > 15 * 60_000L -> lastAttempt + 300_000L
            else -> maxOf(lastAttempt + MIN_GAP, minOf(newest + CGM_CYCLE, now + 360_000L))
        }
    }
    fun fail(status: Int, now: Long) {
        if (status == 401 || status == 403) { unauthorized = true; return }
        if (status == 429) { next = (now / 86_400_000L + 1) * 86_400_000L; return }
        failures++
        next = now + minOf(900_000L, (120_000.0 * Math.pow(2.0, (failures - 1).toDouble())).toLong())
    }
}
