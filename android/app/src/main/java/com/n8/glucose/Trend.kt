package com.n8.glucose

import org.json.JSONArray
import org.json.JSONObject
import java.time.OffsetDateTime
import kotlin.math.pow
import kotlin.math.sqrt

/** One CGM reading plus what was worked out from the readings before it. Times are epoch milliseconds. */
class Reading(val mgdl: Int, val time: Long, val direction: String?) {
    /** Change since the previous reading, per 5 minutes. */
    var delta: Double? = null
    /** Damped-trend estimate FORECAST_MINUTES ahead, with an 80% range. */
    var forecast: Double? = null
    var forecastLow: Double? = null
    var forecastHigh: Double? = null
    var rate: Double? = null
    var noisy = false
    var lowInMinutes: Int? = null
    var highInMinutes: Int? = null

    val arrow: String get() = when (direction) {
        "DoubleUp" -> "↑↑"; "SingleUp" -> "↑"; "FortyFiveUp" -> "↗"; "Flat" -> "→"
        "FortyFiveDown" -> "↘"; "SingleDown" -> "↓"; "DoubleDown" -> "↓↓"; else -> "?"
    }

    companion object {
        const val FORECAST_MINUTES = 20
        private val TREND_NAMES = arrayOf("None", "DoubleUp", "SingleUp", "FortyFiveUp", "Flat", "FortyFiveDown", "SingleDown", "DoubleDown", "NotComputable", "OutOfRange")

        /** Parses Nightscout entries (any order); returns the newest reading, analysed, or null for an empty array. */
        fun parse(json: String, now: Long): Reading? {
            val array = try { JSONArray(json) } catch (e: Exception) { throw IllegalArgumentException("Expected a JSON array.") }
            val readings = ArrayList<Reading>()
            for (i in 0 until array.length()) {
                val entry = array.optJSONObject(i) ?: continue
                if (entry.has("type") && entry.optString("type") != "sgv") continue
                val sgv = entry.opt("sgv")?.toString()?.toIntOrNull() ?: continue
                if (sgv < 1 || sgv > 1000) continue
                val time = time(entry) ?: continue
                if (time > now + 120_000) continue
                var direction = entry.optString("direction", "").ifBlank { null }
                if (direction == null) {
                    val trend = entry.opt("trend")?.toString()?.toIntOrNull()
                    if (trend != null && trend in TREND_NAMES.indices) direction = TREND_NAMES[trend]
                }
                readings.add(Reading(sgv, time, direction))
            }
            if (array.length() > 0 && readings.isEmpty()) throw IllegalArgumentException("No valid CGM entry or timestamp.")
            if (readings.isEmpty()) return null
            // Newest first; drop duplicate uploads of the same sensor reading (within 30 seconds).
            val ordered = ArrayList<Reading>()
            for (r in readings.sortedByDescending { it.time })
                if (ordered.isEmpty() || ordered.last().time - r.time >= 30_000) ordered.add(r)
            analyze(ordered)
            return ordered[0]
        }

        private fun time(entry: JSONObject): Long? {
            for (key in arrayOf("date", "mills")) {
                val ms = entry.opt(key)?.toString()?.toLongOrNull()
                if (ms != null && ms > 0) return ms
            }
            for (key in arrayOf("dateString", "sysTime")) {
                val text = entry.optString(key, "")
                if (text.isNotEmpty()) try { return OffsetDateTime.parse(text).toInstant().toEpochMilli() } catch (e: Exception) { }
            }
            return null
        }

        private fun analyze(readings: List<Reading>) {
            val newest = readings[0]
            if (readings.size >= 2) {
                val gap = (newest.time - readings[1].time) / 60_000.0
                if (gap in 2.5..10.0) newest.delta = (newest.mgdl - readings[1].mgdl) * 5.0 / gap
            }
            window(readings, 30.5)?.let { TrendModel.apply(it) }
        }

        /** Contiguous readings (gaps of at most 7.5 minutes) within the given minutes; null if fewer than 3 or spanning under 9.5 minutes. */
        private fun window(readings: List<Reading>, minutes: Double): List<Reading>? {
            val newest = readings[0]
            val window = arrayListOf(newest)
            for (i in 1 until readings.size) {
                if ((window.last().time - readings[i].time) / 60_000.0 > 7.5) break
                if ((newest.time - readings[i].time) / 60_000.0 > minutes) break
                window.add(readings[i])
            }
            return if (window.size < 3 || (newest.time - window.last().time) / 60_000.0 < 9.5) null else window
        }
    }
}

/**
 * Same damped-trend model as the Windows overlay (src/Forecast.cs): a Kalman filter over level and rate,
 * projected forward with the rate fading over time. Keep the constants in step with the PC version.
 */
object TrendModel {
    const val MEASUREMENT_VARIANCE = 16.0
    const val ACCELERATION_NOISE = 0.1
    const val DAMP_PER_MINUTE = 0.955
    const val Z80 = 1.2816
    const val NOISY_LIMIT = 25.0
    const val LOW_THRESHOLD = 70
    const val HIGH_THRESHOLD = 250
    const val THRESHOLD_HORIZON = 30
    const val LOW_WARN_RATE = -0.5

    fun effectiveMinutes(h: Double) = DAMP_PER_MINUTE * (1 - DAMP_PER_MINUTE.pow(h)) / (1 - DAMP_PER_MINUTE)

