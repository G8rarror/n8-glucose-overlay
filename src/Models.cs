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
            return readings.OrderByDescending(r => r.Time).FirstOrDefault();
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
        public void Accept(Reading reading)
        {
            Error = false;
            if (reading == null) { Status = "No readings returned · waiting for CGM data"; return; }
            if (Reading != null && reading.Time < Reading.Time) { Status = "Server returned older data · last reading kept"; return; }
            Reading = reading; Status = "Gluroo connected · click-through locked";
        }
    }
}
