# N8 Glucose for Android

A home-screen widget (plus a small silent notification) showing Gluroo glucose, trend arrow, change per 5 minutes, a 20-minute estimate range, a low/high notice and insulin on board. It uses the same read-only Gluroo requests, estimate and notices as the Windows overlay.

**Secondary display only.** It never alerts, never changes anything in Gluroo, and never talks to the pump. Keep relying on Dexcom, Gluroo and pump alarms.

## How it behaves

- **Fetches only while the screen is on**, and immediately when the screen turns on. When the screen is off nobody can see the widget, so it makes no requests and uses almost no battery.
- One glucose request per CGM reading (about every 5 minutes, never closer than 2 minutes), plus Gluroo's IOB at most every 10 minutes. Backs off on errors, stops on a rejected token, and pauses until 00:00 UTC if Gluroo's daily limit is hit. Gluroo allows 5,000 requests per account per day; each phone or PC running N8 uses a few hundred.
- Android requires a visible notification for an app that keeps a widget this current. It is silent and low-priority, and doubles as a glance at the latest reading.
- The Gluroo URL and token stay on the phone. The token is turned into the API secret header and stored encrypted with a key held in the phone's keystore. Nothing is backed up or transferred to a new phone.

## Install

Android 8.0 or newer. The app is installed from an APK file, not the Play Store.

1. Copy `N8-Glucose-Android-1.1.0.apk` to the phone (USB cable, Google Drive, or email to yourself).
2. Open it on the phone. Android asks to allow installs from that source (Files, Drive, or Gmail); allow it for this install. Google Play Protect may warn that it does not recognise the app; choose **Install anyway**.
3. Open **N8 Glucose**, enter the Gluroo Nightscout URL (`https://your-id.ns.gluroo.com`) and the **API Secret Token**, and tap **Save and start**. The app makes one test request and reports whether Gluroo accepted it. Allow notifications when asked.
4. Add the widget: long-press the home screen › **Widgets** › **N8 Glucose**, or tap **Add widget to home screen** in the app. It can be resized.
5. On phones that close background apps aggressively (Samsung, Xiaomi, OnePlus and others), tap **Battery settings for N8 Glucose** and set battery use to **Unrestricted**.

It restarts itself after a reboot. **Stop** pauses it; **Forget connection** removes the URL and token from the phone. Uninstall like any app.

## Widget states

| Shows | Meaning |
|---|---|
| `CURRENT` (teal) | Latest reading is under 10 minutes old. |
| `STALE` (amber) | Latest reading is 10 minutes old or more; estimate and change are hidden. |
| `OFFLINE` (red) | The last request failed; the line below says why. |
| `est. ~178 (150–205) in 20m` | 20-minute estimate fitted to her own readings (same as the Windows overlay), with a range that held the later reading about 80% of the time. `est. — noisy readings` when readings are chaotic. |
| `Heading below 70 in ~15m · estimate` | Amber notice when the estimate reaches 70 within 30 minutes while falling. Not an alarm. |
| `Low possible now · check finger stick` | CGM at or below 85 and falling (or at or below 70 and not rising): in a fast fall the real level can be well below the CGM. |
| `IOB 1.35 u` | Gluroo's insulin on board, hidden once 25 minutes old. Only as current as the pump data in Gluroo (Tandem uploads about hourly). |

## Build from source

Needs JDK 17 and the Android SDK (platform 35, build tools 35). From `android\`:

```bat
gradlew.bat testDebugUnitTest assembleRelease
```

Release signing reads a properties file named by `n8.signing=` in `local.properties` (`storeFile`, `storePassword`, `keyAlias`, `keyPassword`); without it the release APK is unsigned. Updates must be signed with the same key as the installed copy, or Android refuses them. The unit tests in `app/src/test` mirror the Windows overlay's forecast checks so both apps agree.
