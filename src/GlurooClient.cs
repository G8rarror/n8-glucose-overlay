using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace N8
{
    public sealed class ApiException : Exception
    {
        public readonly int StatusCode;
        public ApiException(int status) : base("Gluroo request failed.") { StatusCode = status; }
    }
    public sealed class GlurooClient : IDisposable
    {
        /// <summary>About 30 minutes of 5-minute CGM readings, for delta and forecast. Still one request per poll.</summary>
        public const int HistoryCount = 6;
        private readonly HttpClient http;
        public GlurooClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { }
        internal GlurooClient(HttpMessageHandler handler)
        {
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("N8GlucoseOverlay/1.1");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }
        /// <summary>Records requested by the one-off treatments check. Only used by --treatments-check.</summary>
        public const int TreatmentsCount = 100;
        public const int TreatmentsMaxBytes = 1048576;
        public async Task<Reading> FetchAsync(string server, string header, CancellationToken cancel)
        {
            string json = await GetAsync(server, header, "/api/v1/entries.json?count=" + HistoryCount, 65536, cancel).ConfigureAwait(false);
            return Reading.Parse(json, DateTimeOffset.UtcNow);
        }
        /// <summary>Gluroo's insulin on board, from the documented devicestatus endpoint. GET only.</summary>
        public async Task<InsulinStatus> FetchIobAsync(string server, string header, CancellationToken cancel)
        {
            string json = await GetAsync(server, header, "/api/v1/devicestatus.json?count=1", 65536, cancel).ConfigureAwait(false);
            return InsulinStatus.Parse(json, DateTimeOffset.UtcNow);
        }
        /// <summary>Newest pump basal/bolus time from Gluroo, for PCs without tconnectsync. GET only, 5 records.</summary>
        public async Task<DateTimeOffset?> FetchPumpDataTimeAsync(string server, string header, CancellationToken cancel)
        {
            string json = await GetAsync(server, header, "/api/v1/treatments.json?count=5", 65536, cancel).ConfigureAwait(false);
            return PumpSync.NewestPumpTreatment(json, DateTimeOffset.UtcNow);
        }
        /// <summary>Raw JSON from the documented treatments endpoint. GET only; never posts, edits or deletes.</summary>
        public Task<string> FetchTreatmentsJsonAsync(string server, string header, CancellationToken cancel)
        {
            return GetAsync(server, header, "/api/v1/treatments.json?count=" + TreatmentsCount, TreatmentsMaxBytes, cancel);
        }
        private async Task<string> GetAsync(string server, string header, string pathAndQuery, int maxBytes, CancellationToken cancel)
        {
            server = Credentials.ValidateServer(server);
            header = Credentials.Header(header, true);
            using (var request = new HttpRequestMessage(HttpMethod.Get, server + pathAndQuery))
            {
                request.Headers.Add("api-secret", header);
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw new ApiException((int)response.StatusCode);
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        if (response.Content.Headers.ContentLength > maxBytes) throw new FormatException("Response too large.");
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var buffer = new MemoryStream())
                        {
                            byte[] block = new byte[4096];
                            int count;
                            while ((count = await stream.ReadAsync(block, 0, block.Length, timeout.Token).ConfigureAwait(false)) > 0)
                            {
                                if (buffer.Length + count > maxBytes) throw new FormatException("Response too large.");
                                buffer.Write(block, 0, count);
                            }
                            return Encoding.UTF8.GetString(buffer.ToArray());
                        }
                    }
                }
            }
        }
        public void Dispose() { http.Dispose(); }
    }

    public sealed class PollPolicy
    {
        public DateTimeOffset Next = DateTimeOffset.MinValue;
        public DateTimeOffset LastAttempt = DateTimeOffset.MinValue;
        public bool Unauthorized;
        private int failures;
        public bool CanPoll(DateTimeOffset now) { return !Unauthorized && now >= Next && now - LastAttempt >= TimeSpan.FromSeconds(120); }
        public void Begin(DateTimeOffset now, int seconds) { LastAttempt = now; Next = now.AddSeconds(Math.Max(120, seconds)); }
        public void Success() { failures = 0; }
        /// <summary>Dexcom posts about every 5 minutes; poll again shortly after the next reading is due.</summary>
        public const int CgmCycleSeconds = 315;
        /// <summary>
        /// After a successful poll, aligns the next one to the CGM cycle instead of a fixed interval.
        /// minSeconds (the PollSeconds setting, floored at 120) is never undercut. A late reading retries at that floor;
        /// no data, or a reading over 15 minutes old (signal loss), falls back to 5 minutes. A future-dated reading is capped at 6 minutes.
        /// </summary>
        public void Schedule(DateTimeOffset now, DateTimeOffset? newestReading, int minSeconds)
        {
            int min = Math.Max(120, minSeconds);
            var floor = LastAttempt.AddSeconds(min);
            if (newestReading == null || now - newestReading.Value > TimeSpan.FromMinutes(15)) { Next = LastAttempt.AddSeconds(Math.Max(min, 300)); return; }
            var expected = newestReading.Value.AddSeconds(CgmCycleSeconds);
            var cap = LastAttempt.AddSeconds(Math.Max(min, 360));
            Next = expected <= floor ? floor : (expected < cap ? expected : cap);
        }
        public void Fail(int status, DateTimeOffset now)
        {
            if (status == 401 || status == 403) { Unauthorized = true; return; }
            if (status == 429) { Next = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero).AddSeconds(5); return; }
            failures = Math.Min(failures + 1, 4);
            var retry = now.AddSeconds(Math.Min(900, 120 * Math.Pow(2, failures - 1)));
            if (retry > Next) Next = retry;
        }
    }

    public sealed class RateRecord
    {
        public long NextTicks { get; set; }
        public long AttemptTicks { get; set; }
        public bool Unauthorized { get; set; }
        public static RateRecord From(PollPolicy p) { return new RateRecord { NextTicks = p.Next.UtcTicks, AttemptTicks = p.LastAttempt.UtcTicks, Unauthorized = p.Unauthorized }; }
        public PollPolicy ToPolicy() { return new PollPolicy { Next = new DateTimeOffset(NextTicks, TimeSpan.Zero), LastAttempt = new DateTimeOffset(AttemptTicks, TimeSpan.Zero), Unauthorized = Unauthorized }; }
    }

    /// <summary>Persisted per-origin request cooldowns, shared by the overlay and the treatments check.</summary>
    public static class RateFile
    {
        public static Dictionary<string, RateRecord> Load(string path, string server)
        {
            if (!File.Exists(path)) return new Dictionary<string, RateRecord>();
            try
            {
                var value = new JavaScriptSerializer().Deserialize<Dictionary<string, RateRecord>>(File.ReadAllText(path));
                if (value == null) throw new FormatException();
                foreach (var record in value.Values) record.ToPolicy();
                return value;
            }
            catch (Exception)
            {
                // An unreadable budget file is not permission to immediately retry.
                var conservative = new Dictionary<string, RateRecord>();
                if (!string.IsNullOrEmpty(server)) conservative[server] = RateRecord.From(new PollPolicy { Next = DateTimeOffset.UtcNow.AddMinutes(15) });
                return conservative;
            }
        }
        public static void Save(string path, Dictionary<string, RateRecord> rates)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(rates));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }
}
