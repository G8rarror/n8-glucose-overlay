package com.n8.glucose

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.view.View
import android.widget.RemoteViews

class GlucoseWidget : AppWidgetProvider() {
    companion object {
        private val TEAL = Color.rgb(103, 232, 204)
        private val AMBER = Color.rgb(255, 205, 112)
        private val RED = Color.rgb(255, 153, 143)
        private val MUTED = Color.rgb(160, 178, 192)

        fun updateAll(c: Context, s: Snapshot) {
            val manager = AppWidgetManager.getInstance(c)
            val ids = manager.getAppWidgetIds(ComponentName(c, GlucoseWidget::class.java))
            if (ids.isNotEmpty()) manager.updateAppWidget(ids, views(c, s))
        }

        fun views(c: Context, s: Snapshot): RemoteViews {
            val now = System.currentTimeMillis()
            val mmol = Store.mmol(c)
            val v = RemoteViews(c.packageName, R.layout.widget)
            v.setOnClickPendingIntent(R.id.root, PendingIntent.getActivity(c, 0, Intent(c, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE))
            val r = s.reading
            val stale = r != null && now - r.time >= Snapshot.STALE_MS
            val accent = when { s.error -> RED; stale -> AMBER; else -> TEAL }
            v.setTextViewText(R.id.badge, when { stale -> "STALE"; s.error -> "OFFLINE"; r == null -> "WAITING"; else -> "CURRENT" })
            v.setTextColor(R.id.badge, accent)
            v.setTextViewText(R.id.value, if (r == null) "—" else Format.value(r.mgdl, mmol))
            v.setTextColor(R.id.value, if (stale) AMBER else Color.WHITE)
            v.setTextViewText(R.id.arrow, r?.arrow ?: "?")
            v.setTextColor(R.id.arrow, accent)
            val delta = if (r == null || stale) null else Format.delta(r, mmol)
            v.setTextViewText(R.id.delta, Format.unit(mmol) + (delta?.let { "   $it" } ?: ""))
            val estimate = if (r == null || stale) null else Format.estimate(r, mmol)
            v.setTextViewText(R.id.estimate, estimate ?: "")
            v.setTextViewText(R.id.iob, if (s.iobShowable(now)) "IOB " + Format.iob(s.iob!!) + " u" else "IOB —")
            v.setTextViewText(R.id.age, if (r == null) "No reading yet" else Format.age(now - r.time))
            val threshold = if (r == null || stale || s.error) null else Format.threshold(r, mmol)
            val status = threshold ?: if (s.error || r == null) s.status else null
            v.setViewVisibility(R.id.status, if (status == null) View.GONE else View.VISIBLE)
            v.setTextViewText(R.id.status, status ?: "")
            v.setTextColor(R.id.status, if (threshold != null) AMBER else if (s.error) RED else MUTED)
            return v
        }
    }

    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        manager.updateAppWidget(ids, views(context, Store.loadSnapshot(context)))
        if (Store.enabled(context)) GlucoseService.start(context)
    }
}

class BootReceiver : android.content.BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if ((intent.action == Intent.ACTION_BOOT_COMPLETED || intent.action == Intent.ACTION_MY_PACKAGE_REPLACED) && Store.enabled(context)) GlucoseService.start(context)
    }
}
