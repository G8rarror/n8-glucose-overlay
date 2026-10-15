using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace N8
{
    internal static class Verification
    {
        private static readonly List<string> results = new List<string>();
        private static string Folder(string[] args)
        {
            int index = Array.IndexOf(args, "--output");
            string path = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification");
            Directory.CreateDirectory(path); return path;
        }
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            results.Add("PASS " + name);
        }
        private static void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; } catch (FormatException) { rejected = true; }
            Check(rejected, name);
        }
        public static int Run(string[] args)
        {
            string folder = Folder(args);
            try
            {
                Check(Credentials.ValidateServer("https://test.ns.gluroo.com/") == "https://test.ns.gluroo.com", "Normalize genuine Gluroo origin");
                foreach (string url in new[] { "http://test.ns.gluroo.com", "https://test.ns.gluroo.com.evil.test", "https://evil.test", "https://secret@test.ns.gluroo.com", "https://test.ns.gluroo.com?token=secret", "https://test.ns.gluroo.com/api/v1/", "https://test.ns.gluroo.com:444", "https://test.ns.gluroo.com/#secret" })
                    Reject(delegate { Credentials.ValidateServer(url); }, "Reject unsafe origin: " + url.Replace("secret", "example"));
                string header = Credentials.Header("test", false);
                Check(header == "a94a8fe5ccb19ba61c4c0873d391e987982fbbd3", "SHA-1 header matches known vector");
                Check(Credentials.Header(header.ToUpperInvariant(), true) == header, "Pasted SHA-1 header is not hashed again");
                Reject(delegate { Credentials.Header("", false); }, "Empty token rejected");
                Reject(delegate { Credentials.Header("not-hex", true); }, "Malformed SHA-1 rejected");
                string protectedValue = Credentials.Protect(header);
                Check(protectedValue != header && Credentials.Unprotect(protectedValue) == header, "Windows CurrentUser DPAPI round trip");
                var settings = new Settings { Server = "https://test.ns.gluroo.com", ProtectedCredential = protectedValue, X = -123, Mmol = true };
                var store = new SettingsStore(Path.Combine(folder, "test-profile")); store.Save(settings); store.Save(settings);
                var loaded = store.Load();
                Check(loaded.X == -123 && loaded.Mmol && Credentials.Unprotect(loaded.ProtectedCredential) == header, "Atomic settings replacement and encrypted credential round trip");
                Check(!File.ReadAllText(store.FilePath).Contains(header), "Credential absent from plaintext settings");
                loaded.PollSeconds = 1; loaded.ScalePercent = 900; loaded.StaleMinutes = -1; loaded.Validate();
                Check(loaded.PollSeconds == 120 && loaded.ScalePercent == 200 && loaded.StaleMinutes == 5, "Out-of-range settings clamped");
                var now = new DateTimeOffset(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);
                const long ms = 1792086900000; // 2026-10-15 17:55 UTC
                string sample = "[{\"sgv\":142,\"date\":" + ms + ",\"direction\":\"Flat\",\"trend\":1,\"type\":\"sgv\",\"device\":\"gluroo\"}]";
                var reading = Reading.Parse(sample, now);
                Check(reading.Mgdl == 142 && reading.Arrow == "→", "Prefer documented direction over numeric trend");
                Check(reading.Time == new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms), "Epoch milliseconds retained as measurement timestamp");
                Check(reading.Value(true) == "7.9", "mg/dL to mmol/L display conversion");
                Check(Reading.Parse("[{\"sgv\":100,\"dateString\":\"2026-10-15T13:55:00-04:00\",\"trend\":7}]", now).Arrow == "↓↓", "ISO offset timestamp and Gluroo trend fallback");
                for (int trend = 1; trend <= 7; trend++)
                    Check(Reading.Parse("[{\"sgv\":100,\"date\":" + ms + ",\"trend\":" + trend + "}]", now).Arrow != "?", "Gluroo numeric trend " + trend);
                Check(Reading.Parse("[{\"sgv\":100,\"mills\":" + ms + "}]", now).Arrow == "?", "Unknown trend never rendered as flat");
                Check(Reading.Parse("[]", now) == null, "Empty API result is distinct from a zero reading");
                var display = new DisplayState(); display.Accept(reading);
                display.Accept(new Reading { Mgdl = 999, Time = reading.Time.AddMinutes(-1) });
                Check(display.Reading == reading, "Older server result never replaces last known reading");
                display.Accept(null); Check(display.Reading == reading, "Empty result retains last known reading and timestamp");
                Reject(delegate { Reading.Parse("<html>bad gateway</html>", now); }, "Malformed JSON rejected as incompatible response");
                Reject(delegate { Reading.Parse("{\"error\":\"x\"}", now); }, "Unexpected response shape rejected");
                Reject(delegate { Reading.Parse("[{\"sgv\":0,\"date\":" + ms + "}]", now); }, "Invalid glucose rejected");
                Reject(delegate { Reading.Parse("[{\"sgv\":100}]", now); }, "Missing timestamp rejected");
                Reject(delegate { Reading.Parse("[{\"sgv\":100,\"dateString\":\"2099-01-01T00:00:00Z\"}]", now); }, "Future timestamp rejected");
                Check(Reading.Parse("[{\"sgv\":90,\"date\":" + (ms - 60000) + "},{\"sgv\":110,\"date\":" + ms + "}]", now).Mgdl == 110, "Newest valid reading selected independent of order");
                reading.Time = now.AddMinutes(-10);
                Check(reading.IsStale(now, 10) && !reading.IsStale(now.AddSeconds(-1), 10), "Staleness boundary uses reading time");
                Check(OverlayWindow.Age(TimeSpan.FromSeconds(-1)) == "Clock ahead", "Future clock discrepancy made visible");
                var policy = new PollPolicy(); policy.Begin(now, 1);
                Check(!policy.CanPoll(now.AddSeconds(119)) && policy.CanPoll(now.AddSeconds(120)), "Minimum two-minute interval, including manual refresh");
                policy.Fail(401, now); Check(!policy.CanPoll(now.AddDays(1)), "Authentication failure suspends polling");
                policy = new PollPolicy(); policy.Fail(429, now);
                var midnight = new DateTimeOffset(2026, 10, 16, 0, 0, 5, TimeSpan.Zero);
                Check(policy.Next == midnight && !policy.CanPoll(midnight.AddSeconds(-1)), "429 pauses until next UTC day");
                Check(RateRecord.From(policy).ToPolicy().Next == midnight, "Daily rate pause can survive restart");
                policy = new PollPolicy(); policy.Fail(503, now); policy.Fail(503, now); Check(policy.Next == now.AddSeconds(240), "Transient failures use exponential backoff");
                HttpTests(header).GetAwaiter().GetResult();
                using (var image = OverlayWindow.Draw(new Settings(), new DisplayState { Reading = reading }, now, 1))
                    Check(image.GetPixel(0, 0).A == 0 && image.GetPixel(170, 115).A > 0, "Per-pixel transparent corners and translucent panel");
                using (var image = OverlayWindow.Draw(new Settings { PanelOpacity = 0 }, new DisplayState(), now, 1))
                    Check(image.GetPixel(170, 115).A == 0, "Zero-opacity panel creates text-only overlay");
                results.Add("RESULT " + results.Count + " checks passed. Mocked API; no live account contacted.");
                File.WriteAllLines(Path.Combine(folder, "self-test.txt"), results); return 0;
            }
            catch (Exception ex) { results.Add("FAIL " + ex.GetType().Name + ": " + ex.Message); File.WriteAllLines(Path.Combine(folder, "self-test.txt"), results); return 1; }
        }
        private sealed class Stub : HttpMessageHandler
        {
            public Func<HttpRequestMessage, HttpResponseMessage> Respond;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { return Task.FromResult(Respond(request)); }
        }
        private static async Task HttpTests(string header)
        {
            var stub = new Stub { Respond = delegate(HttpRequestMessage request)
            {
                Check(request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri == "https://test.ns.gluroo.com/api/v1/entries.json?count=1", "Read-only documented JSON endpoint");
                Check(string.Join("", request.Headers.GetValues("api-secret")) == header && !request.RequestUri.Query.Contains(header), "Credential sent only in api-secret header");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"sgv\":123,\"dateString\":\"2026-10-15T00:00:00Z\",\"trend\":4}]") };
            } };
            using (var client = new GlurooClient(stub)) Check((await client.FetchAsync("https://test.ns.gluroo.com", header, CancellationToken.None)).Mgdl == 123, "HTTP parsing integration");
            foreach (int status in new[] { 401, 429, 302, 503 })
            {
                using (var client = new GlurooClient(new Stub { Respond = delegate { return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("not JSON") }; } }))
                {
                    bool failed = false;
                    try { await client.FetchAsync("https://test.ns.gluroo.com", header, CancellationToken.None); }
                    catch (ApiException ex) { failed = ex.StatusCode == status; }
                    Check(failed, "HTTP " + status + " handled before parsing error body");
                }
            }
            using (var client = new GlurooClient(new Stub { Respond = delegate { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65537)) }; } }))
            {
                bool failed = false;
                try { await client.FetchAsync("https://test.ns.gluroo.com", header, CancellationToken.None); } catch (FormatException) { failed = true; }
                Check(failed, "Oversized response rejected");
            }
        }
        public static int Preview(string[] args)
        {
            string folder = Folder(args);
            var now = DateTimeOffset.UtcNow;
            var state = new DisplayState { Reading = new Reading { Mgdl = 112, Direction = "Flat", Time = now.AddMinutes(-2) }, Status = "Gluroo connected · click-through locked" };
            SaveImage(folder, "overlay-current.png", new Settings(), state, now);
            state.Reading.Time = now.AddMinutes(-17); state.Error = true; state.Status = "Connection unavailable · retry scheduled";
            SaveImage(folder, "overlay-stale.png", new Settings(), state, now);
            state.Demo = true; state.Error = false; state.Reading.Time = now.AddSeconds(-35); state.Status = "SIMULATED · no connection to Gluroo";
            SaveImage(folder, "overlay-demo.png", new Settings(), state, now);
            SaveImage(folder, "overlay-text-only.png", new Settings { PanelOpacity = 0 }, state, now);
            using (var icon = OverlayApp.MakeIcon()) using (var file = File.Create(Path.Combine(folder, "N8.ico"))) icon.Save(file);
            return 0;
        }
        private static void SaveImage(string folder, string name, Settings settings, DisplayState state, DateTimeOffset now)
        {
            using (var bitmap = OverlayWindow.Draw(settings, state, now, 1)) bitmap.Save(Path.Combine(folder, name), ImageFormat.Png);
        }
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flag);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        public static int Smoke(string[] args)
        {
            string folder = Folder(args);
            int result = 0;
            using (var app = new OverlayApp(Path.Combine(folder, "smoke-profile"), true, true))
            using (var behind = new Form { Text = "N8 verification surface", TopMost = true, StartPosition = FormStartPosition.Manual, Location = new Point(10, 10), Size = new Size(700, 400) })
            using (var timer = new System.Windows.Forms.Timer { Interval = 1500 })
            {
                behind.Show(); behind.Activate();
                timer.Tick += delegate
                {
                    timer.Stop();
                    try
                    {
                        app.Config.X = 40; app.Config.Y = 60; app.Config.Locked = true; app.Overlay.Apply(app.Config);
                        app.Overlay.KeepOnTop();
                        int style = Native.GetWindowLong(app.Overlay.Handle, -20);
                        Check((style & Native.Layered) != 0 && (style & Native.Transparent) != 0 && (style & Native.NoActivate) != 0 && (style & 8) != 0, "Native layered, click-through, no-activate, topmost styles");
                        var point = new Native.Point(app.Overlay.Left + 100, app.Overlay.Top + 70);
                        IntPtr underlying = GetAncestor(WindowFromPoint(point), 2);
                        Check(underlying != IntPtr.Zero && underlying != app.Overlay.Handle, "Locked overlay passes hit testing to underlying window");
                        results.Add("INFO Hotkeys available: " + app.Overlay.HotkeysAvailable + "; registration errors: " + app.Overlay.LockHotkeyError + ", " + app.Overlay.VisibilityHotkeyError);
                        IntPtr foreground = GetForegroundWindow(); app.Overlay.Hide(); app.Overlay.Show(); app.Overlay.Render();
                        Check(GetForegroundWindow() == foreground, "Showing overlay does not steal keyboard focus");
                        app.Config.Locked = false; app.Overlay.Apply(app.Config);
                        Check(GetAncestor(WindowFromPoint(point), 2) == app.Overlay.Handle, "Move mode receives mouse hit testing");
                        app.Config.X = 99999; app.Config.Y = -99999; app.Overlay.Apply(app.Config);
                        Check(Screen.FromRectangle(app.Overlay.Bounds).WorkingArea.Contains(app.Overlay.Bounds), "Offscreen position recovered to connected monitor");
                        app.Config.Locked = true; app.Overlay.Apply(app.Config);
                        using (var settings = new SettingsWindow(app))
                        {
                            settings.Show(); settings.Refresh();
                            Button saveButton = null;
                            foreach (Control parent in settings.Controls)
                                foreach (Control child in parent.Controls)
                                    if (child is Button && child.Text == "Save & connect") saveButton = (Button)child;
                            Check(saveButton != null && saveButton.Visible && saveButton.Parent.ClientRectangle.Contains(saveButton.Bounds), "Save button visible and inside settings footer");
                            foreach (Control control in settings.Controls)
                            {
                                var tabs = control as TabControl;
                                if (tabs == null) continue;
                                for (int tab = 0; tab < tabs.TabCount; tab++)
                                {
                                    tabs.SelectedIndex = tab; settings.Refresh();
                                    using (var bitmap = new Bitmap(settings.Width, settings.Height)) { settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, settings.Size)); bitmap.Save(Path.Combine(folder, "settings-" + tab + ".png")); }
                                }
                            }
                            saveButton.PerformClick();
                            Check(File.Exists(Path.Combine(folder, "smoke-profile", "settings.json")), "Settings Save action persists through the actual UI");
                            settings.Close();
                        }
                        string authRoot = Path.Combine(folder, "auth-recovery-profile");
                        var authSettings = new Settings { Server = "https://test.ns.gluroo.com", ProtectedCredential = Credentials.Protect(Credentials.Header("old-test-token", false)) };
                        new SettingsStore(authRoot).Save(authSettings);
                        var paused = new PollPolicy { Unauthorized = true, Next = DateTimeOffset.UtcNow.AddMinutes(2), LastAttempt = DateTimeOffset.UtcNow };
                        File.WriteAllText(Path.Combine(authRoot, "request-limits.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new Dictionary<string, RateRecord> { { authSettings.Server, RateRecord.From(paused) } }));
                        using (var recovery = new OverlayApp(authRoot, true, true))
                        {
                            var corrected = recovery.Config.Copy(); corrected.ProtectedCredential = Credentials.Protect(Credentials.Header("new-test-token", false));
                            recovery.ApplySettings(corrected, true);
                            var savedRates = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, RateRecord>>(File.ReadAllText(Path.Combine(authRoot, "request-limits.json")));
                            Check(!savedRates[authSettings.Server].Unauthorized && savedRates[authSettings.Server].NextTicks == paused.Next.UtcTicks, "Changed credential persists authorization reset before next request without bypassing cooldown");
                            recovery.ExitThread();
                        }
                        results.Add("RESULT Native UI smoke checks passed. Demo mode; no live account contacted.");
                    }
                    catch (Exception ex) { results.Add("FAIL " + ex.GetType().Name + ": " + ex.Message); result = 1; }
                    finally { File.WriteAllLines(Path.Combine(folder, "smoke-test.txt"), results); behind.Close(); app.ExitThread(); }
                };
                timer.Start(); Application.Run(app);
            }
            return result;
        }
    }
}
