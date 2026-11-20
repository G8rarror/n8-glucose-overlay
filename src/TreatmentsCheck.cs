using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace N8
{
    /// <summary>
    /// Shape of Gluroo's treatments data, without dose values. Records which kinds of entries exist,
    /// how recent they are, and whether anything looks automatic (Control-IQ), so we can judge
    /// whether pump data arrives without manual logging and how late it runs.
    /// </summary>
    public sealed class TreatmentSummary
    {
        public sealed class Kind
        {
            public string Name;
            public int Count;
            public DateTimeOffset? Newest;
        }
        public int Total;
        public int Undated;
        public DateTimeOffset? Newest, Oldest;
        public int LastHour, Last3Hours, Last24Hours;
        public readonly List<Kind> Kinds = new List<Kind>();
        public readonly Dictionary<string, int> Sources = new Dictionary<string, int>();
        public int WithInsulin, WithRate, WithDuration, WithCarbs;
        public int MarkedAutomatic, MentionsAutomatic, TempBasals, FromTandem;
        public double? MedianGapMinutes;

        private static readonly string[] AutomaticWords = { "control-iq", "controliq", "control iq", "automatic", "auto bolus", "autobolus", "auto-bolus" };

        public static TreatmentSummary Parse(string json, DateTimeOffset now)
        {
            object parsed;
            try { parsed = new JavaScriptSerializer { MaxJsonLength = GlurooClient.TreatmentsMaxBytes }.DeserializeObject(json); }
            catch (ArgumentException) { throw new FormatException("Invalid JSON response."); }
            var array = parsed as object[];
            if (array == null) throw new FormatException("Expected a JSON array.");
            var summary = new TreatmentSummary();
            var kinds = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase);
            var times = new List<DateTimeOffset>();
            foreach (var item in array)
            {
                var t = item as Dictionary<string, object>;
                if (t == null) continue;
                summary.Total++;
                string type = Text(t, "eventType");
                if (type.Length == 0) type = "(no eventType)";
                if (type.Length > 40) type = type.Substring(0, 40);
                Kind kind;
                if (!kinds.TryGetValue(type, out kind)) { kind = new Kind { Name = type }; kinds[type] = kind; summary.Kinds.Add(kind); }
                kind.Count++;

                DateTimeOffset time;
                if (TryTime(t, out time) && time <= now.AddMinutes(5))
                {
                    times.Add(time);
                    if (kind.Newest == null || time > kind.Newest) kind.Newest = time;
                    if (summary.Newest == null || time > summary.Newest) summary.Newest = time;
                    if (summary.Oldest == null || time < summary.Oldest) summary.Oldest = time;
                    var age = now - time;
                    if (age <= TimeSpan.FromHours(1)) summary.LastHour++;
                    if (age <= TimeSpan.FromHours(3)) summary.Last3Hours++;
                    if (age <= TimeSpan.FromHours(24)) summary.Last24Hours++;
                }
                else summary.Undated++;

                string source = Source(Text(t, "enteredBy"));
                int seen; summary.Sources.TryGetValue(source, out seen); summary.Sources[source] = seen + 1;

                if (Present(t, "insulin")) summary.WithInsulin++;
                if (Present(t, "rate") || Present(t, "absolute") || Present(t, "percent")) summary.WithRate++;
                if (Present(t, "duration")) summary.WithDuration++;
                if (Present(t, "carbs")) summary.WithCarbs++;

                object automatic;
                if ((t.TryGetValue("automatic", out automatic) || t.TryGetValue("isAutomatic", out automatic)) && automatic is bool && (bool)automatic) summary.MarkedAutomatic++;
                string haystack = (type + " " + Text(t, "notes") + " " + Text(t, "reason") + " " + Text(t, "enteredBy") + " " + Text(t, "type")).ToLowerInvariant();
                if (AutomaticWords.Any(haystack.Contains)) summary.MentionsAutomatic++;
                if (type.IndexOf("temp basal", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("tempbasal", StringComparison.OrdinalIgnoreCase) >= 0) summary.TempBasals++;
                if (haystack.Contains("tandem") || haystack.Contains("mobi") || haystack.Contains("t:slim")) summary.FromTandem++;
            }
            summary.Kinds.Sort((a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : string.CompareOrdinal(a.Name, b.Name));
            times.Sort();
            var gaps = new List<double>();
            for (int i = 1; i < times.Count; i++)
            {
                double gap = (times[i] - times[i - 1]).TotalMinutes;
                if (gap > 0.1) gaps.Add(gap);
            }
            if (gaps.Count > 0) { gaps.Sort(); summary.MedianGapMinutes = gaps[gaps.Count / 2]; }
            return summary;
        }

        private static string Text(Dictionary<string, object> t, string key)
        {
            object value;
            return t.TryGetValue(key, out value) && value != null && !(value is Dictionary<string, object>) && !(value is object[]) ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim() : "";
        }
        private static bool Present(Dictionary<string, object> t, string key)
        {
            object value;
            return t.TryGetValue(key, out value) && value != null && !(value is string && ((string)value).Trim().Length == 0);
        }
        /// <summary>enteredBy, with email addresses hidden and length capped. Never contains dose data.</summary>
        private static string Source(string enteredBy)
        {
            if (enteredBy.Length == 0) return "(blank)";
            if (enteredBy.Contains("@")) return "(an email address)";
            enteredBy = new string(enteredBy.Where(c => !char.IsControl(c)).ToArray());
            return enteredBy.Length > 32 ? enteredBy.Substring(0, 32) + "…" : enteredBy;
        }
        private static bool TryTime(Dictionary<string, object> t, out DateTimeOffset time)
        {
            time = default(DateTimeOffset);
            foreach (string key in new[] { "date", "mills", "timestamp" })
            {
                object value;
                long ms;
                if (t.TryGetValue(key, out value) && value != null && !(value is string) && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms) && ms > 0)
                {
                    try { time = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms); return true; }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
            foreach (string key in new[] { "created_at", "timestamp", "dateString", "date" })
            {
                object value;
                if (t.TryGetValue(key, out value) && value is string &&
                    DateTimeOffset.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)) return true;
            }
            return false;
        }

        public static string Age(TimeSpan span)
        {
            if (span < TimeSpan.Zero) return "in the future";
            if (span.TotalMinutes < 1) return "under 1 min ago";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " min ago";
            if (span.TotalHours < 48) return (int)span.TotalHours + " h " + span.Minutes + " min ago";
            return (int)span.TotalDays + " days ago";
        }

        public string Report(DateTimeOffset now)
        {
            var s = new StringBuilder();
            s.AppendLine("N8 treatments check (read-only)");
            s.AppendLine("Checked " + now.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " local");
            s.AppendLine("Request: one GET /api/v1/treatments.json?count=" + GlurooClient.TreatmentsCount);
            s.AppendLine("No dose amounts, carb amounts or notes are shown or saved.");
            s.AppendLine();
            s.AppendLine("Records returned: " + Total + (Undated > 0 ? " (" + Undated + " without a readable time)" : ""));
            if (Newest != null)
            {
                s.AppendLine("Newest record:    " + Newest.Value.ToLocalTime().ToString("MM-dd HH:mm") + "  (" + Age(now - Newest.Value) + ")");
                s.AppendLine("Oldest record:    " + Oldest.Value.ToLocalTime().ToString("MM-dd HH:mm") + "  (" + Age(now - Oldest.Value) + ")");
                s.AppendLine("In last 1 h / 3 h / 24 h: " + LastHour + " / " + Last3Hours + " / " + Last24Hours);
                if (MedianGapMinutes != null) s.AppendLine("Typical spacing between records: " + MedianGapMinutes.Value.ToString("0.#", CultureInfo.InvariantCulture) + " min");
            }
            if (Kinds.Count > 0)
            {
                s.AppendLine();
                s.AppendLine("Record types (eventType)");
                foreach (var k in Kinds.Take(15))
                    s.AppendLine("  " + k.Name.PadRight(28) + k.Count.ToString().PadLeft(4) + "   newest " + (k.Newest == null ? "unknown" : Age(now - k.Newest.Value)));
                if (Kinds.Count > 15) s.AppendLine("  … and " + (Kinds.Count - 15) + " more types");
                s.AppendLine();
                s.AppendLine("Entered by");
                foreach (var pair in Sources.OrderByDescending(p => p.Value).Take(8))
                    s.AppendLine("  " + pair.Key.PadRight(34) + pair.Value.ToString().PadLeft(4));
                s.AppendLine();
                s.AppendLine("Fields present (values not shown)");
                s.AppendLine("  insulin " + WithInsulin + " · basal rate " + WithRate + " · duration " + WithDuration + " · carbs " + WithCarbs);
                s.AppendLine();
                s.AppendLine("Automatic-delivery signals");
                s.AppendLine("  Temp basal records:                 " + TempBasals);
                s.AppendLine("  Flagged automatic:                  " + MarkedAutomatic);
                s.AppendLine("  Mention Control-IQ / automatic:     " + MentionsAutomatic);
                s.AppendLine("  Mention Tandem / Mobi:              " + FromTandem);
            }
            s.AppendLine();
            s.AppendLine("What this suggests");
            foreach (var line in Findings(now)) s.AppendLine("  - " + line);
            return s.ToString();
        }

        public List<string> Findings(DateTimeOffset now)
        {
            var f = new List<string>();
            if (Total == 0)
            {
                f.Add("No treatments came back. Pump data is not reaching Gluroo's API. Check that Gluroo's Insulin Delivery setting is Tandem.");
                return f;
            }
            bool automatic = TempBasals > 0 || MarkedAutomatic > 0 || MentionsAutomatic > 0;
            if (automatic) f.Add("Automatic Control-IQ activity seems to reach Gluroo without anyone entering it.");
            else if (WithInsulin > 0) f.Add("Insulin records exist but nothing looks automatic. They may be manual entries, or Gluroo may not label Control-IQ activity.");
            else f.Add("No insulin records in this batch. Only other log entries came back.");
            if (TempBasals == 0 && WithRate == 0) f.Add("No basal-rate changes found. Without them, insulin-on-board estimates would miss most of Control-IQ's work.");
            if (Newest != null)
            {
                double minutes = (now - Newest.Value).TotalMinutes;
                if (minutes > 90) f.Add("The newest record is over 90 minutes old. Expect pump data to run well behind the CGM (Tandem's app uploads about hourly).");
                else f.Add("The newest record is under 90 minutes old. Run the check again at another time to see how far behind it usually runs.");
            }
            f.Add("This report is for planning only. It is not suitable for dosing decisions.");
            return f;
        }
    }

    internal static class TreatmentsCheck
    {
        public static int Run(string[] args)
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "N8GlucoseOverlay");
            int index = Array.IndexOf(args, "--output");
            string folder = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : root;
            bool created;
            // Holding the overlay's mutex keeps the two from sharing the request interval at the same moment.
            using (var mutex = new Mutex(true, Program.MutexName(), out created))
            {
                if (!created) return Show("Exit N8 Glucose Overlay from the tray first, then run the treatments check again.\n\nThis keeps the two from sending requests too close together.", 2);
                try { return RunLocked(root, folder); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static int RunLocked(string root, string folder)
        {
            Settings config;
            string header;
            try
            {
                config = new SettingsStore(root).Load();
                if (string.IsNullOrEmpty(config.ProtectedCredential)) return Show("No Gluroo connection is saved. Set it up in the overlay's Settings first.", 2);
                config.Server = Credentials.ValidateServer(config.Server);
                header = Credentials.Unprotect(config.ProtectedCredential);
            }
            catch (Exception) { return Show("Saved settings or credentials could not be opened. Re-enter them in the overlay's Settings.", 2); }

            string ratesPath = Path.Combine(root, "request-limits.json");
            var rates = RateFile.Load(ratesPath, config.Server);
            RateRecord record;
            var policy = rates.TryGetValue(config.Server, out record) ? record.ToPolicy() : new PollPolicy();
            var now = DateTimeOffset.UtcNow;
            if (policy.Unauthorized) return Show("Gluroo rejected the saved credential last time. Update it in the overlay's Settings.", 2);
            if (!policy.CanPoll(now))
            {
                var next = policy.Next > policy.LastAttempt.AddSeconds(120) ? policy.Next : policy.LastAttempt.AddSeconds(120);
                return Show("Gluroo's request interval is still protected. Try again after " + next.ToLocalTime().ToString("HH:mm:ss") + ".", 3);
            }
            policy.Begin(now, config.PollSeconds);
            try { rates[config.Server] = RateRecord.From(policy); RateFile.Save(ratesPath, rates); }
            catch (Exception) { return Show("Could not save the request limit, so no request was sent.", 2); }

            string report;
            int code = 0;
            try
            {
                string json;
                using (var client = new GlurooClient())
                using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    json = client.FetchTreatmentsJsonAsync(config.Server, header, cancel.Token).GetAwaiter().GetResult();
                policy.Success();
                now = DateTimeOffset.UtcNow;
                report = TreatmentSummary.Parse(json, now).Report(now);
            }
            catch (ApiException ex)
            {
                policy.Fail(ex.StatusCode, DateTimeOffset.UtcNow);
                report = "Gluroo answered HTTP " + ex.StatusCode + ". " +
                    (ex.StatusCode == 401 || ex.StatusCode == 403 ? "The credential was rejected." :
                     ex.StatusCode == 404 ? "The treatments endpoint is not available for this account." :
                     ex.StatusCode == 429 ? "The daily request budget is used up; requests pause until 00:00 UTC." : "Try again later.");
                code = 1;
            }
            catch (FormatException) { report = "Gluroo's reply was not a treatments list the check could read (or it was over 1 MB)."; code = 1; }
            catch (Exception) { policy.Fail(0, DateTimeOffset.UtcNow); report = "Could not reach Gluroo. Check the connection and try again later."; code = 1; }
            try { rates[config.Server] = RateRecord.From(policy); RateFile.Save(ratesPath, rates); } catch (Exception) { }

            string file = Path.Combine(folder, "treatments-check.txt");
            try { Directory.CreateDirectory(folder); File.WriteAllText(file, report, new UTF8Encoding(false)); report += Environment.NewLine + "Saved to " + file; }
            catch (Exception) { report += Environment.NewLine + "(Could not save the report file.)"; }
            ShowReport(report);
            return code;
        }

        private static int Show(string message, int code)
        {
            MessageBox.Show(message, "N8 treatments check", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return code;
        }

        private static void ShowReport(string report)
        {
            using (var form = new Form { Text = "N8 treatments check (read-only)", Width = 720, Height = 640, StartPosition = FormStartPosition.CenterScreen, Icon = OverlayApp.MakeIcon() })
            using (var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f), Text = report.Replace("\r\n", "\n").Replace("\n", "\r\n"), BackColor = SystemColors.Window })
            using (var close = new Button { Text = "Close", Dock = DockStyle.Bottom, Height = 34 })
            {
                close.Click += delegate { form.Close(); };
                form.Controls.Add(box); form.Controls.Add(close);
                form.AcceptButton = close; form.CancelButton = close;
                form.Shown += delegate { box.SelectionStart = 0; box.SelectionLength = 0; close.Focus(); };
                Application.Run(form);
            }
        }
    }
}
