package com.n8.glucose

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import java.util.concurrent.Executors

/**
 * Keeps the widget and its notification current. Polls Gluroo only while the screen is on (nobody can see
 * the widget otherwise), and right away when the screen turns on. Read-only; never alerts.
 */
class GlucoseService : Service() {
    companion object {
        const val CHANNEL = "glucose"
        const val NOTIFICATION_ID = 1
        const val IOB_POLL_MS = 600_000L
        private const val TICK_MS = 30_000L

        fun start(c: Context) {
            if (Store.header(c) == null || Store.server(c) == null) return
            try { c.startForegroundService(Intent(c, GlucoseService::class.java)) } catch (e: Exception) { /* background start not allowed now; the app or boot will start it */ }
        }
        fun stop(c: Context) = c.stopService(Intent(c, GlucoseService::class.java))
    }

    private val handler = Handler(Looper.getMainLooper())
    private val worker = Executors.newSingleThreadExecutor()
    private lateinit var policy: PollPolicy
    private var snapshot = Snapshot()
    @Volatile private var busy = false
    private var ticking = false

    private val tick = object : Runnable {
        override fun run() {
            poll()
            render()
            handler.postDelayed(this, TICK_MS)
        }
    }

    private val screen = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (intent.action == Intent.ACTION_SCREEN_OFF) stopTicking() else startTicking()
        }
    }

    override fun onCreate() {
        super.onCreate()
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "Glucose", NotificationManager.IMPORTANCE_LOW).apply {
            description = "Current glucose, kept up to date for the widget. Silent; no alerts."
            setShowBadge(false)
        })
        policy = Store.loadPolicy(this)
        snapshot = Store.loadSnapshot(this)
        registerReceiver(screen, IntentFilter().apply { addAction(Intent.ACTION_SCREEN_ON); addAction(Intent.ACTION_SCREEN_OFF) })
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val notification = notification()
        if (Build.VERSION.SDK_INT >= 34) startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(NOTIFICATION_ID, notification)
        if (Store.header(this) == null) { stopSelf(); return START_NOT_STICKY }
        policy = Store.loadPolicy(this)
        if (getSystemService(PowerManager::class.java).isInteractive) startTicking()
        return START_STICKY
    }

    private fun startTicking() {
        if (ticking) return
        ticking = true
        handler.post(tick)
    }
    private fun stopTicking() {
        ticking = false
        handler.removeCallbacks(tick)
    }

    override fun onDestroy() {
        stopTicking()
        try { unregisterReceiver(screen) } catch (e: Exception) { }
        worker.shutdownNow()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun poll() {
        val now = System.currentTimeMillis()
        val server = Store.server(this); val header = Store.header(this)
        if (server == null || header == null) { update(snapshot.with(status = "Open N8 Glucose to connect", error = true)); return }
        if (policy.unauthorized) { update(snapshot.with(status = "Authentication failed · update in app", error = true)); return }
        if (busy || !policy.canPoll(now)) return
        busy = true
        policy.begin(now); Store.savePolicy(this, policy)
        worker.execute {
            var next = snapshot
            try {
                val reading = Gluroo.entries(server, header, System.currentTimeMillis())
                val after = System.currentTimeMillis()
                next = when {
                    reading == null -> next.with(status = "No readings returned", error = false)
                    next.reading != null && reading.time < next.reading!!.time -> next.with(status = "Older data returned · last reading kept", error = false)
                    else -> next.with(reading = reading, status = "Gluroo connected", error = false)
                }
                policy.success(after, next.reading?.time)
                if (after - next.iobTime >= IOB_POLL_MS - 5_000) {
                    try { next = next.with(iob = Gluroo.iob(server, header), iobTime = System.currentTimeMillis()) }
                    catch (e: ApiException) { if (e.status == 429) { policy.fail(429, after); next = next.with(status = "Daily API limit · paused until 00:00 UTC", error = true) } }
                    catch (e: Exception) { /* IOB is optional; glucose is unaffected */ }
                }
            } catch (e: ApiException) {
                policy.fail(e.status, System.currentTimeMillis())
                next = next.with(error = true, status = when {
                    e.status == 401 || e.status == 403 -> "Authentication failed · update in app"
                    e.status == 429 -> "Daily API limit · paused until 00:00 UTC"
                    e.status in 300..399 || e.status == 404 -> "Check the Gluroo URL in the app"
                    else -> "Gluroo unavailable · retry scheduled"
                })
            } catch (e: IllegalArgumentException) {
                policy.fail(0, System.currentTimeMillis()); next = next.with(status = "Unexpected response · retry scheduled", error = true)
            } catch (e: Exception) {
                policy.fail(0, System.currentTimeMillis()); next = next.with(status = "No connection · retry scheduled", error = true)
            }
            Store.savePolicy(this, policy)
            handler.post { busy = false; update(next) }
        }
    }

    private fun update(s: Snapshot) {
        snapshot = s
        Store.saveSnapshot(this, s)
        render()
    }

    /** Redraws without network: ages and staleness move on every tick. */
    private fun render() {
        GlucoseWidget.updateAll(this, snapshot)
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notification())
    }

    private fun notification(): Notification {
        val now = System.currentTimeMillis()
        val mmol = Store.mmol(this)
        val r = snapshot.reading
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val builder = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat)
            .setOngoing(true).setOnlyAlertOnce(true).setShowWhen(false)
            .setCategory(Notification.CATEGORY_STATUS)
            .setContentIntent(open)
        if (r == null) return builder.setContentTitle("N8 Glucose").setContentText(snapshot.status).build()
        val stale = now - r.time >= Snapshot.STALE_MS
        val title = Format.value(r.mgdl, mmol) + " " + Format.unit(mmol) + "  " + r.arrow + (if (stale) "" else Format.delta(r, mmol)?.let { "   $it" } ?: "") + (if (stale) "  · STALE" else "")
        val parts = ArrayList<String>()
        if (!stale) (Format.threshold(r, mmol) ?: Format.estimate(r, mmol))?.let { parts.add(it) }
        if (snapshot.iobShowable(now)) parts.add("IOB " + Format.iob(snapshot.iob!!) + "u")
        parts.add(Format.age(now - r.time))
        if (snapshot.error) parts.add(snapshot.status)
        return builder.setContentTitle(title).setContentText(parts.joinToString(" · ")).build()
    }
}