    /** window is newest first. */
    fun apply(window: List<Reading>) {
        val newest = window[0]
        val ordered = window.reversed()
        // Start from the first two readings (level and rate), so a fast rise or fall is not mistaken for noise.
        val dt0 = (ordered[1].time - ordered[0].time) / 60_000.0
        val r = MEASUREMENT_VARIANCE
        var g = ordered[1].mgdl.toDouble(); var v = (ordered[1].mgdl - ordered[0].mgdl) / dt0
        var pgg = r; var pgv = r / dt0; var pvv = 2 * r / (dt0 * dt0)
        var nis = 0.0; var updates = 0
        val q = ACCELERATION_NOISE
        for (i in 2 until ordered.size) {
            val dt = (ordered[i].time - ordered[i - 1].time) / 60_000.0
            g += v * dt
            val ngg = pgg + 2 * dt * pgv + dt * dt * pvv + q * dt * dt * dt / 3
            val ngv = pgv + dt * pvv + q * dt * dt / 2
            val nvv = pvv + q * dt
            pgg = ngg; pgv = ngv; pvv = nvv
            val innovation = ordered[i].mgdl - g
            val s = pgg + MEASUREMENT_VARIANCE
            nis += innovation * innovation / s; updates++
            val kg = pgg / s; val kv = pgv / s
            g += kg * innovation; v += kv * innovation
            pvv -= kv * pgv; pgg *= 1 - kg; pgv *= 1 - kg
        }
        newest.rate = v
        // Rougher readings than expected widen the range instead of hiding the estimate; hide only when chaotic.
        val roughness = nis / updates
        if (roughness > NOISY_LIMIT) { newest.noisy = true; return }
        val scale = maxOf(1.0, roughness)
        pgg *= scale; pgv *= scale; pvv *= scale
        fun project(h: Double): Pair<Double, Double> {
            val k = effectiveMinutes(h)
            val variance = pgg + 2 * k * pgv + k * k * pvv + scale * (q * k * k * k / 3 + MEASUREMENT_VARIANCE)
            return Pair(g + v * k, sqrt(maxOf(0.0, variance)))
        }
        val (mean, sd) = project(Reading.FORECAST_MINUTES.toDouble())
        newest.forecast = mean; newest.forecastLow = mean - Z80 * sd; newest.forecastHigh = mean + Z80 * sd
        for (h in 1..THRESHOLD_HORIZON) {
            val (m, s) = project(h.toDouble())
            if (newest.lowInMinutes == null && newest.mgdl > LOW_THRESHOLD && v <= LOW_WARN_RATE && m - Z80 * s <= LOW_THRESHOLD) newest.lowInMinutes = h
            if (newest.highInMinutes == null && newest.mgdl < HIGH_THRESHOLD && v > 0 && m >= HIGH_THRESHOLD) newest.highInMinutes = h
        }
    }
}

/** Text for the widget and notification, shared so both always agree. */
object Format {
    private const val MMOL = 18.0182
    fun value(mgdl: Int, mmol: Boolean) = if (mmol) "%.1f".format(java.util.Locale.ROOT, mgdl / MMOL) else mgdl.toString()
    fun unit(mmol: Boolean) = if (mmol) "mmol/L" else "mg/dL"
    private fun num(x: Double, mmol: Boolean) = if (mmol) "%.1f".format(java.util.Locale.ROOT, x / MMOL) else Math.round(x).toString()

    fun delta(r: Reading, mmol: Boolean): String? {
        val d = r.delta ?: return null
        var text = if (mmol) "%.1f".format(java.util.Locale.ROOT, d / MMOL) else Math.round(d).toString()
        if (text == "-0" || text == "-0.0") text = text.substring(1)
        return (if (text.startsWith("-")) "" else "+") + text + " / 5m"
    }

    fun estimate(r: Reading, mmol: Boolean): String? {
        val lo = r.forecastLow; val hi = r.forecastHigh
        if (lo == null || hi == null) return if (r.noisy) "est. — noisy readings" else null
        val range = when {
            hi < 40 -> if (mmol) "<2.2" else "<40"
            lo > 400 -> if (mmol) ">22.2" else ">400"
            else -> (if (lo < 40) "<" + num(40.0, mmol) else num(lo, mmol)) + "–" + (if (hi > 400) ">" + num(400.0, mmol) else num(hi, mmol))
        }
        return "est. $range in ${Reading.FORECAST_MINUTES}m"
    }

    fun threshold(r: Reading, mmol: Boolean): String? {
        fun t(x: Int) = if (mmol) "%.1f".format(java.util.Locale.ROOT, x / MMOL) else x.toString()
        r.lowInMinutes?.let { return "Below ${t(TrendModel.LOW_THRESHOLD)} possible in ~${it}m · estimate" }
        r.highInMinutes?.let { return "Above ${t(TrendModel.HIGH_THRESHOLD)} likely in ~${it}m · estimate" }
        return null
    }

    fun age(ms: Long): String {
        if (ms < 0) return "clock ahead"
        val s = ms / 1000
        return when {
            s < 60 -> "${s}s ago"
            s < 3600 -> "${s / 60}m ago"
            s < 86400 -> "${s / 3600}h ${(s % 3600) / 60}m ago"
            else -> "${s / 86400}d ago"
        }
    }

    fun iob(iob: Double): String {
        val text = "%.2f".format(java.util.Locale.ROOT, iob)
        return if (text == "-0.00") "0.00" else text
    }
}
