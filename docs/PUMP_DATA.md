# Getting pump data into Gluroo (for the IOB row)

N8's glucose row only needs Gluroo. The **IOB** row and **pump data age** need the pump's basal and bolus records to reach Gluroo too, because N8 shows Gluroo's own insulin-on-board figure. N8 never talks to the pump; it only reads Gluroo.

Tandem pumps (t:slim X2, Mobi) upload to Tandem Source through the phone app, roughly **once an hour**. Nothing on the PC can make that faster, so IOB in Gluroo can be up to about an hour behind the pump. The pump screen is always the authoritative IOB.

There are two ways to copy Tandem Source data into Gluroo. **Use only one.** Running both uploads every bolus twice, which doubles Gluroo's IOB.

## Option A: Gluroo's built-in Tandem connection (simplest)

In Gluroo, open the insulin delivery settings, choose your Tandem pump, and sign in to t:connect / Tandem Source there. Also set **Menu > Settings > Insulin > Rapid Acting** to the insulin in the pump, which Gluroo needs to calculate IOB. N8 has not been tested against this route, but it reads the same Gluroo endpoints, so the IOB row should work. The pump-data age is then estimated from Gluroo's newest pump record and shown with `~`.

## Option B: tconnectsync on one Windows PC

[tconnectsync](https://github.com/jwoglom/tconnectsync) is an open-source tool that reads Tandem Source and uploads to any Nightscout-compatible site, including Gluroo. Running it on the PC that also runs N8 gives an exact pump-data age, read from its log. Run it on **one PC only**; other PCs running N8 still get IOB from Gluroo.

It is configured here for `BASAL BOLUS` only. That keeps it to adding records: it never deletes anything in Gluroo and never contacts the pump.

### Install

1. Install [Python](https://www.python.org/downloads/windows/) 3.11 or newer (tested with 3.14) and tick **Add python.exe to PATH**.
2. Copy the files from [`tools/tconnectsync`](../tools/tconnectsync) to `%LOCALAPPDATA%\tconnectsync`.
3. In a Command Prompt:

   ```bat
   cd /d "%LOCALAPPDATA%\tconnectsync"
   python -m venv venv
   venv\Scripts\python.exe -m pip install tconnectsync==3.0.3
   powershell -NoProfile -ExecutionPolicy Bypass -File apply-patches.ps1
   powershell -NoProfile -ExecutionPolicy Bypass -File setup-secrets.ps1
   ```

   `setup-secrets.ps1` asks for the Tandem Source login (the account the pump uploads to), region, time zone and Gluroo's **plain API Secret Token**. It writes `.env` readable only by your Windows account. If N8 is already set up, it reuses N8's Gluroo URL and checks the token matches.

4. Try it once in the foreground:

   ```bat
   venv\Scripts\python.exe -m tconnectsync --features BASAL BOLUS
   ```

   It should log in, find the pump, and upload recent basal and bolus records. Check that they appear in Gluroo.

5. Start it at sign-in: press `Win+R`, open `shell:startup`, and put a shortcut to `start-hidden.vbs` there (or copy the file). It runs `run-sync.cmd` without a window, checks Tandem Source every 120 seconds, logs to `sync.log`, and restarts after 60 seconds if it ever stops. Then sign out and back in, or double-click `start-hidden.vbs`.

### The two patches

`apply-patches.ps1` makes two small changes to tconnectsync 3.0.3. Both are still needed as of tconnectsync's current main branch:

- **Clock skew on login.** If the PC's clock is a few seconds behind Tandem's, login fails with `ImmatureSignatureError: The token is not yet valid` and syncing stops. The patch allows 60 seconds of skew.
- **Local end time.** On a PC not set to UTC, the end of the sync window was treated as UTC, which can give the newest basal record a negative duration. The patch treats it as local time.

`pip install --upgrade tconnectsync` overwrites both; run `apply-patches.ps1` again afterwards. If a future version changes the code, the script refuses to patch and says so.

### Checking that uploads are regular

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\tconnectsync\upload-gaps.ps1"
```

This read-only script lists each new upload from the pump, when this PC first saw it (**lag**) and the time since the previous one (**gap**). Typical values are a gap of 60-70 minutes and a lag of 2-7 minutes.

- A **gap** well over 75 minutes points to the phone: keep the Tandem app running (do not swipe it away), keep Bluetooth on, and keep the pump battery charged.
- A large **lag** points to the PC: check that tconnectsync is running and read the end of `sync.log`.

### Stopping or removing it

Remove the Startup entry, then end the hidden `cmd.exe` and `python.exe` processes in Task Manager (or sign out). Delete `%LOCALAPPDATA%\tconnectsync` to remove it completely, including the saved Tandem password.
