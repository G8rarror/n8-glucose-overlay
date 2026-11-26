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
                Func<int[], int, string> series = delegate(int[] values, int stepMinutes)
                {
                    // values[0] is newest
                    var parts = new List<string>();
                    for (int i = 0; i < values.Length; i++) parts.Add("{\"sgv\":" + values[i] + ",\"date\":" + (ms - i * stepMinutes * 60000L) + "}");
                    return "[" + string.Join(",", parts) + "]";
                };
                var rising = Reading.Parse(series(new[] { 130, 120, 110, 100, 90, 80 }, 5), now);
                Check(rising.Delta == 10 && rising.DeltaText(false) == "+10", "Delta is change since previous reading");
                Check(rising.LinearForecast.HasValue && Math.Abs(rising.LinearForecast.Value - 170) < 0.01, "Straight line: steady rise of 2 mg/dL/min projects +40 in 20 minutes");
                Check(rising.Forecast > 145 && rising.Forecast < 170, "Damped trend projects less than the straight line on a steady rise");
                Check(rising.ForecastLow < rising.Forecast && rising.Forecast < rising.ForecastHigh && rising.ForecastHigh - rising.ForecastLow < 90, "Range brackets the estimate and stays reasonable (fitted: about 80 wide at 2+ mg/dL/min)");
                Check(rising.Rate > 1.6 && rising.Rate < 2.2, "Filtered rate near 2 mg/dL/min");
                Check(rising.ForecastText(true) != null && rising.DeltaText(true) == "+0.6" && rising.RangeText(true).Contains("–"), "Delta and range convert to mmol/L");
                Check(System.Text.RegularExpressions.Regex.IsMatch(rising.EstimateText(false), @"\Aest\. ~\d+ \(\d+–\d+\) in 20m\z"), "Estimate shows the middle first, then the range");
                var falling = Reading.Parse(series(new[] { 60, 75, 90, 105, 120 }, 5), now);
                Check(falling.DeltaText(false) == "-15" && falling.ForecastText(false) == "<40" && falling.RangeText(false).StartsWith("<40"), "Projection below CGM range shown as <40");
                Check(falling.LowInMinutes == null, "No low notice once already below 70");
                Check(Reading.Parse(series(new[] { 100, 100, 100, 100 }, 5), now).DeltaText(false) == "+0", "Flat delta");
                Check(Reading.Parse(series(new[] { 110, 100 }, 10), now).Delta == 5, "Delta normalized to 5 minutes across a missed reading");
                var gapped = Reading.Parse(series(new[] { 110, 100 }, 15), now);
                Check(gapped.Delta == null && gapped.Forecast == null && gapped.LinearForecast == null, "No delta or forecast across a long gap");
                Check(Reading.Parse(series(new[] { 110, 105 }, 5), now).Forecast == null, "Forecast needs at least three readings over ten minutes");
                Check(Reading.Parse("[{\"sgv\":110,\"date\":" + ms + "},{\"sgv\":105,\"date\":" + (ms - 600000) + "},{\"sgv\":100,\"date\":" + (ms - 1500000) + "}]", now).Forecast == null, "Forecast window stops at a gap over 7.5 minutes");
                var noisy = Reading.Parse(series(new[] { 100, 140, 95, 135 }, 5), now);
                Check(noisy.Forecast == null && noisy.LinearForecast == null && noisy.Noisy, "Noisy trace suppresses forecast and is flagged");
                var flat80 = Reading.Parse(series(new[] { 80, 80, 81, 80, 79, 80 }, 5), now);
                Check(flat80.LowInMinutes == null && flat80.HighInMinutes == null && flat80.Forecast > 78 && flat80.Forecast < 95, "Flat at 80: no threshold notice; fitted estimate drifts up slightly");
                var slide = Reading.Parse(series(new[] { 95, 100, 105, 110, 115, 120 }, 5), now);
                Check(slide.LowInMinutes == null && !slide.LowNow, "Gentle 1 mg/dL/min drift from 95 gives no notice (backtest: mostly false alarms)");
                var drop = Reading.Parse(series(new[] { 105, 120, 135, 150, 165, 180 }, 5), now);
                Check(drop.LowInMinutes >= 5 && drop.LowInMinutes <= 30 && drop.ThresholdText(false).StartsWith("Heading below 70 in ~"), "Falling 3 mg/dL/min from 105 warns of a coming low");
                var nearLow = Reading.Parse(series(new[] { 84, 88, 92, 96, 100, 104 }, 5), now);
                Check(nearLow.LowNow && nearLow.ThresholdText(false) == "Low possible now · check finger stick", "CGM at 84 and falling asks for a finger stick");
                var risingLow = Reading.Parse(series(new[] { 68, 60, 55, 52 }, 5), now);
                Check(!risingLow.LowNow && risingLow.LowInMinutes == null, "Recovering from a low gives no notice");
                var climb = Reading.Parse(series(new[] { 235, 225, 215, 205, 195, 185 }, 5), now);
                Check(climb.HighInMinutes >= 3 && climb.HighInMinutes <= 15 && climb.LowInMinutes == null && climb.ThresholdText(false).StartsWith("Above 250"), "Climbing 2 mg/dL/min from 235 notes a coming high");
                var easing = Reading.Parse(series(new[] { 100, 104, 112, 124, 138, 154 }, 5), now);
                Check(!easing.Noisy && easing.Forecast > 70 && easing.Forecast < 100, "A fast fall that is easing is not mistaken for noise");
                var smooth = Reading.Parse(series(new[] { 100, 110, 120, 130, 140, 150 }, 5), now);
                var uneven = Reading.Parse(series(new[] { 100, 100, 111, 111, 128, 150 }, 5), now);
                double m1, l1, h1, m4, l4, h4;
                var unevenWindow = Reading.ParseAll(series(new[] { 100, 100, 111, 111, 128, 150 }, 5), now);
                LearnedEstimate.Estimate(unevenWindow, -2, 1, out m1, out l1, out h1); LearnedEstimate.Estimate(unevenWindow, -2, 4, out m4, out l4, out h4);
                Check(!uneven.Noisy && uneven.Forecast != null && m1 == m4 && h4 - l4 > 1.9 * (h1 - l1), "Uneven fall keeps the estimate; rough readings widen the range");
                var steady = Reading.Parse(series(new[] { 120, 120, 120, 120, 120, 120 }, 5), now);
                Check(steady.Forecast > 118 && steady.Forecast < 128 && steady.ForecastLow < 105 && steady.ForecastHigh > 135, "Fitted estimate: flat at 120 stays near 120 with a range of about -19 to +22");
                var dampedOnly = TrendModel.Default.Copy(); dampedOnly.Learned = false;
                var highFlat = Reading.Parse(series(new[] { 260, 260, 260, 260, 260, 260 }, 5), now);
                Check(highFlat.Forecast < 255 && Reading.AnalyzeNewest(Reading.ParseAll(series(new[] { 260, 260, 260, 260, 260, 260 }, 5), now), dampedOnly).Forecast > 259, "Fitted estimate expects a flat high to come down; the damped trend alone does not");
                var logLines = new List<string> { ForecastLog.Header };
                for (int i = 0; i < 40; i++)
                {
                    // Readings every 5 minutes rising 5 each; every estimate says +15 from now with a range of ±10.
                    int value = 100 + 5 * i; var t = now.AddMinutes(-200 + 5 * i);
                    logLines.Add(ForecastLog.Row.Format(new Reading { Mgdl = value, Time = t, Forecast = value + 15, ForecastLow = value + 5, ForecastHigh = value + 25, LinearForecast = value + 25 }));
                }
                var score = ForecastLog.Score.From(logLines, now, 7);
                Check(score.Checked == 36 && score.Compared == 36 && Math.Abs(score.DampedError - 5) < 0.01 && Math.Abs(score.LinearError - 5) < 0.01 && score.InRange == 36, "Forecast log pairs each estimate with the reading 20 minutes later");
                Check(ForecastLog.Score.From(logLines, now.AddDays(30), 7).Checked == 0, "Forecast log ignores estimates older than the report window");
                string logRoot = Path.Combine(Path.GetTempPath(), "n8-forecast-test-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var testLog = new ForecastLog(logRoot);
                    var withMs = new Reading { Mgdl = 100, Time = new DateTimeOffset(2026, 11, 26, 19, 38, 49, 765, TimeSpan.Zero) };
                    Check(testLog.Record(withMs) && !testLog.Record(withMs) && !new ForecastLog(logRoot).Record(withMs), "Forecast log writes each reading once, also after restart");
                    Check(File.ReadAllLines(testLog.FilePath).Length == 2 && File.ReadAllLines(testLog.FilePath)[0] == ForecastLog.Header, "Forecast log has a header and one row");
                }
                finally { try { Directory.Delete(logRoot, true); } catch (IOException) { } }
                var duplicated = Reading.Parse("[{\"sgv\":120,\"date\":" + ms + "},{\"sgv\":120,\"date\":" + (ms - 5000) + "},{\"sgv\":110,\"date\":" + (ms - 300000) + "}]", now);
                Check(duplicated.Delta == 10, "Duplicate uploads of one reading ignored for delta");
                Check(Reading.Parse("[{\"sgv\":100,\"date\":" + ms + "}]", now).Delta == null, "Single reading has no delta");
                var treatments = TreatmentSummary.Parse(SampleTreatments(now), now);
                string treatmentReport = treatments.Report(now);
                Check(treatments.Total == 16 && treatments.TempBasals == 12 && treatments.WithInsulin == 3, "Treatments summary counts record types");
                Check(treatments.MentionsAutomatic >= 1 && treatments.MarkedAutomatic == 1 && treatments.FromTandem == 16, "Treatments summary detects automatic and Tandem records");
                Check(Math.Abs((treatments.Newest.Value - now.AddMinutes(-70)).TotalSeconds) < 1 && treatments.LastHour == 0 && treatments.Last3Hours == 16 && treatments.MedianGapMinutes == 5, "Treatments summary measures data lag and spacing");
                Check(!treatmentReport.Contains("1.37") && !treatmentReport.Contains("0.85") && !treatmentReport.Contains("private note") && !treatmentReport.Contains("parent@example"), "Treatments report omits doses, notes and email addresses");
                Check(treatmentReport.Contains("without anyone entering it") && treatmentReport.Contains("1 h 10 min ago"), "Treatments report explains automatic data and lag");
                var stale = TreatmentSummary.Parse("[{\"eventType\":\"Temp Basal\",\"rate\":1,\"date\":" + (ms - 3 * 3600000L) + "}]", now);
                Check(stale.Report(now).Contains("over 90 minutes"), "Old pump data flagged as lagging");
                var manualOnly = TreatmentSummary.Parse("[{\"eventType\":\"Meal Bolus\",\"insulin\":2,\"created_at\":\"2026-10-15T17:40:00Z\",\"enteredBy\":\"Gluroo\"}]", now);
                Check(manualOnly.TempBasals == 0 && manualOnly.Report(now).Contains("nothing looks automatic"), "Manual-only treatments flagged as not automatic");
                Check(TreatmentSummary.Parse("[]", now).Report(now).Contains("No treatments came back"), "Empty treatments explained");
                Reject(delegate { TreatmentSummary.Parse("{\"status\":401}", now); }, "Unexpected treatments shape rejected");
                var iob = InsulinStatus.Parse("[{\"glurooIob\":1.35,\"glurooCob\":0}]", now);
                Check(iob.Iob == 1.35 && iob.IobText() == "1.35" && iob.Fetched == now, "Gluroo IOB parsed from devicestatus");
                Check(InsulinStatus.Parse("{\"glurooIob\":\"0\",\"glurooCob\":0}", now).IobText() == "0.00", "IOB accepted as object and string, zero kept as zero");
                Check(InsulinStatus.Parse("[{\"device\":\"loop\"}]", now) == null && InsulinStatus.Parse("[]", now) == null, "Missing IOB is unknown, not zero");
                Reject(delegate { InsulinStatus.Parse("[{\"glurooIob\":500}]", now); }, "Implausible IOB rejected");
                Check(iob.IsShowable(now.AddMinutes(24)) && !iob.IsShowable(now.AddMinutes(25)), "IOB hidden once 25 minutes old");
                string log = "2026-11-20 07:46:09 INFO     New reported tandemsource data. (cur_max_date: 1795177996.0 last_max_date: 1795173821.0)\n" +
                    "2026-11-20 08:36:21 INFO     No new reported tandemsource data. cur_max_date: 2026-11-20T11:00:00+00:00 (63m ago) last_event_time: 2026-11-20T13:46:06.559715+00:00 (50m ago)\n";
                Check(PumpSync.Latest(log) == new DateTimeOffset(2026, 11, 20, 12, 33, 16, TimeSpan.Zero), "Pump data time read from both tconnectsync log formats, newest wins, last_event_time ignored");
                Check(PumpSync.Latest("nothing here") == null && PumpSync.Read(Path.Combine(folder, "no-such-folder")) == null, "Missing tconnectsync log means unknown pump data age");
                string glurooPump = "[{\"eventType\":\"Carb Correction\",\"created_at\":\"2026-10-15T17:58:00Z\",\"enteredBy\":\"parent\"}," +
                    "{\"eventType\":\"Correction Bolus\",\"created_at\":\"2026-10-15T17:59:00Z\",\"enteredBy\":\"parent\"}," +
                    "{\"eventType\":\"Temp Basal\",\"created_at\":\"2026-10-15T17:38:50.000Z\",\"enteredBy\":\"System\"}," +
                    "{\"eventType\":\"Combo Bolus\",\"created_at\":\"2026-10-15T17:20:00Z\",\"enteredBy\":\"System\"}]";
                Check(PumpSync.NewestPumpTreatment(glurooPump, now) == new DateTimeOffset(2026, 10, 15, 17, 38, 50, TimeSpan.Zero), "Gluroo fallback uses newest pump basal/bolus, ignores hand-entered records");
                Check(PumpSync.NewestPumpTreatment("[]", now) == null, "No pump treatments means unknown pump data age");
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
                policy = new PollPolicy(); policy.Begin(now, 120); policy.Schedule(now, now.AddSeconds(-30), 120);
                Check(policy.Next == now.AddSeconds(285), "On-time reading: next poll 5m15s after the reading");
                policy = new PollPolicy(); policy.Begin(now, 120); policy.Schedule(now, now.AddSeconds(-330), 120);
                Check(policy.Next == now.AddSeconds(120), "Late reading: retry at the 120 s floor");
                policy = new PollPolicy(); policy.Begin(now, 120); policy.Schedule(now, now.AddSeconds(-280), 300);
                Check(policy.Next == now.AddSeconds(300), "PollSeconds setting is never undercut");
                policy = new PollPolicy(); policy.Begin(now, 120); policy.Schedule(now, now.AddSeconds(90), 120);
                Check(policy.Next == now.AddSeconds(360), "Future-dated reading capped at 6 minutes");
                policy = new PollPolicy(); policy.Begin(now, 120); policy.Schedule(now, now.AddMinutes(-40), 120);
                Check(policy.Next == now.AddSeconds(300), "Signal loss falls back to 5 minutes");
                policy = new PollPolicy(); policy.Begin(now, 120); policy.Schedule(now, null, 120);
                Check(policy.Next == now.AddSeconds(300) && !policy.CanPoll(now.AddSeconds(299)), "No data falls back to 5 minutes");
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
                Check(request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri == "https://test.ns.gluroo.com/api/v1/entries.json?count=6", "Read-only documented JSON endpoint");
                Check(string.Join("", request.Headers.GetValues("api-secret")) == header && !request.RequestUri.Query.Contains(header), "Credential sent only in api-secret header");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"sgv\":123,\"dateString\":\"2026-10-15T00:00:00Z\",\"trend\":4}]") };
            } };
            using (var client = new GlurooClient(stub)) Check((await client.FetchAsync("https://test.ns.gluroo.com", header, CancellationToken.None)).Mgdl == 123, "HTTP parsing integration");
            var treatmentsStub = new Stub { Respond = delegate(HttpRequestMessage request)
            {
                Check(request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri == "https://test.ns.gluroo.com/api/v1/treatments.json?count=100" && request.Content == null, "Treatments check is a single read-only GET of the documented endpoint");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            } };
            using (var client = new GlurooClient(treatmentsStub)) Check(await client.FetchTreatmentsJsonAsync("https://test.ns.gluroo.com", header, CancellationToken.None) == "[]", "Treatments response returned for local summary");
            var iobStub = new Stub { Respond = delegate(HttpRequestMessage request)
            {
                Check(request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri == "https://test.ns.gluroo.com/api/v1/devicestatus.json?count=1" && request.Content == null, "IOB is a single read-only GET of the documented devicestatus endpoint");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"glurooIob\":0.4,\"glurooCob\":0}]") };
            } };
            using (var client = new GlurooClient(iobStub)) Check((await client.FetchIobAsync("https://test.ns.gluroo.com", header, CancellationToken.None)).Iob == 0.4, "IOB HTTP parsing integration");
            var pumpStub = new Stub { Respond = delegate(HttpRequestMessage request)
            {
                Check(request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri == "https://test.ns.gluroo.com/api/v1/treatments.json?count=5" && request.Content == null, "Pump-data fallback is a single small read-only GET of treatments");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"eventType\":\"Temp Basal\",\"created_at\":\"2026-10-15T00:00:00Z\",\"enteredBy\":\"System\"}]") };
            } };
            using (var client = new GlurooClient(pumpStub)) Check((await client.FetchPumpDataTimeAsync("https://test.ns.gluroo.com", header, CancellationToken.None)) == new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), "Pump-data fallback HTTP integration");
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
        /// <summary>Mock Control-IQ-like treatments: temp basals every 5 minutes, an automatic correction, a manual bolus. Newest 70 minutes old.</summary>
        internal static string SampleTreatments(DateTimeOffset now)
        {
            var parts = new List<string>();
            Func<int, string> at = delegate(int minutesAgo) { return now.AddMinutes(-minutesAgo).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture); };
            for (int i = 0; i < 12; i++)
                parts.Add("{\"eventType\":\"Temp Basal\",\"rate\":0.85,\"absolute\":0.85,\"duration\":5,\"created_at\":\"" + at(70 + i * 5) + "\",\"enteredBy\":\"Gluroo Tandem\"}");
            parts.Add("{\"eventType\":\"Correction Bolus\",\"insulin\":1.37,\"automatic\":true,\"notes\":\"Control-IQ technology automatic correction bolus\",\"created_at\":\"" + at(95) + "\",\"enteredBy\":\"Gluroo Tandem\"}");
            parts.Add("{\"eventType\":\"Meal Bolus\",\"insulin\":1.37,\"carbs\":30,\"notes\":\"private note\",\"created_at\":\"" + at(120) + "\",\"enteredBy\":\"Gluroo Tandem\"}");
            parts.Add("{\"eventType\":\"Bolus\",\"insulin\":0.85,\"created_at\":\"" + at(150) + "\",\"enteredBy\":\"parent@example.com via Tandem\"}");
            parts.Add("{\"eventType\":\"Note\",\"notes\":\"private note\",\"created_at\":\"" + at(160) + "\",\"enteredBy\":\"Tandem\"}");
            parts.Reverse();
            return "[" + string.Join(",", parts) + "]";
        }
        public static int Preview(string[] args)
        {
            string folder = Folder(args);
            var now = DateTimeOffset.UtcNow;
            string sampleReport = "SAMPLE FROM MOCKED DATA, NOT YOUR ACCOUNT" + Environment.NewLine + Environment.NewLine + TreatmentSummary.Parse(SampleTreatments(now), now).Report(now);
            File.WriteAllText(Path.Combine(folder, "treatments-check-sample.txt"), sampleReport, new UTF8Encoding(false));
            using (var form = new Form { Width = 720, Height = 640, FormBorderStyle = FormBorderStyle.FixedSingle, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000), ShowInTaskbar = false })
            using (var box = new TextBox { Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f), Text = sampleReport.Replace("\r\n", "\n").Replace("\n", "\r\n"), BackColor = SystemColors.Window })
            {
                form.Controls.Add(box); form.CreateControl(); box.CreateControl();
                using (var bitmap = new Bitmap(box.Width, box.Height)) { box.DrawToBitmap(bitmap, new Rectangle(0, 0, box.Width, box.Height)); bitmap.Save(Path.Combine(folder, "treatments-check-sample.png"), ImageFormat.Png); }
            }
            var state = new DisplayState { Reading = new Reading { Mgdl = 112, Direction = "Flat", Time = now.AddMinutes(-2), Delta = -4, Forecast = 96, ForecastLow = 84, ForecastHigh = 108 }, Status = "Gluroo connected · click-through locked",
                Insulin = new InsulinStatus { Iob = 1.35, Fetched = now }, PumpData = now.AddMinutes(-23) };
            SaveImage(folder, "overlay-current.png", new Settings(), state, now);
            SaveImage(folder, "overlay-current-mmol.png", new Settings { Mmol = true }, state, now);
            state.PumpData = now.AddMinutes(-88);
            SaveImage(folder, "overlay-pump-late.png", new Settings(), state, now);
            var keep = state.Reading;
            state.Reading = new Reading { Mgdl = 92, Direction = "FortyFiveDown", Time = now.AddMinutes(-1), Delta = -5, Forecast = 76, ForecastLow = 63, ForecastHigh = 89, LowInMinutes = 15 };
            SaveImage(folder, "overlay-low-notice.png", new Settings(), state, now);
            state.Reading = keep;
            state.PumpData = now.AddMinutes(-23);
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
