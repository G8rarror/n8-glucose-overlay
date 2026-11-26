package com.n8.glucose

import android.Manifest
import android.app.Activity
import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.InputType
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.util.concurrent.Executors

/** Setup screen: Gluroo URL and token, units, start/stop. Built in code to keep the app dependency-free. */
class MainActivity : Activity() {
    private lateinit var url: EditText
    private lateinit var token: EditText
    private lateinit var hashed: CheckBox
    private lateinit var mmol: CheckBox
    private lateinit var status: TextView
    private val worker = Executors.newSingleThreadExecutor()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val pad = (16 * resources.displayMetrics.density).toInt()
        val column = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(pad, pad, pad, pad) }
        fun text(s: String, size: Float = 15f, bold: Boolean = false) = TextView(this).apply {
            text = s; textSize = size; if (bold) setTypeface(typeface, Typeface.BOLD); setPadding(0, pad / 2, 0, pad / 4)
        }
        column.addView(text("N8 Glucose", 24f, true))
        column.addView(text("A home-screen widget for Gluroo glucose, trend, a 20-minute estimate and insulin on board. Read-only: it never changes anything in Gluroo and never talks to the pump. It is a secondary display; rely on Dexcom, Gluroo and pump alarms.", 13f))
        column.addView(text("Gluroo Nightscout URL", 14f, true))
        url = EditText(this).apply { hint = "https://your-id.ns.gluroo.com"; inputType = InputType.TYPE_TEXT_VARIATION_URI; setSingleLine(); setText(Store.server(this@MainActivity) ?: "") }
        column.addView(url)
        column.addView(text("API Secret Token", 14f, true))
        token = EditText(this).apply {
            hint = if (Store.header(this@MainActivity) != null) "Saved · leave blank to keep" else "From Gluroo's Nightscout settings"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD; setSingleLine()
        }
        column.addView(token)
        hashed = CheckBox(this).apply { text = "This is the 40-character API Secret Header, not the token" }
        column.addView(hashed)
        mmol = CheckBox(this).apply { text = "Show mmol/L"; isChecked = Store.mmol(this@MainActivity) }
        column.addView(mmol)
        column.addView(Button(this).apply { text = "Save and start"; setOnClickListener { saveAndStart() } })
        column.addView(Button(this).apply { text = "Stop"; setOnClickListener {
            Store.setEnabled(this@MainActivity, false); GlucoseService.stop(this@MainActivity); status.text = "Stopped. The widget keeps the last reading, marked STALE once old."
        } })
        status = text("", 14f)
        column.addView(status)
        column.addView(text("Add the widget", 14f, true))
        column.addView(text("Long-press the home screen › Widgets › N8 Glucose. Or use the button below, if your launcher supports it.", 13f))
        column.addView(Button(this).apply { text = "Add widget to home screen"; setOnClickListener { pinWidget() } })
        column.addView(text("Keep it running", 14f, true))
        column.addView(text("Some phones stop background apps. Set N8 Glucose's battery use to Unrestricted. It only fetches while the screen is on, so the cost is small.", 13f))
        column.addView(Button(this).apply { text = "Battery settings for N8 Glucose"; setOnClickListener {
            try { startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName"))) } catch (e: Exception) { }
        } })
        column.addView(Button(this).apply { text = "Forget connection"; setOnClickListener {
            GlucoseService.stop(this@MainActivity); Store.clear(this@MainActivity); url.setText(""); token.setText("")
            GlucoseWidget.updateAll(this@MainActivity, Snapshot()); status.text = "Connection removed from this phone."
        } })
        val scroll = ScrollView(this).apply { addView(column); fitsSystemWindows = true }
        setContentView(scroll)
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        showState()
    }

    override fun onResume() { super.onResume(); showState() }
    override fun onDestroy() { worker.shutdown(); super.onDestroy() }

    private fun showState() {
        if (!::status.isInitialized) return
        val s = Store.loadSnapshot(this)
        val r = s.reading
        status.text = when {
            Store.header(this) == null -> "Not connected yet."
            !Store.enabled(this) -> "Connected, but stopped."
            r == null -> s.status
            else -> "Running · last reading " + Format.value(r.mgdl, Store.mmol(this)) + " " + Format.age(System.currentTimeMillis() - r.time) + " · " + s.status
        }
    }

    private fun saveAndStart() {
        val server: String; val header: String
        try {
            server = Gluroo.validateServer(url.text.toString())
            val typed = token.text.toString()
            header = if (typed.isBlank()) Store.header(this) ?: throw IllegalArgumentException("Enter the API Secret Token from Gluroo.")
                     else Gluroo.header(typed, hashed.isChecked)
        } catch (e: IllegalArgumentException) { status.text = e.message; return }
        val useMmol = mmol.isChecked
        status.text = "Checking with Gluroo…"
        worker.execute {
            // One test read, so a wrong URL or token shows up here rather than silently in the widget.
            val result = try {
                Gluroo.entries(server, header, System.currentTimeMillis()); null
            } catch (e: ApiException) {
                when (e.status) { 401, 403 -> "Gluroo rejected the token. Check the API Secret Token."; 429 -> "Gluroo's daily request limit is used up; try after 00:00 UTC."; else -> "Gluroo answered HTTP ${e.status}. Check the URL." }
            } catch (e: Exception) { "Could not reach Gluroo. Check the URL and the phone's connection." }
            runOnUiThread {
                if (result != null) { status.text = result; return@runOnUiThread }
                Store.saveConnection(this, server, header, useMmol)
                Store.setEnabled(this, true)
                token.setText(""); token.hint = "Saved · leave blank to keep"
                GlucoseService.start(this)
                status.text = "Connected. The widget and notification update every few minutes while the screen is on."
            }
        }
    }

    private fun pinWidget() {
        val manager = getSystemService(AppWidgetManager::class.java)
        if (manager.isRequestPinAppWidgetSupported) manager.requestPinAppWidget(ComponentName(this, GlucoseWidget::class.java), null, null)
        else status.text = "This launcher can't add widgets from an app; use long-press on the home screen."
    }
}
