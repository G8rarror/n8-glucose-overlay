using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace N8
{
    /// <summary>
    /// Offline replay of saved CGM entries through model variants: --backtest entries.json [--output folder].
    /// No network. Truth is the CGM itself, which trails real blood glucose in fast falls, so low timing here is conservative.
    /// </summary>
    public static class Backtest
    {
        sealed class Variant { public string Name; public TrendModel.Options Options; }

        public static int Run(string[] args)
        {
            int i = Array.IndexOf(args, "--backtest");
            string file = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            if (file == null || !File.Exists(file)) { Console.Error.WriteLine("usage: --backtest entries.json [--output folder]"); return 2; }
            int o = Array.IndexOf(args, "--output");
            string folder = o >= 0 && o + 1 < args.Length ? args[o + 1] : Path.GetDirectoryName(Path.GetFullPath(file));
            var readings = Reading.ParseAll(File.ReadAllText(file), DateTimeOffset.UtcNow.AddMinutes(5));
            readings.Reverse(); // oldest first
            var report = Report(readings, Variants());
            File.WriteAllText(Path.Combine(folder, "backtest.txt"), report, new UTF8Encoding(false));
            Console.Write(report);
            return 0;
        }

        static List<Variant> Variants()
        {
            var d = TrendModel.Default;
            Func<string, Action<TrendModel.Options>, Variant> v = (name, change) => { var opt = d.Copy(); change(opt); return new Variant { Name = name, Options = opt }; };
            return new List<Variant>
            {
                v("Before backtest (low edge, uncapped, z 1.28)", x => { x.LowEdgeZ = TrendModel.Z80; x.MaxScale = double.MaxValue; x.RangeZ = TrendModel.Z80; x.Learned = false; }),
                v("Damped-trend estimate (before fit)", x => x.Learned = false),
                v("Current defaults", x => { }),
                v("Defaults + 5 min lag in fast falls", x => x.LagMinutes = 5),
                v("Defaults + look ahead 20 min", x => x.LowHorizon = 20),
                v("Defaults + low edge of range reaches 70", x => x.LowEdgeZ = TrendModel.Z80),
                v("Defaults + stronger damping (0.94)", x => x.DampPerMinute = 0.94),
            };
        }

        /// <summary>Runs one variant at every reading as if live (the 6 newest readings at that moment, as the apps fetch).</summary>
        static List<Reading> Replay(List<Reading> oldestFirst, TrendModel.Options options)
        {
            var results = new List<Reading>();
            for (int i = 0; i < oldestFirst.Count; i++)
            {
                var window = new List<Reading>();
                for (int k = i; k >= 0 && window.Count < GlurooClient.HistoryCount; k--)
                    window.Add(new Reading { Mgdl = oldestFirst[k].Mgdl, Time = oldestFirst[k].Time, Direction = oldestFirst[k].Direction });
                results.Add(Reading.AnalyzeNewest(window, options));
            }
            return results;
        }

        static int? At(List<Reading> oldestFirst, DateTimeOffset target, int from)
        {
            int best = -1;
            for (int k = from; k < oldestFirst.Count && oldestFirst[k].Time <= target.AddMinutes(2.5); k++)
                if (oldestFirst[k].Time >= target.AddMinutes(-2.5) && (best < 0 || Math.Abs((oldestFirst[k].Time - target).TotalSeconds) < Math.Abs((oldestFirst[best].Time - target).TotalSeconds))) best = k;
            return best < 0 ? (int?)null : best;
        }

        static string Report(List<Reading> data, List<Variant> variants)
        {
            var b = new StringBuilder();
            var days = (data[data.Count - 1].Time - data[0].Time).TotalDays;
            b.AppendLine("N8 forecast backtest · " + data.Count + " readings over " + days.ToString("0.0", CultureInfo.InvariantCulture) + " days · " + data[0].Time.ToLocalTime().ToString("MM-dd HH:mm") + " to " + data[data.Count - 1].Time.ToLocalTime().ToString("MM-dd HH:mm"));
            b.AppendLine("Truth is the CGM, which trails blood glucose in fast falls; real lows start earlier and go lower than counted here.");
            b.AppendLine();
            // Low episodes: first reading <= 70 after at least 30 minutes above 70.
            var lows = new List<int>();
            DateTimeOffset lastLow = DateTimeOffset.MinValue;
            for (int i = 1; i < data.Count; i++)
                if (data[i].Mgdl <= TrendModel.LowThreshold)
                {
                    if (data[i].Time - lastLow > TimeSpan.FromMinutes(30) && data[i - 1].Mgdl > TrendModel.LowThreshold) lows.Add(i);
                    lastLow = data[i].Time;
                }
            b.AppendLine("Low episodes (CGM at or below 70): " + lows.Count + (lows.Count > 0 ? "  at " + string.Join(", ", lows.Select(i => data[i].Time.ToLocalTime().ToString("MM-dd HH:mm") + " (min " + MinAfter(data, i, 45) + ")")) : ""));
            b.AppendLine();
            b.AppendLine("Variant                                      MAE   fast-fall  in-range  median   lows warned  lead (min)        notices  false  near  per day");
            b.AppendLine("                                             20m   MAE/bias   (aim 80%) width    >=5m ahead   each low          total    alarm  miss");
            foreach (var variant in variants)
            {
                var results = Replay(data, variant.Options);
                // Accuracy 20 minutes ahead.
                double err = 0, fastErr = 0, fastBias = 0; int n = 0, fast = 0, inRange = 0; var widths = new List<double>();
                for (int i = 0; i < data.Count; i++)
                {
                    var r = results[i];
                    if (r.Forecast == null) continue;
                    int? j = At(data, data[i].Time.AddMinutes(Reading.ForecastMinutes), i + 1);
                    if (j == null) continue;
                    double actual = data[j.Value].Mgdl, miss = r.Forecast.Value - actual;
                    n++; err += Math.Abs(miss); widths.Add(r.ForecastHigh.Value - r.ForecastLow.Value);
                    if (actual >= r.ForecastLow && actual <= r.ForecastHigh) inRange++;
                    if (r.Rate <= -1) { fast++; fastErr += Math.Abs(miss); fastBias += miss; }
                }
                widths.Sort();
                // Low warnings: any notice (LowInMinutes or LowNow).
                Func<int, bool> warned = i => results[i].LowInMinutes != null || results[i].LowNow;
                var leads = new List<string>(); int early = 0;
                foreach (int low in lows)
                {
                    int first = -1;
                    for (int k = low; k >= 0 && data[low].Time - data[k].Time <= TimeSpan.FromMinutes(45); k--) if (warned(k)) first = k;
                    if (first < 0) { leads.Add("miss"); continue; }
                    double lead = (data[low].Time - data[first].Time).TotalMinutes;
                    leads.Add(lead.ToString("0", CultureInfo.InvariantCulture));
                    if (lead >= 5) early++;
                }
                // Notice episodes and whether a low followed.
                int episodes = 0, falseAlarm = 0, nearMiss = 0;
                for (int i = 0; i < data.Count; i++)
                {
                    if (!warned(i) || (i > 0 && warned(i - 1) && data[i].Time - data[i - 1].Time <= TimeSpan.FromMinutes(10))) continue;
                    episodes++;
                    int min = MinAfter(data, i, 45);
                    if (min > 80) falseAlarm++; else if (min > TrendModel.LowThreshold) nearMiss++;
                }
                b.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-44} {1,5:0.0}  {2,5:0.0}/{3,5:+0;-0}  {4,6:0}%   {5,5:0}    {6,2}/{7,-2}        {8,-17} {9,5}    {10,5}  {11,4}  {12,6:0.0}",
                    variant.Name, n > 0 ? err / n : 0, fast > 0 ? fastErr / fast : 0, fast > 0 ? fastBias / fast : 0, n > 0 ? 100.0 * inRange / n : 0,
                    widths.Count > 0 ? widths[widths.Count / 2] : 0, early, lows.Count, string.Join(",", leads), episodes, falseAlarm, nearMiss, episodes / Math.Max(days, 0.01)));
            }
            b.AppendLine();
            b.AppendLine("MAE: average miss of the 20-minute estimate (mg/dL). Fast-fall: only while falling 1+ mg/dL/min; bias > 0 means the estimate stayed too high.");
            b.AppendLine("Notices: each separate run of 'Below 70 possible' or 'check finger stick'. False alarm: CGM stayed above 80 for the next 45 minutes. Near miss: lowest 71-80.");
            return b.ToString();
        }

        static int MinAfter(List<Reading> data, int i, int minutes)
        {
            int min = data[i].Mgdl;
            for (int k = i; k < data.Count && data[k].Time - data[i].Time <= TimeSpan.FromMinutes(minutes); k++) min = Math.Min(min, data[k].Mgdl);
            return min;
        }
    }
}
