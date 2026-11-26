using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace N8
{
    /// <summary>
    /// Damped-trend estimate. A small Kalman filter tracks glucose level and rate of change through
    /// recent CGM readings; the projection assumes the rate fades over time (Control-IQ and the body
    /// push back), and carries an uncertainty that widens with the horizon. The tuning constants are
    /// starting guesses; the forecast log exists to check them against real readings.
    /// </summary>
    public static class TrendModel
    {
        /// <summary>CGM reading noise variance (mg/dL²), about a 4 mg/dL standard deviation.</summary>
        public const double MeasurementVariance = 16;
        /// <summary>Two-sided 80% interval.</summary>
        public const double Z80 = 1.2816;
        /// <summary>Hide the estimate only when readings are this many times rougher (in variance) than expected: about 5x the expected spread.</summary>
        public const double NoisyLimit = 25;
        public const int LowThreshold = 70, HighThreshold = 250, ThresholdHorizon = 30;
        /// <summary>CGM at or below this while falling: the real level may already be low (sensor lag), so ask for a finger stick.</summary>
        public const int CheckBelow = 85;

        /// <summary>Tuning; the backtest compares variants of these.</summary>
        public sealed class Options
        {
            /// <summary>How quickly the rate of change itself can wander (mg/dL/min² spectral density).</summary>
            public double AccelerationNoise = 0.1;
            /// <summary>Fraction of the rate kept per minute ahead; 0.955 halves the rate in about 15 minutes.</summary>
            public double DampPerMinute = 0.955;
            /// <summary>Only warn of a possible low while falling at least this fast (mg/dL/min).</summary>
            public double LowWarnRate = -0.5;
            /// <summary>Sensor lag allowance: while falling faster than LagFromRate, the level is taken as this many minutes further along.</summary>
            public double LagMinutes = 0, LagFromRate = -1;
            /// <summary>The first rule: warn only while the CGM is above 70, and never ask for a finger stick.</summary>
            public bool OriginalLowRule;
            /// <summary>Upper limit on how much rough readings widen the range (variance factor).</summary>
            public double MaxScale = 4;
            /// <summary>Low notice: how far ahead to look (minutes), and which edge must reach 70 (0 = the estimate itself, which cut false alarms by about 80% in the backtest; Z80 = low edge of the range).</summary>
            public int LowHorizon = 30;
            public double LowEdgeZ = 0;
            /// <summary>Width of the shown range in standard deviations (1.8 gave 80% coverage in the 7-day backtest; 1.28 would if the model were perfectly calibrated).</summary>
            public double RangeZ = 1.8;
            public Options Copy() { return (Options)MemberwiseClone(); }
        }
        public static readonly Options Default = new Options();

        /// <summary>Sum of damping factors over h minutes: how many minutes of the current rate still apply.</summary>
        public static double EffectiveMinutes(double h, Options o)
        {
            return o.DampPerMinute * (1 - Math.Pow(o.DampPerMinute, h)) / (1 - o.DampPerMinute);
        }

        /// <summary>Fills the damped estimate fields on window[0]. window is newest first, contiguous, at least 3 readings.</summary>
        public static void Apply(List<Reading> window, Options o = null)
        {
            o = o ?? Default;
            var newest = window[0];
            var ordered = window.AsEnumerable().Reverse().ToList();
            // Start from the first two readings (level and rate), so a fast rise or fall is not mistaken for noise.
            double dt0 = (ordered[1].Time - ordered[0].Time).TotalMinutes, r = MeasurementVariance, q = o.AccelerationNoise;
            double g = ordered[1].Mgdl, v = (ordered[1].Mgdl - ordered[0].Mgdl) / dt0;
            double pgg = r, pgv = r / dt0, pvv = 2 * r / (dt0 * dt0);
            double nis = 0; int updates = 0;
            for (int i = 2; i < ordered.Count; i++)
            {
                double dt = (ordered[i].Time - ordered[i - 1].Time).TotalMinutes;
                g += v * dt;
                double ngg = pgg + 2 * dt * pgv + dt * dt * pvv + q * dt * dt * dt / 3;
                double ngv = pgv + dt * pvv + q * dt * dt / 2;
                double nvv = pvv + q * dt;
                pgg = ngg; pgv = ngv; pvv = nvv;
                double innovation = ordered[i].Mgdl - g, s = pgg + MeasurementVariance;
                nis += innovation * innovation / s; updates++;
                double kg = pgg / s, kv = pgv / s;
                g += kg * innovation; v += kv * innovation;
                pvv -= kv * pgv; pgg *= 1 - kg; pgv *= 1 - kg;
            }
            newest.Rate = v;
            // Rougher readings than the model expects widen the range rather than hide the estimate: scaling every variance
            // by the observed roughness leaves the estimate unchanged and only widens its uncertainty. Hide only when chaotic.
            double roughness = nis / updates;
            if (roughness > NoisyLimit) { newest.Noisy = true; return; }
            double scale = Math.Min(o.MaxScale, Math.Max(1, roughness));
            pgg *= scale; pgv *= scale; pvv *= scale;
            // In a fast fall the sensor trails the blood; lean the level ahead by the lag allowance.
            if (o.LagMinutes > 0 && v <= o.LagFromRate) g += v * o.LagMinutes;
            double mean, sd;
            Project(g, v, pgg, pgv, pvv, Reading.ForecastMinutes, scale, o, out mean, out sd);
            newest.Forecast = mean; newest.ForecastLow = mean - o.RangeZ * sd; newest.ForecastHigh = mean + o.RangeZ * sd;
            if (!o.OriginalLowRule)
                newest.LowNow = (newest.Mgdl <= LowThreshold && v <= 0) || (newest.Mgdl <= CheckBelow && v <= o.LowWarnRate);
            for (int h = 1; h <= ThresholdHorizon; h++)
            {
                Project(g, v, pgg, pgv, pvv, h, scale, o, out mean, out sd);
                bool lowAllowed = o.OriginalLowRule ? newest.Mgdl > LowThreshold : !newest.LowNow;
                if (newest.LowInMinutes == null && lowAllowed && h <= o.LowHorizon && v <= o.LowWarnRate && mean - o.LowEdgeZ * sd <= LowThreshold) newest.LowInMinutes = h;
                if (newest.HighInMinutes == null && newest.Mgdl < HighThreshold && v > 0 && mean >= HighThreshold) newest.HighInMinutes = h;
            }
        }
        private static void Project(double g, double v, double pgg, double pgv, double pvv, double h, double scale, Options o, out double mean, out double sd)
        {
            double k = EffectiveMinutes(h, o);
            mean = g + v * k;
            // Uncertainty of today's level and rate, rate wander over the horizon, and the noise of the reading that will be compared.
            sd = Math.Sqrt(Math.Max(0, pgg + 2 * k * pgv + k * k * pvv + scale * (o.AccelerationNoise * k * k * k / 3 + MeasurementVariance)));
        }
    }

    /// <summary>
    /// Local score-keeping: one CSV row per new reading with the estimates made at that moment. Accuracy is
    /// worked out later by pairing each row with the reading about 20 minutes after it. Stays on this PC.
    /// </summary>
    public sealed class ForecastLog
    {
        public const string FileName = "forecast-log.csv";
        public const string Header = "time,mgdl,damped,low,high,linear";
        public const int KeepDays = 30;
        public readonly string FilePath;
        private DateTimeOffset last = DateTimeOffset.MinValue;
        public ForecastLog(string root)
        {
            FilePath = Path.Combine(root, FileName);
            try
            {
                if (File.Exists(FilePath))
                {
                    var tail = File.ReadLines(FilePath).LastOrDefault();
                    Row row;
                    if (tail != null && Row.TryParse(tail, out row)) last = row.Time;
                }
            }
            catch (IOException) { }
        }
        /// <summary>Appends the reading once. Never throws; score-keeping must not disturb the display.</summary>
        public bool Record(Reading r)
        {
            // The file stores whole seconds; Gluroo times carry milliseconds.
            if (r == null || r.Time < last.AddSeconds(1)) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                bool fresh = !File.Exists(FilePath);
                if (!fresh && new FileInfo(FilePath).Length > 2 * 1024 * 1024) { Trim(r.Time); fresh = !File.Exists(FilePath); }
                File.AppendAllText(FilePath, (fresh ? Header + "\r\n" : "") + Row.Format(r) + "\r\n", new UTF8Encoding(false));
                last = r.Time;
                return true;
            }
            catch (Exception) { return false; }
        }
        private void Trim(DateTimeOffset now)
        {
            var keep = new List<string> { Header };
            foreach (var line in File.ReadAllLines(FilePath))
            {
                Row row;
                if (Row.TryParse(line, out row) && now - row.Time <= TimeSpan.FromDays(KeepDays)) keep.Add(line);
            }
            string temporary = FilePath + ".tmp";
            File.WriteAllLines(temporary, keep, new UTF8Encoding(false));
            File.Replace(temporary, FilePath, null);
        }
        public Score Summarize(DateTimeOffset now, int days)
        {
            try { return File.Exists(FilePath) ? Score.From(File.ReadAllLines(FilePath), now, days) : new Score(); }
            catch (IOException) { return new Score(); }
        }

        public struct Row
        {
            public DateTimeOffset Time; public int Mgdl; public double? Damped, Low, High, Linear;
            public static string Format(Reading r)
            {
                Func<double?, string> f = x => x == null ? "" : x.Value.ToString("0.0", CultureInfo.InvariantCulture);
                return r.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "," + r.Mgdl.ToString(CultureInfo.InvariantCulture) + "," +
                    f(r.Forecast) + "," + f(r.ForecastLow) + "," + f(r.ForecastHigh) + "," + f(r.LinearForecast);
            }
            public static bool TryParse(string line, out Row row)
            {
                row = new Row();
                var p = line.Split(',');
                if (p.Length != 6 || !DateTimeOffset.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out row.Time) ||
                    !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out row.Mgdl)) return false;
                row.Damped = Num(p[2]); row.Low = Num(p[3]); row.High = Num(p[4]); row.Linear = Num(p[5]);
                return true;
            }
            private static double? Num(string s)
            {
                double d;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null;
            }
        }

        public sealed class Score
        {
            public int Days, Checked, Compared, InRange, Ranged;
            /// <summary>Average absolute miss of each model, over the estimates where both existed.</summary>
            public double DampedError, LinearError;
            public static Score From(IEnumerable<string> lines, DateTimeOffset now, int days)
            {
                var rows = new List<Row>();
                foreach (var line in lines) { Row row; if (Row.TryParse(line, out row)) rows.Add(row); }
                rows.Sort((a, b) => a.Time.CompareTo(b.Time));
                var score = new Score { Days = days };
                double dampedSum = 0, linearSum = 0;
                int j = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    var p = rows[i];
                    if (now - p.Time > TimeSpan.FromDays(days) || (p.Damped == null && p.Linear == null)) continue;
                    var target = p.Time.AddMinutes(Reading.ForecastMinutes);
                    // Closest reading within 2.5 minutes of the target time.
                    while (j < rows.Count && rows[j].Time < target.AddMinutes(-2.5)) j++;
                    int best = -1;
                    for (int k = j; k < rows.Count && rows[k].Time <= target.AddMinutes(2.5); k++)
                        if (best < 0 || Math.Abs((rows[k].Time - target).TotalSeconds) < Math.Abs((rows[best].Time - target).TotalSeconds)) best = k;
                    if (best < 0) continue;
                    int actual = rows[best].Mgdl;
                    score.Checked++;
                    if (p.Low != null && p.High != null) { score.Ranged++; if (actual >= p.Low.Value && actual <= p.High.Value) score.InRange++; }
                    if (p.Damped != null && p.Linear != null)
                    {
                        score.Compared++;
                        dampedSum += Math.Abs(p.Damped.Value - actual); linearSum += Math.Abs(p.Linear.Value - actual);
                    }
                }
                if (score.Compared > 0) { score.DampedError = dampedSum / score.Compared; score.LinearError = linearSum / score.Compared; }
                return score;
            }
            public string Report(bool mmol)
            {
                Func<double, string> u = d => mmol ? (d / 18.0182).ToString("0.0", CultureInfo.InvariantCulture) + " mmol/L" : Math.Round(d).ToString("0", CultureInfo.InvariantCulture) + " mg/dL";
                var b = new StringBuilder();
                b.AppendLine("Last " + Days + " days: " + Checked + " estimates checked against the reading " + Reading.ForecastMinutes + " minutes later.");
                if (Compared < 20) { b.AppendLine(); b.AppendLine("Not enough data yet. Leave the overlay running; a day of readings gives a first look, a week a fair one."); return b.ToString(); }
                b.AppendLine();
                b.AppendLine("Average miss, damped trend (shown):   " + u(DampedError));
                b.AppendLine("Average miss, straight line (old):    " + u(LinearError));
                if (Ranged > 0) b.AppendLine("Actual reading inside shown range:    " + (100 * InRange / Ranged) + "%   (aim: about 80%)");
                b.AppendLine();
                b.AppendLine("Compared over " + Compared + " estimates where both models had a value.");
                b.AppendLine("Only readings this PC received are counted. Estimates are a guide, not for dosing.");
                return b.ToString();
            }
        }
    }
}
