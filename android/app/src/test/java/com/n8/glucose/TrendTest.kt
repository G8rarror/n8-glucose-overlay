package com.n8.glucose

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Mirrors the forecast checks in the Windows overlay's src/Verification.cs, so both apps agree. */
class TrendTest {
    private val now = 1_800_000_000_000L
    private fun series(values: IntArray, stepMinutes: Int): String =
        values.mapIndexed { i, v -> "{\"sgv\":$v,\"date\":${now - i * stepMinutes * 60_000L}}" }.joinToString(",", "[", "]")
    private fun parse(vararg values: Int) = Reading.parse(series(values, 5), now)!!

    @Test fun steadyRiseIsDampedWithRange() {
        val r = parse(130, 120, 110, 100, 90, 80)
        assertEquals(10.0, r.delta!!, 1e-9)
        assertTrue(r.forecast!! > 145 && r.forecast!! < 170)
        assertTrue(r.forecastLow!! < r.forecast!! && r.forecast!! < r.forecastHigh!! && r.forecastHigh!! - r.forecastLow!! < 50)
        assertTrue(r.rate!! > 1.6 && r.rate!! < 2.2)
        assertEquals("+10 / 5m", Format.delta(r, false))
        assertTrue(Format.estimate(r, true)!!.contains("–"))
    }

    @Test fun fallingBelowRange() {
        val r = parse(60, 75, 90, 105, 120)
        assertTrue(Format.estimate(r, false)!!.startsWith("est. <40"))
        assertNull(r.lowInMinutes)
    }

    @Test fun noisyIsFlagged() {
        val r = parse(100, 140, 95, 135)
        assertNull(r.forecast); assertTrue(r.noisy)
        assertEquals("est. — noisy readings", Format.estimate(r, false))
    }

    @Test fun easingFallIsNotNoise() {
        val r = parse(100, 104, 112, 124, 138, 154)
        assertTrue(!r.noisy && r.forecast!! > 70 && r.forecast!! < 100)
        // Uneven fall (repeated values, then catch-up jumps): estimate kept, range wider than for a smooth fall.
        val smooth = parse(100, 110, 120, 130, 140, 150)
        val uneven = parse(100, 100, 111, 111, 128, 150)
        assertTrue(!uneven.noisy && uneven.forecast != null)
        assertTrue(uneven.forecastHigh!! - uneven.forecastLow!! > smooth.forecastHigh!! - smooth.forecastLow!!)
    }

    @Test fun thresholds() {
        val flat = parse(80, 80, 81, 80, 79, 80)
        assertNull(flat.lowInMinutes); assertNull(flat.highInMinutes)
        val slide = parse(95, 100, 105, 110, 115, 120)
        assertTrue(slide.lowInMinutes!! in 5..30)
        assertTrue(Format.threshold(slide, false)!!.startsWith("Below 70 possible in ~"))
        val climb = parse(235, 225, 215, 205, 195, 185)
        assertTrue(climb.highInMinutes!! in 3..15)
        assertNull(climb.lowInMinutes)
    }

    @Test fun gapsAndShortHistory() {
        assertNull(Reading.parse(series(intArrayOf(110, 100), 15), now)!!.delta)
        assertNull(Reading.parse(series(intArrayOf(110, 105), 5), now)!!.forecast)
        assertEquals(5.0, Reading.parse(series(intArrayOf(110, 100), 10), now)!!.delta!!, 1e-9)
    }

    @Test fun parsing() {
        assertNull(Reading.parse("[]", now))
        assertEquals(110, Reading.parse("[{\"sgv\":90,\"date\":${now - 60000}},{\"sgv\":110,\"date\":$now}]", now)!!.mgdl)
        assertEquals("FortyFiveUp", Reading.parse("[{\"sgv\":90,\"date\":$now,\"trend\":3}]", now)!!.direction)
        assertNotNull(Reading.parse("[{\"sgv\":90,\"dateString\":\"2027-01-15T08:00:00Z\"}]", now))
        try { Reading.parse("[{\"sgv\":100}]", now); throw AssertionError("missing timestamp accepted") } catch (e: IllegalArgumentException) { }
        try { Reading.parse("[{\"sgv\":100,\"dateString\":\"2099-01-01T00:00:00Z\"}]", now); throw AssertionError("future accepted") } catch (e: IllegalArgumentException) { }
    }

    @Test fun iobParsing() {
        assertEquals(1.35, Gluroo.parseIob("[{\"glurooIob\":1.35,\"glurooCob\":0}]")!!, 1e-9)
        assertEquals(0.0, Gluroo.parseIob("{\"glurooIob\":\"0\"}")!!, 1e-9)
        assertNull(Gluroo.parseIob("[{\"glurooCob\":0}]"))
        try { Gluroo.parseIob("[{\"glurooIob\":500}]"); throw AssertionError("implausible accepted") } catch (e: IllegalArgumentException) { }
        assertEquals("0.00", Format.iob(-0.001))
    }

    @Test fun serverAndHeader() {
        assertEquals("https://abc-1.ns.gluroo.com", Gluroo.validateServer(" https://ABC-1.ns.gluroo.com/ "))
        for (bad in listOf("http://abc.ns.gluroo.com", "https://abc.ns.gluroo.com/api/v1", "https://abc.ns.gluroo.com?token=x", "https://evil.com", "https://abc.ns.gluroo.com:8443"))
            try { Gluroo.validateServer(bad); throw AssertionError("accepted $bad") } catch (e: IllegalArgumentException) { }
        // SHA-1 of "test", the same vector the PC tests use.
        assertEquals("a94a8fe5ccb19ba61c4c0873d391e987982fbbd3", Gluroo.header("test", false))
    }

    @Test fun pollPolicyMatchesPc() {
        val p = PollPolicy()
        val t = now
        p.begin(t); p.success(t, t - 30_000)
        assertEquals(t - 30_000 + 315_000, p.next)
        p.begin(t); p.success(t, t - 300_000)
        assertEquals(t + 120_000, p.next)
        p.begin(t); p.success(t, null)
        assertEquals(t + 300_000, p.next)
        p.fail(500, t); assertEquals(t + 120_000, p.next)
        p.fail(500, t); assertEquals(t + 240_000, p.next)
        p.fail(401, t); assertTrue(p.unauthorized && !p.canPoll(t + 10_000_000))
    }
}
