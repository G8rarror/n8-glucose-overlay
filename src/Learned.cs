using System;
using System.Collections.Generic;

namespace N8
{
    /// <summary>
    /// The 20-minute estimate and its range, fitted to 30 days of her readings (10-27 to 11-26-2026) by least absolute
    /// deviations; artifacts/fit/Fit.cs. Scored on weeks it was not fitted on: average miss 16.6 mg/dL versus 18.4 for the
    /// damped trend and 18.8 for "no change", better in all four weeks; the range held the later reading 79% of the time.
    /// It deliberately has no term for "lows bounce back": that bounce is the family treating her, and an estimate that
    /// assumes treatment would talk itself out of warning. Low and high notices stay with the damped trend.
    /// Refit about monthly; keep android/.../Trend.kt in step.
    /// </summary>
    public static class LearnedEstimate
    {
        const double Intercept = 3.2571, Rate = 7.7801, RateSquared = -1.8074, Curvature = -1.6791, LastStep = 2.9796,
            Level = -12.6265, AboveHigh = 10.8305, RateTimesLevel = -0.2176;
        /// <summary>10th and 90th percentile misses by speed: under 0.5, 0.5-1, 1-2, 2+ mg/dL/min.</summary>
        static readonly double[] Low10 = { -18.6, -19.6, -22.8, -37.2 }, High90 = { 21.6, 25.9, 35.2, 44.3 };

        /// <summary>window: newest first, contiguous (gaps up to 7.5 min), within 30 minutes; rate: Kalman mg/dL/min;
        /// roughness: the filter's variance factor (1 = readings as smooth as expected), which widens the range as the damped trend's does.</summary>
        public static void Estimate(List<Reading> window, double rate, double roughness, out double mid, out double low, out double high)
        {
            var newest = window[0];
            double g = newest.Mgdl;
            double lastStep = rate;
            if (window.Count > 1)
            {
                double dt = (newest.Time - window[1].Time).TotalMinutes;
                lastStep = (newest.Mgdl - window[1].Mgdl) / Math.Max(1, dt);
            }
            double change = Intercept + Rate * rate + RateSquared * rate * Math.Abs(rate) / 2 + Curvature * Bend(window) * 10 + LastStep * lastStep
                + Level * (g - 120) / 100 + AboveHigh * Math.Max(0, g - 200) / 100 + RateTimesLevel * rate * (g - 120) / 100;
            mid = g + change;
            double speed = Math.Abs(rate);
            int b = speed < 0.5 ? 0 : speed < 1 ? 1 : speed < 2 ? 2 : 3;
            double widen = Math.Sqrt(Math.Max(1, roughness));
            low = mid + Low10[b] * widen; high = mid + High90[b] * widen;
        }

        /// <summary>Second derivative (mg/dL/min²) of a least-squares quadratic; 0 with under 4 readings or under 15 minutes.</summary>
        public static double Bend(List<Reading> window)
        {
            var newest = window[0];
            int n = Math.Min(window.Count, 6);
            if (n < 4 || (newest.Time - window[n - 1].Time).TotalMinutes < 14.5) return 0;
            var a = new double[3, 4];
            for (int m = 0; m < n; m++)
            {
                double x = (window[m].Time - newest.Time).TotalMinutes;
                double[] p = { 1, x, x * x };
                for (int r = 0; r < 3; r++) { a[r, 3] += p[r] * window[m].Mgdl; for (int c = 0; c < 3; c++) a[r, c] += p[r] * p[c]; }
            }
            for (int c = 0; c < 3; c++)
            {
                int piv = c;
                for (int r = c + 1; r < 3; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
                if (Math.Abs(a[piv, c]) < 1e-12) return 0;
                for (int k = 0; k < 4; k++) { double t = a[c, k]; a[c, k] = a[piv, k]; a[piv, k] = t; }
                for (int r = c + 1; r < 3; r++) { double f = a[r, c] / a[c, c]; for (int k = c; k < 4; k++) a[r, k] -= f * a[c, k]; }
            }
            double x2 = a[2, 3] / a[2, 2];
            return 2 * x2;
        }
    }
}
