using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace N8
{
    public sealed class ApiException : Exception
    {
        public readonly int StatusCode;
        public ApiException(int status) : base("Gluroo request failed.") { StatusCode = status; }
    }
    public sealed class GlurooClient : IDisposable
    {
        private readonly HttpClient http;
        public GlurooClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { }
        internal GlurooClient(HttpMessageHandler handler)
        {
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("N8GlucoseOverlay/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }
        public async Task<Reading> FetchAsync(string server, string header, CancellationToken cancel)
        {
            server = Credentials.ValidateServer(server);
            header = Credentials.Header(header, true);
            using (var request = new HttpRequestMessage(HttpMethod.Get, server + "/api/v1/entries.json?count=1"))
            {
                request.Headers.Add("api-secret", header);
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw new ApiException((int)response.StatusCode);
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        if (response.Content.Headers.ContentLength > 65536) throw new FormatException("Response too large.");
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var buffer = new MemoryStream())
                        {
                            byte[] block = new byte[4096];
                            int count;
                            while ((count = await stream.ReadAsync(block, 0, block.Length, timeout.Token).ConfigureAwait(false)) > 0)
                            {
                                if (buffer.Length + count > 65536) throw new FormatException("Response too large.");
                                buffer.Write(block, 0, count);
                            }
                            return Reading.Parse(Encoding.UTF8.GetString(buffer.ToArray()), DateTimeOffset.UtcNow);
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
}
