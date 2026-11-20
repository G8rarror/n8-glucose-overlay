using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace N8
{
    /// <summary>Insulin on board as Gluroo reports it (devicestatus "glurooIob"), plus when we fetched it.</summary>
    public sealed class InsulinStatus
    {
        public double Iob;
        public DateTimeOffset Fetched;
        /// <summary>An IOB value older than this is not shown; it decays too much to be meaningful.</summary>
        public const int MaxShowMinutes = 25;
        public static InsulinStatus Parse(string json, DateTimeOffset now)
        {
            object parsed;
            try { parsed = new JavaScriptSerializer { MaxJsonLength = 65536 }.DeserializeObject(json); }
            catch (ArgumentException) { throw new FormatException("Invalid JSON response."); }
            var records = parsed as object[];
            if (records == null && parsed is Dictionary<string, object>) records = new[] { parsed };
            if (records == null) throw new FormatException("Expected device status.");
            foreach (var item in records)
            {
                var record = item as Dictionary<string, object>;
                if (record == null || !record.ContainsKey("glurooIob") || record["glurooIob"] == null) continue;
                double iob;
                if (!double.TryParse(Convert.ToString(record["glurooIob"], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out iob) ||
                    double.IsNaN(iob) || iob < -10 || iob > 100)
                    throw new FormatException("Implausible IOB.");
                return new InsulinStatus { Iob = iob, Fetched = now };
            }
            return null;
        }
        public bool IsShowable(DateTimeOffset now) { return now - Fetched < TimeSpan.FromMinutes(MaxShowMinutes); }
        public string IobText()
        {
            string text = Iob.ToString("0.00", CultureInfo.InvariantCulture);
            return text == "-0.00" ? "0.00" : text;
        }
    }

    /// <summary>Reads, locally and read-only, when Tandem last received pump data, from tconnectsync's own log.</summary>
    public static class PumpSync
    {
        /// <summary>Above this, insulin data is probably missing recent doses; Tandem's phone app normally uploads about hourly.</summary>
        public const int LateMinutes = 75;
        private static readonly Regex Iso = new Regex(@"cur_max_date: (\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2}))");
        private static readonly Regex Epoch = new Regex(@"cur_max_date: (\d{9,11})(?:\.\d+)?\b");
        public static string DefaultFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tconnectsync"); } }
        /// <summary>Newest "cur_max_date" in the text (time of the newest pump event Tandem holds), or null.</summary>
        public static DateTimeOffset? Latest(string text)
        {
            DateTimeOffset? best = null;
            foreach (Match m in Iso.Matches(text))
            {
                DateTimeOffset t;
                if (DateTimeOffset.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out t) && (best == null || t > best)) best = t;
            }
            foreach (Match m in Epoch.Matches(text))
            {
                long seconds;
                if (long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
                {
                    var t = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    if (best == null || t > best) best = t;
                }
            }
            return best;
        }
        /// <summary>
        /// Fallback for PCs without tconnectsync: time of the newest pump-sourced basal or bolus in Gluroo's treatments.
        /// Basal records can be up to ~30 minutes apart when the rate is steady, so this can read older than the truth, never newer.
        /// </summary>
        public static DateTimeOffset? NewestPumpTreatment(string json, DateTimeOffset now)
        {
            object parsed;
            try { parsed = new JavaScriptSerializer { MaxJsonLength = 65536 }.DeserializeObject(json); }
            catch (ArgumentException) { throw new FormatException("Invalid JSON response."); }
            var records = parsed as object[];
            if (records == null) throw new FormatException("Expected a JSON array.");
            DateTimeOffset? best = null;
            foreach (var item in records)
            {
                var record = item as Dictionary<string, object>;
                if (record == null) continue;
                string type = record.ContainsKey("eventType") ? Convert.ToString(record["eventType"]) : "";
                if (type.IndexOf("Basal", StringComparison.OrdinalIgnoreCase) < 0 && type.IndexOf("Bolus", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // Hand-entered records are not pump data.
                string by = record.ContainsKey("enteredBy") ? Convert.ToString(record["enteredBy"]) : "";
                if (by != "System" && by.IndexOf("tconnectsync", StringComparison.OrdinalIgnoreCase) < 0 && by.IndexOf("Tandem", StringComparison.OrdinalIgnoreCase) < 0) continue;
                DateTimeOffset t;
                if (!record.ContainsKey("created_at") || !DateTimeOffset.TryParse(Convert.ToString(record["created_at"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t)) continue;
                if (t > now.AddMinutes(2)) continue;
                if (best == null || t > best) best = t;
            }
            return best;
        }
        public static DateTimeOffset? Read(string folder)
        {
            DateTimeOffset? best = null;
            foreach (string name in new[] { "sync.log", "sync.previous.log" })
            {
                try
                {
                    var t = Latest(Tail(Path.Combine(folder, name), 262144));
                    if (t != null && (best == null || t > best)) best = t;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return best;
        }
        private static string Tail(string path, int bytes)
        {
            if (!File.Exists(path)) return "";
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, stream.Length - bytes);
                stream.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[stream.Length - start];
                int read = 0, n;
                while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) read += n;
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
        }
    }
}
