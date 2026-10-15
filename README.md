# N8 Glucose Overlay

A small native Windows 11 application displaying Gluroo Global Connect glucose readings over your desktop and borderless-windowed games. It includes a transparent, always-on-top, click-through overlay, tray controls, trend arrows, local measurement timestamps, continuously updated reading age, and visible stale/offline states.

[Download the Windows release](https://github.com/G8rarror/n8-glucose-overlay/releases/tag/v1.0.0)

![N8 Glucose Overlay displaying simulated data](docs/overlay-demo.png)

## Run

1. Extract **N8-Glucose-Overlay-1.0.0.zip** to a folder you can write to.
2. Double-click **N8GlucoseOverlay.exe**. No administrator rights are needed.
3. In Gluroo, open **Connections → DIY / Nightscout (Loop)** and generate or copy your Nightscout URL and credential. Some versions place this under Settings → Gluroo Global Connect Nightscout.
4. Paste the exact base URL, such as `https://your-id.ns.gluroo.com`, into Settings. Paste the **API Secret Token** with the plain format selected, or choose **API Secret Header (SHA-1)** and paste its 40-character value.
5. Select **Save & connect**. The first request starts within a second when permitted by the request cooldown. If Gluroo has readings, the overlay displays the latest one. If it is empty, confirm readings are reaching Gluroo on your phone.

The Windows 11 built-in .NET Framework 4.8/4.8.1 is used. No SDK, Node, Python, WebView, account registration, or separate hosting is required to run the release. The executable is unsigned; Windows may show its normal publisher/reputation prompt. Review the included source and build locally if preferred.

To try the layout without credentials, check **Offline demo** in Settings, or run:

```powershell
.\N8GlucoseOverlay.exe --demo
```

Demo readings are labeled **DEMO / SIMULATED** and make no network requests. Demo mode lasts only for the current session. To connect to Gluroo, uncheck demo and save your connection.

## Install (optional)

Portable mode works directly from the extracted folder. To add a Start menu entry, double-click **Install.cmd**. It copies the application into `%LOCALAPPDATA%\Programs\N8 Glucose Overlay`; it does not require elevation and does not start the app automatically.

For optional sign-in startup and a desktop shortcut, open PowerShell in the extracted release folder and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -StartWithWindows -DesktopShortcut
```

Then open **N8 Glucose Overlay** from Start. To update, exit the app from the tray, extract the new release, and run its installer. Existing settings are kept. To disable sign-in startup, remove **N8 Glucose Overlay** from the Startup folder (`Win+R` → `shell:startup`).

## Controls

| Action | Control |
| --- | --- |
| Open settings | Double-click tray icon, or right-click → Settings |
| Lock / move | `Ctrl+Alt+G`, or tray → click-through locked / move mode |
| Reposition | Switch to move mode and drag the panel, then lock it |
| Hide / show | `Ctrl+Alt+H`, or tray menu |
| Recover position | Tray → Move to primary display |
| Choose a monitor | Settings → Overlay → display preset → Top right |
| Exact position | Settings → Overlay → X/Y in virtual desktop pixels; negative coordinates supported |
| Refresh | Tray → Refresh when allowed; cooldowns still apply |
| Exit | Tray → Exit |

Look under the tray's hidden-icons chevron if you do not see N8. Settings includes size (75–200%), panel opacity (0–95%), mg/dL or mmol/L, stale threshold (5–30 minutes), and polling interval (120–900 seconds). Opacity **0%** leaves only the text visible. Position and display choices persist. Disconnected or invalid monitor positions are moved back onto a connected display. Windows DPI changes are handled as the overlay moves between displays. Shortcuts may be unavailable in restricted desktop sessions or if another app has registered them; use the tray controls in that case.

## Reading states

- **CURRENT** means the measurement is newer than your configured stale threshold; it does not guarantee real-time delivery. The reading's local date/time and age are displayed below it.
- **STALE** appears when the measurement reaches that threshold. The last known value remains visible with an amber treatment.
- **OFFLINE** indicates a failed connection while a retained reading may still be recent. Once old enough, **STALE** takes priority and the connection error remains on the status line.
- **WAITING** means no reading is available. Unknown trends display `?`, rather than assuming a flat trend.
- Failed requests and empty results keep the last reading and its original timestamp. Older results cannot replace a newer reading. Future timestamps more than two minutes ahead are rejected; smaller discrepancies say **Clock ahead**.

The default polling interval is two minutes. Repeated network failures back off to fifteen minutes. Incorrect credentials suspend polling until you enter a changed credential or URL. For 401/403, verify the URL and credential pair or regenerate the connection in Gluroo. HTTP 429 suspends requests until 00:00 UTC the following day; the budget is shared with your other GGC clients. Closing settings, refreshing, and restarting do not bypass the saved cooldown. See [API_COMPATIBILITY.md](API_COMPATIBILITY.md) for the verified contract and testing boundary.

## Privacy and scope

The app sends a read-only HTTPS request directly to your Gluroo account origin. It stores only the SHA-1 API header, encrypted with Windows DPAPI for the current user, in `%LOCALAPPDATA%\N8GlucoseOverlay\settings.json`. The plain token is not saved. The header itself is a usable credential, so treat it as a password. DPAPI protects stored credentials from other Windows users, not from malicious programs running as you. The URL and appearance preferences are stored normally; request timestamps are in `request-limits.json`. Readings remain in memory and are cleared on exit. No telemetry, glucose history, or request/response logs are created.

Use this as a **secondary display only**. It provides no alarms, treatment recommendations, or device control. Use your CGM, pump, or meter directly for medical decisions. GGC is an experimental service and may change or stop responding. N8 is an independent application and is not affiliated with Gluroo.

This is a desktop composited overlay. Exclusive-fullscreen games, secure desktop/UAC prompts, and games that restrict overlays can cover it. Use borderless-windowed mode. It does not inject into games or interact with anti-cheat systems. Specific game compatibility and mixed-DPI hardware have not been manually tested.

## Uninstall

Exit N8 from the tray, then run from its installed folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall.ps1
```

To also remove saved settings and credentials, append `-RemoveData`. Otherwise these are retained. For a portable copy, exit and delete the extracted folder; optionally delete `%LOCALAPPDATA%\N8GlucoseOverlay` to remove its saved configuration. Rotate credentials in Gluroo if you want to revoke API access.

## Build from source

From a Windows 11 checkout, run **build.cmd**. The compiler included with Windows .NET Framework builds `build\N8GlucoseOverlay.exe`; no package restore or third-party packages are required. `package.cmd` builds, verifies, and creates the release ZIP. These commands write only to the checkout's generated directories.

```powershell
.\build.cmd
# WinForms programs should be launched with -Wait when checking their exit code:
Start-Process .\build\N8GlucoseOverlay.exe -ArgumentList '--self-test --output artifacts' -Wait
Start-Process .\build\N8GlucoseOverlay.exe -ArgumentList '--smoke-test --output artifacts' -Wait
```

Self-tests use fake credentials, mocked HTTP responses, Windows encryption, and temporary profiles under the output folder. Smoke tests briefly show their own test windows and inspect native click-through, focus, topmost, and position behavior; they need an interactive Windows desktop. Neither contacts Gluroo. Reports are written to `artifacts\self-test.txt` and `artifacts\smoke-test.txt`. `--preview --output artifacts` renders the same overlay drawing code as PNGs for layout inspection. Tests do not read or modify your normal N8 profile.
