using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace N8
{
    public sealed class Settings
    {
        public string Server { get; set; }
        public string ProtectedCredential { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int ScalePercent { get; set; }
        public int PanelOpacity { get; set; }
        public int StaleMinutes { get; set; }
        public int PollSeconds { get; set; }
        public bool Mmol { get; set; }
        public bool Locked { get; set; }
        public Settings()
        {
            X = 40; Y = 40; ScalePercent = 100; PanelOpacity = 78;
            StaleMinutes = 10; PollSeconds = 120; Locked = true;
        }
        public void Validate()
        {
            ScalePercent = Math.Max(75, Math.Min(200, ScalePercent));
            PanelOpacity = Math.Max(0, Math.Min(95, PanelOpacity));
            StaleMinutes = Math.Max(5, Math.Min(30, StaleMinutes));
            PollSeconds = Math.Max(120, Math.Min(900, PollSeconds));
        }
        public Settings Copy() { return (Settings)MemberwiseClone(); }
    }

    public static class Credentials
    {
        public static string ValidateServer(string text)
        {
            Uri uri;
            if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri) || uri.Scheme != "https" ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
                uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
                !Regex.IsMatch(uri.DnsSafeHost, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.ns\.gluroo\.com\z", RegexOptions.IgnoreCase))
                throw new ArgumentException("Enter the exact HTTPS Nightscout base URL from Gluroo, such as https://your-id.ns.gluroo.com. Do not include a token, API path, or proxy.");
            return uri.GetLeftPart(UriPartial.Authority);
        }
        public static string Header(string input, bool alreadyHashed)
        {
            input = input.Trim();
            if (alreadyHashed)
            {
                if (!Regex.IsMatch(input, @"\A[0-9a-fA-F]{40}\z")) throw new ArgumentException("The API Secret Header must contain exactly 40 hexadecimal characters.");
                return input.ToLowerInvariant();
            }
            if (input.Length < 1 || input.Length > 512 || input.Any(char.IsControl)) throw new ArgumentException("Enter the API Secret Token from Gluroo.");
            using (var hash = SHA1.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-", "").ToLowerInvariant();
        }
        public static string Protect(string header)
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(header), null, DataProtectionScope.CurrentUser));
        }
        public static string Unprotect(string value)
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            try { return Header(Encoding.UTF8.GetString(bytes), true); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
    }

    public sealed class SettingsStore
    {
        public readonly string FilePath;
        public SettingsStore(string root) { FilePath = Path.Combine(root, "settings.json"); }
        public Settings Load()
        {
            if (!File.Exists(FilePath)) return new Settings();
            var settings = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath));
            if (settings == null) throw new InvalidDataException("Settings are empty.");
            settings.Validate();
            return settings;
        }
        public void Save(Settings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(settings), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
    }

    public sealed class Reading
    {
        public int Mgdl;
        public DateTimeOffset Time;
        public string Direction;
        /// <summary>Change since the previous reading, normalized to mg/dL per 5 minutes. Null without a usable previous reading.</summary>
        public double? Delta;
        /// <summary>Projected mg/dL ForecastMinutes after this reading, from the damped trend (TrendModel). Null when history is too short, gapped, or noisy.</summary>
        public double? Forecast;
        /// <summary>80% range around Forecast.</summary>
        public double? ForecastLow, ForecastHigh;
        /// <summary>The older straight-line projection, kept for comparison in the forecast log; not shown.</summary>
        public double? LinearForecast;
        /// <summary>Filtered rate of change, mg/dL per minute.</summary>
        public double? Rate;
        /// <summary>Recent readings jump around too much for a trend estimate.</summary>
        public bool Noisy;
        /// <summary>Minutes until a low (lower edge of range) or high (estimate) threshold may be crossed, within TrendModel.ThresholdHorizon.</summary>
        public int? LowInMinutes, HighInMinutes;
        /// <summary>CGM is low or near low and still falling: the real level may already be lower (sensor lag).</summary>
        public bool LowNow;
        public const int ForecastMinutes = 20;
        public string Arrow { get { return TrendArrow(Direction); } }
        public static string TrendArrow(string direction)
        {
            switch (direction)
            {
                case "DoubleUp": return "↑↑";
                case "SingleUp": return "↑";
                case "FortyFiveUp": return "↗";
                case "Flat": return "→";
                case "FortyFiveDown": return "↘";
                case "SingleDown": return "↓";
                case "DoubleDown": return "↓↓";
                default: return "?";
            }
        }
        public bool IsStale(DateTimeOffset now, int minutes) { return now - Time >= TimeSpan.FromMinutes(minutes); }
        public string Value(bool mmol) { return mmol ? (Mgdl / 18.0182).ToString("0.0", CultureInfo.InvariantCulture) : Mgdl.ToString(CultureInfo.InvariantCulture); }
        public string DeltaText(bool mmol)
        {
            if (Delta == null) return null;
            double d = mmol ? Delta.Value / 18.0182 : Delta.Value;
            string text = mmol ? d.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(d, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
            if (text == "-0" || text == "-0.0") text = text.Substring(1);
            return (text.StartsWith("-") ? "" : "+") + text;
        }
        public string ForecastText(bool mmol)
        {
            if (Forecast == null) return null;
            double f = Forecast.Value;
            // CGM range is 40-400 mg/dL; never imply precision outside it.
            if (f < 40) return mmol ? "<2.2" : "<40";
            if (f > 400) return mmol ? ">22.2" : ">400";
            return mmol ? (f / 18.0182).ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(f).ToString("0", CultureInfo.InvariantCulture);
        }
        /// <summary>"est. ~178 (150–205) in 20m": the middle estimate first, then its range. Null when there is no estimate.</summary>
        public string EstimateText(bool mmol)
        {
            if (Forecast == null || ForecastLow == null || ForecastHigh == null) return Noisy ? "est. — noisy readings" : null;
            string middle = ForecastText(mmol), tail = " in " + ForecastMinutes + "m";
            // Outside the CGM's 40-400 range the middle is already "<40" or ">400"; a range adds nothing.
            if (middle.StartsWith("<") || middle.StartsWith(">")) return "est. " + middle + tail;
            return "est. ~" + middle + " (" + RangeText(mmol) + ")" + tail;
        }
        /// <summary>"150–175", clamped to the 40-400 CGM range. Null without a range.</summary>
        public string RangeText(bool mmol)
        {
            if (ForecastLow == null || ForecastHigh == null) return null;
            double lo = ForecastLow.Value, hi = ForecastHigh.Value;
            if (hi < 40) return mmol ? "<2.2" : "<40";
            if (lo > 400) return mmol ? ">22.2" : ">400";
            Func<double, string> f = x => mmol ? (x / 18.0182).ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(x).ToString("0", CultureInfo.InvariantCulture);
            return (lo < 40 ? "<" + f(40) : f(lo)) + "–" + (hi > 400 ? ">" + f(400) : f(hi));
        }
        /// <summary>Threshold notice for the status line, or null.</summary>
        public string ThresholdText(bool mmol)
        {
            Func<int, string> f = x => mmol ? (x / 18.0182).ToString("0.0", CultureInfo.InvariantCulture) : x.ToString(CultureInfo.InvariantCulture);
            if (LowNow) return "Low possible now · check finger stick";
            if (LowInMinutes != null) return "Heading below " + f(TrendModel.LowThreshold) + " in ~" + LowInMinutes + "m · estimate";
            if (HighInMinutes != null) return "Above " + f(TrendModel.HighThreshold) + " likely in ~" + HighInMinutes + "m · estimate";
            return null;
        }
        /// <summary>Runs the analysis on readings already in memory (newest first, deduplicated). Used by the backtest.</summary>
        public static Reading AnalyzeNewest(List<Reading> readings, TrendModel.Options options)
        {
            Analyze(readings, options);
            return readings[0];
        }
        /// <summary>Fills Delta and Forecast on readings[0] (newest first, deduplicated).</summary>
        private static void Analyze(List<Reading> readings, TrendModel.Options options = null)
        {
            var newest = readings[0];
            if (readings.Count >= 2)
            {
                double gap = (newest.Time - readings[1].Time).TotalMinutes;
                if (gap >= 2.5 && gap <= 10) newest.Delta = (newest.Mgdl - readings[1].Mgdl) * 5.0 / gap;
            }
            // Damped trend over contiguous readings in the last 30 minutes (what is fetched).
            var trend = Window(readings, 30.5);
            if (trend != null) TrendModel.Apply(trend, options);
            // Straight line over the last 20 minutes, kept for comparison.
            var window = Window(readings, 20.5);
            if (window == null) return;
            // Least-squares line, x in minutes relative to the newest reading.
            int n = window.Count;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (var r in window)
            {
                double x = (r.Time - newest.Time).TotalMinutes;
                sx += x; sy += r.Mgdl; sxx += x * x; sxy += x * r.Mgdl;
            }
            double denominator = n * sxx - sx * sx;
            if (denominator <= 0) return;
            double slope = (n * sxy - sx * sy) / denominator, intercept = (sy - slope * sx) / n;
            double squares = 0;
            foreach (var r in window)
            {
                double residual = r.Mgdl - (intercept + slope * (r.Time - newest.Time).TotalMinutes);
                squares += residual * residual;
            }
            // A noisy or curving trace makes a straight-line projection misleading, so show nothing.
            if (Math.Sqrt(squares / n) > 6) return;
            newest.LinearForecast = intercept + slope * ForecastMinutes;
        }
        /// <summary>Contiguous readings (gaps of at most 7.5 minutes) within the given minutes of the newest; null if fewer than 3 or spanning under 9.5 minutes.</summary>
        private static List<Reading> Window(List<Reading> readings, double minutes)
        {
            var newest = readings[0];
            var window = new List<Reading> { newest };
            for (int i = 1; i < readings.Count; i++)
            {
                if ((window[window.Count - 1].Time - readings[i].Time).TotalMinutes > 7.5) break;
                if ((newest.Time - readings[i].Time).TotalMinutes > minutes) break;
                window.Add(readings[i]);
            }
            return window.Count < 3 || (newest.Time - window[window.Count - 1].Time).TotalMinutes < 9.5 ? null : window;
        }
        public static Reading Parse(string json, DateTimeOffset now)
        {
            object parsed;
            try { parsed = new JavaScriptSerializer { MaxJsonLength = 65536 }.DeserializeObject(json); }
            catch (ArgumentException) { throw new FormatException("Invalid JSON response."); }
            var array = parsed as object[];
            if (array == null) throw new FormatException("Expected a JSON array.");
            var readings = new List<Reading>();
            foreach (var item in array)
            {
                var entry = item as Dictionary<string, object>;
                if (entry == null || (entry.ContainsKey("type") && Convert.ToString(entry["type"]) != "sgv")) continue;
                int sgv;
                if (!entry.ContainsKey("sgv") || !int.TryParse(Convert.ToString(entry["sgv"], CultureInfo.InvariantCulture), out sgv) || sgv < 1 || sgv > 1000) continue;
                DateTimeOffset time;
                if (!TryTime(entry, out time) || time > now.AddMinutes(2)) continue;
                string direction = entry.ContainsKey("direction") ? Convert.ToString(entry["direction"]) : null;
                if (string.IsNullOrWhiteSpace(direction))
                {
                    int trend;
                    if (entry.ContainsKey("trend") && int.TryParse(Convert.ToString(entry["trend"]), out trend))
                    {
                        string[] names = { "None", "DoubleUp", "SingleUp", "FortyFiveUp", "Flat", "FortyFiveDown", "SingleDown", "DoubleDown", "NotComputable", "OutOfRange" };
                        if (trend >= 0 && trend < names.Length) direction = names[trend];
                    }
                }
                readings.Add(new Reading { Mgdl = sgv, Time = time, Direction = direction });
            }
            if (array.Length > 0 && readings.Count == 0) throw new FormatException("No valid CGM entry or timestamp.");
            if (readings.Count == 0) return null;
            // Newest first; drop duplicate uploads of the same sensor reading (within 30 seconds).
            var ordered = new List<Reading>();
            foreach (var r in readings.OrderByDescending(r => r.Time))
                if (ordered.Count == 0 || (ordered[ordered.Count - 1].Time - r.Time).TotalSeconds >= 30) ordered.Add(r);
            Analyze(ordered);
            return ordered[0];
        }
        /// <summary>Every valid reading in a Nightscout entries array, newest first and deduplicated; nothing analysed. Used by the backtest.</summary>
        public static List<Reading> ParseAll(string json, DateTimeOffset now)
        {
            var all = new List<Reading>();
            object parsed = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.DeserializeObject(json);
            var array = parsed as object[];
            if (array == null) throw new FormatException("Expected a JSON array.");
            foreach (var item in array)
            {
                var single = Parse(new JavaScriptSerializer { MaxJsonLength = 65536 }.Serialize(new[] { item }), now);
                if (single != null) all.Add(new Reading { Mgdl = single.Mgdl, Time = single.Time, Direction = single.Direction });
            }
            var ordered = new List<Reading>();
            foreach (var r in all.OrderByDescending(r => r.Time))
                if (ordered.Count == 0 || (ordered[ordered.Count - 1].Time - r.Time).TotalSeconds >= 30) ordered.Add(r);
            return ordered;
        }
        private static bool TryTime(Dictionary<string, object> entry, out DateTimeOffset time)
        {
            time = default(DateTimeOffset);
            foreach (string key in new[] { "date", "mills" })
            {
                long ms;
                if (entry.ContainsKey(key) && long.TryParse(Convert.ToString(entry[key], CultureInfo.InvariantCulture), out ms))
                {
                    try { time = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms); return ms > 0; }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
            foreach (string key in new[] { "dateString", "sysTime" })
                if (entry.ContainsKey(key) && DateTimeOffset.TryParse(Convert.ToString(entry[key]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)) return true;
            return false;
        }
    }

    public sealed class DisplayState
    {
        public Reading Reading;
        public string Status = "Set up Gluroo from the tray";
        public bool Error;
        public bool Demo;
        /// <summary>Gluroo IOB; null until fetched. Not shown once older than InsulinStatus.MaxShowMinutes.</summary>
        public InsulinStatus Insulin;
        /// <summary>Newest pump event Tandem holds, from the local tconnectsync log; null if unknown.</summary>
        public DateTimeOffset? PumpData;
        /// <summary>True when PumpData came from Gluroo's treatments (approximate, may read older) rather than the local tconnectsync log.</summary>
        public bool PumpDataApprox;
        public void Accept(Reading reading)
        {
            Error = false;
            if (reading == null) { Status = "No readings returned · waiting for CGM data"; return; }
            if (Reading != null && reading.Time < Reading.Time) { Status = "Server returned older data · last reading kept"; return; }
            Reading = reading; Status = "Gluroo connected · click-through locked";
        }
    }
}
