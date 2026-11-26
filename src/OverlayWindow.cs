using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace N8
{
    internal static class Native
    {
        public const int Layered = 0x80000, Transparent = 0x20, NoActivate = 0x08000000, ToolWindow = 0x80;
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct Size { public int W, H; public Size(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct Blend { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref Point point, ref Size size, IntPtr src, ref Point origin, int key, ref Blend blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    }

    public sealed class OverlayWindow : Form
    {
        private Settings settings;
        public DisplayState State = new DisplayState();
        public Action PositionChanged;
        public Action<int> Hotkey;
        public bool HotkeysAvailable { get; private set; }
        public int LockHotkeyError { get; private set; }
        public int VisibilityHotkeyError { get; private set; }
        private int renderDpi = 96;
        public OverlayWindow(Settings initial)
        {
            settings = initial;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            base.Text = "N8 Glucose Overlay";
            Location = new System.Drawing.Point(initial.X, initial.Y);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.Layered | Native.NoActivate | Native.ToolWindow;
                if (settings == null || settings.Locked) cp.ExStyle |= Native.Transparent;
                return cp;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            bool a = Native.RegisterHotKey(Handle, 1, 0x4003, (uint)Keys.G);
            LockHotkeyError = a ? 0 : Marshal.GetLastWin32Error();
            bool b = Native.RegisterHotKey(Handle, 2, 0x4003, (uint)Keys.H);
            VisibilityHotkeyError = b ? 0 : Marshal.GetLastWin32Error();
            HotkeysAvailable = a && b;
            renderDpi = (int)Native.GetDpiForWindow(Handle);
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            Native.UnregisterHotKey(Handle, 1); Native.UnregisterHotKey(Handle, 2);
            base.OnHandleDestroyed(e);
        }
        public void Apply(Settings value)
        {
            settings = value;
            if (!IsHandleCreated) CreateControl();
            int style = Native.GetWindowLong(Handle, -20);
            Native.SetWindowLong(Handle, -20, settings.Locked ? style | Native.Transparent : style & ~Native.Transparent);
            Location = new System.Drawing.Point(settings.X, settings.Y);
            Render();
            ClampToScreen();
        }
        public void ClampToScreen()
        {
            var bounds = new Rectangle(Location, Size);
            bool fullyVisible = false;
            foreach (var screen in Screen.AllScreens) if (screen.WorkingArea.Contains(bounds)) fullyVisible = true;
            if (!fullyVisible)
            {
                var area = Screen.FromRectangle(bounds).WorkingArea;
                Location = new System.Drawing.Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
            }
            settings.X = Left; settings.Y = Top;
        }
        public void KeepOnTop()
        {
            if (Visible) Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13);
        }
        public void Render()
        {
            if (IsDisposed || !IsHandleCreated) return;
            using (var bitmap = Draw(settings, State, DateTimeOffset.UtcNow, renderDpi / 96f))
            {
                var dc = Native.GetDC(IntPtr.Zero);
                var memory = Native.CreateCompatibleDC(dc);
                IntPtr hbitmap = IntPtr.Zero, previous = IntPtr.Zero;
                try
                {
                    hbitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                    previous = Native.SelectObject(memory, hbitmap);
                    var target = new Native.Point(Left, Top);
                    var origin = new Native.Point(0, 0);
                    var size = new Native.Size(bitmap.Width, bitmap.Height);
                    var blend = new Native.Blend { Op = 0, Alpha = 255, Format = 1 };
                    if (!Native.UpdateLayeredWindow(Handle, dc, ref target, ref size, memory, ref origin, 0, ref blend, 2))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not draw the overlay.");
                }
                finally
                {
                    if (previous != IntPtr.Zero) Native.SelectObject(memory, previous);
                    if (hbitmap != IntPtr.Zero) Native.DeleteObject(hbitmap);
                    Native.DeleteDC(memory); Native.ReleaseDC(IntPtr.Zero, dc);
                }
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!settings.Locked && e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
            }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x312 && Hotkey != null) { Hotkey(m.WParam.ToInt32()); return; }
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
            base.WndProc(ref m);
            if (m.Msg == 0x232)
            {
                ClampToScreen();
                if (PositionChanged != null) PositionChanged();
            }
            if (m.Msg == 0x2E0)
            {
                renderDpi = m.WParam.ToInt32() & 0xffff;
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(delegate { Render(); ClampToScreen(); }));
            }
        }
        public static Bitmap Draw(Settings s, DisplayState state, DateTimeOffset now, float dpiScale)
        {
            float scale = s.ScalePercent / 100f * dpiScale;
            var bitmap = new Bitmap((int)Math.Ceiling(340 * scale), (int)Math.Ceiling(194 * scale), PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent); g.ScaleTransform(scale, scale);
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var shape = Rounded(new RectangleF(1, 1, 338, 192), 18))
                {
                    if (s.PanelOpacity > 0)
                    {
                        using (var panel = new SolidBrush(Color.FromArgb(s.PanelOpacity * 255 / 100, 13, 22, 31))) g.FillPath(panel, shape);
                        using (var border = new Pen(Color.FromArgb(75, 135, 158, 174))) g.DrawPath(border, shape);
                    }
                    if (!s.Locked) using (var pen = new Pen(Color.FromArgb(93, 223, 203), 2)) g.DrawPath(pen, shape);
                }
                var r = state.Reading;
                bool stale = r != null && r.IsStale(now, s.StaleMinutes);
                var accent = state.Demo ? Color.FromArgb(163, 165, 255) : (state.Error ? Color.FromArgb(255, 153, 143) : (stale ? Color.FromArgb(255, 205, 112) : Color.FromArgb(103, 232, 204)));
                Text(g, "N8  /  GLUCOSE", 18, 14, 10, FontStyle.Bold, Color.FromArgb(218, 229, 236), 185, 22);
                string badge = state.Demo ? "DEMO" : (stale ? "STALE" : (state.Error ? "OFFLINE" : (r == null ? "WAITING" : "CURRENT")));
                Text(g, badge, 238, 14, 9, FontStyle.Bold, accent, 84, 22, StringAlignment.Far);
                Text(g, r == null ? "—" : r.Value(s.Mmol), 15, 34, 45, FontStyle.Bold, stale ? Color.FromArgb(255, 211, 137) : Color.White, 205, 74);
                Text(g, r == null ? "?" : r.Arrow, 222, 38, 35, FontStyle.Regular, accent, 102, 62);
                string unit = s.Mmol ? "mmol/L" : "mg/dL";
                string delta = r == null || stale ? null : r.DeltaText(s.Mmol);
                Text(g, delta == null ? unit : unit + "   " + delta + " / 5m", 20, 103, 10, FontStyle.Regular, Color.FromArgb(202, 216, 227), 160, 18);
                string range = r == null || stale ? null : r.RangeText(s.Mmol);
                string estimate = range != null ? "est. " + range + " in " + Reading.ForecastMinutes + "m" : (r != null && !stale && r.Noisy ? "est. — noisy readings" : null);
                if (estimate != null) Text(g, estimate, 166, 103, 10, FontStyle.Regular, Color.FromArgb(160, 178, 192), 156, 18, StringAlignment.Far);
                // Insulin row: Gluroo's IOB, and how old the newest pump data behind it is.
                var amber = Color.FromArgb(255, 205, 112);
                bool iobShown = state.Insulin != null && state.Insulin.IsShowable(now);
                Text(g, "IOB", 20, 126, 10, FontStyle.Bold, Color.FromArgb(160, 178, 192), 40, 20);
                Text(g, iobShown ? state.Insulin.IobText() + " u" : "—", 52, 122, 13, FontStyle.Bold, Color.White, 110, 24);
                TimeSpan? pumpAge = state.PumpData == null ? (TimeSpan?)null : now - state.PumpData.Value;
                bool pumpLate = pumpAge == null || pumpAge.Value.TotalMinutes > PumpSync.LateMinutes;
                string pumpText = pumpAge == null ? "pump data age unknown" : "pump data " + (state.PumpDataApprox ? "~" : "") + Age(pumpAge.Value).Replace(" ago", " old");
                Text(g, pumpText, 160, 127, 9, FontStyle.Regular, pumpLate ? amber : Color.FromArgb(160, 178, 192), 162, 18, StringAlignment.Far);
                string age = r == null ? "No reading received" : "Read " + r.Time.ToLocalTime().ToString("dd MMM · HH:mm:ss") + "  ·  " + Age(now - r.Time);
                Text(g, age, 20, 150, 9, FontStyle.Regular, Color.FromArgb(227, 235, 242), 303, 18);
                string status = !s.Locked ? "MOVE MODE · drag here · Ctrl+Alt+G to lock" : state.Status;
                if (stale && s.Locked) status = "STALE · last known reading · " + state.Status;
                string threshold = r == null || stale || state.Error || !s.Locked ? null : r.ThresholdText(s.Mmol);
                Text(g, threshold ?? status, 20, 171, 8, threshold != null ? FontStyle.Bold : FontStyle.Regular, threshold != null ? amber : accent, 301, 17);
            }
            return bitmap;
        }
        public static string Age(TimeSpan age)
        {
            if (age.TotalSeconds < 0) return "Clock ahead";
            if (age.TotalSeconds < 60) return ((int)age.TotalSeconds) + "s ago";
            if (age.TotalMinutes < 60) return ((int)age.TotalMinutes) + "m ago";
            if (age.TotalHours < 24) return ((int)age.TotalHours) + "h " + age.Minutes + "m ago";
            return ((int)age.TotalDays) + "d ago";
        }
        private static GraphicsPath Rounded(RectangleF r, int radius)
        {
            var p = new GraphicsPath(); int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        private new static void Text(Graphics g, string text, float x, float y, float size, FontStyle style, Color color, float width, float height, StringAlignment alignment = StringAlignment.Near)
        {
            using (var font = new Font("Segoe UI", size, style, GraphicsUnit.Point))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, Alignment = alignment })
                g.DrawString(text, font, brush, new RectangleF(x, y, width, height), format);
        }
    }
}
