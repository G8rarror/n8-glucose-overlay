using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace N8
{
    public sealed class OverlayApp : ApplicationContext
    {
        public Settings Config { get; private set; }
        public OverlayWindow Overlay { get; private set; }
        public Icon TrayIcon { get; private set; }
        public bool Demo { get; private set; }
        private readonly SettingsStore store;
        private readonly string ratesPath;
        private readonly Dictionary<string, RateRecord> rates;
        private PollPolicy policy;
        private readonly GlurooClient client = new GlurooClient();
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly ToolStripMenuItem visibility, lockMenu;
        private SettingsWindow settingsWindow;
        private CancellationTokenSource request = new CancellationTokenSource();
        private string header;
        private bool busy, exiting;
        private int generation, seconds;
        private DateTimeOffset demoTime = DateTimeOffset.MinValue;
        private readonly bool quiet;
        public OverlayApp(string dataRoot, bool demo, bool quietMode)
        {
            quiet = quietMode;
            store = new SettingsStore(dataRoot); ratesPath = Path.Combine(dataRoot, "request-limits.json");
            string startupError = null;
            try { Config = store.Load(); }
            catch (Exception) { Config = new Settings(); startupError = "Saved settings could not be read. Re-enter your connection in Settings."; }
            rates = LoadRates();
            Demo = demo;
            try { if (!string.IsNullOrEmpty(Config.ProtectedCredential)) { header = Credentials.Unprotect(Config.ProtectedCredential); Credentials.ValidateServer(Config.Server); } }
            catch (Exception) { header = null; startupError = "Saved credentials could not be opened. Enter them again in Settings."; }
            policy = PolicyFor(Config.Server);
            TrayIcon = MakeIcon();
            Overlay = new OverlayWindow(Config) { State = new DisplayState { Demo = Demo } };
            Overlay.PositionChanged = delegate { Config.X = Overlay.Left; Config.Y = Overlay.Top; SavePosition(); };
            Overlay.Hotkey = delegate(int id) { if (id == 1) ToggleLock(); else ToggleVisibility(); };
            var menu = new ContextMenuStrip();
            var title = menu.Items.Add("N8 Glucose Overlay"); title.Enabled = false;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings…", null, delegate { OpenSettings(); });
            visibility = new ToolStripMenuItem("Hide overlay    Ctrl+Alt+H", null, delegate { ToggleVisibility(); }); menu.Items.Add(visibility);
            lockMenu = new ToolStripMenuItem("Click-through locked    Ctrl+Alt+G", null, delegate { ToggleLock(); }); menu.Items.Add(lockMenu);
            menu.Items.Add("Move to primary display", null, delegate
            {
                Config.X = Screen.PrimaryScreen.WorkingArea.Left + 24; Config.Y = Screen.PrimaryScreen.WorkingArea.Top + 24;
                Overlay.Apply(Config); SavePosition();
            });
            menu.Items.Add("Refresh when allowed", null, async delegate { await TickAsync(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });
            tray = new NotifyIcon { Icon = TrayIcon, Text = "N8 Glucose Overlay", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { OpenSettings(); };
            Overlay.Show(); Overlay.Apply(Config); UpdateMenus();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            timer.Interval = 1000;
            timer.Tick += async delegate { await TickAsync(); };
            timer.Start();
            if (startupError != null && !quiet) MessageBox.Show(startupError, "N8 Glucose Overlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!Demo && header == null && !quiet) OpenSettings();
        }
        private Dictionary<string, RateRecord> LoadRates() { return RateFile.Load(ratesPath, Config.Server); }
        private PollPolicy PolicyFor(string server)
        {
            RateRecord record;
            return server != null && rates.TryGetValue(server, out record) ? record.ToPolicy() : new PollPolicy();
        }
        private void SaveRates()
        {
            if (string.IsNullOrEmpty(Config.Server)) return;
            rates[Config.Server] = RateRecord.From(policy);
            RateFile.Save(ratesPath, rates);
        }
        public void ApplySettings(Settings next, bool demo)
        {
            next.Validate();
            string nextHeader = string.IsNullOrEmpty(next.ProtectedCredential) ? null : Credentials.Unprotect(next.ProtectedCredential);
            bool hostChanged = next.Server != Config.Server;
            bool connectionChanged = hostChanged || nextHeader != header;
            bool modeChanged = Demo != demo;
            // Save first; failed writes leave the running configuration intact.
            store.Save(next);
            generation++; request.Cancel(); request.Dispose(); request = new CancellationTokenSource();
            if (hostChanged) policy = PolicyFor(next.Server);
            if (connectionChanged) policy.Unauthorized = false;
            Config = next; header = nextHeader; Demo = demo;
            if (connectionChanged || modeChanged) Overlay.State = new DisplayState { Demo = demo };
            try { SaveRates(); }
            catch (Exception)
            {
                policy.Next = DateTimeOffset.UtcNow.AddMinutes(15);
                Overlay.State.Error = true; Overlay.State.Status = "Could not save request limit · paused 15m";
            }
            Overlay.Apply(Config); SavePosition(); UpdateMenus();
            if (settingsWindow != null) settingsWindow.Text = "N8 Glucose Overlay · Settings";
            // The timer initiates the request after this event returns.
        }
        private void SavePosition()
        {
            try { store.Save(Config); }
            catch (Exception) { Overlay.State.Status = "Position could not be saved"; Overlay.State.Error = true; }
        }
        private void DisplayChanged(object sender, EventArgs e)
        {
            if (!Overlay.IsDisposed && Overlay.IsHandleCreated) Overlay.BeginInvoke(new Action(delegate { Overlay.ClampToScreen(); Overlay.Render(); SavePosition(); }));
        }
        private async Task TickAsync()
        {
            if (exiting) return;
            seconds++;
            if (seconds % 5 == 0) Overlay.KeepOnTop();
            if (seconds % 60 == 1) RefreshPumpData();
            var now = DateTimeOffset.UtcNow;
            if (Demo)
            {
                if (now - demoTime > TimeSpan.FromSeconds(120))
                {
                    demoTime = now;
                    Overlay.State.Reading = new Reading { Mgdl = 112, Direction = "Flat", Time = now.AddSeconds(-35), Delta = 2, Forecast = 120 };
                    Overlay.State.Insulin = new InsulinStatus { Iob = 1.35, Fetched = now };
                    Overlay.State.PumpData = now.AddMinutes(-23);
                }
                Overlay.State.Demo = true; Overlay.State.Status = "SIMULATED · no connection to Gluroo"; Overlay.State.Error = false;
            }
            else if (header == null) { Overlay.State.Status = "Open tray Settings to connect"; }
            else if (policy.Unauthorized) { Overlay.State.Status = "Authentication failed · update credentials"; Overlay.State.Error = true; }
            else if (!busy && policy.CanPoll(now))
            {
                busy = true;
                int currentGeneration = generation;
                policy.Begin(now, Config.PollSeconds);
                try
                {
                    // Persist the interval before sending, including across restart.
                    SaveRates();
                    Overlay.State.Status = "Connecting to Gluroo…";
                    var reading = await client.FetchAsync(Config.Server, header, request.Token);
                    if (exiting || generation != currentGeneration) return;
                    policy.Success(); Overlay.State.Accept(reading);
                    policy.Schedule(DateTimeOffset.UtcNow, Overlay.State.Reading == null ? (DateTimeOffset?)null : Overlay.State.Reading.Time, Config.PollSeconds);
                    await RefreshIobAsync(currentGeneration);
                }
                catch (OperationCanceledException)
                {
                    if (!exiting && generation == currentGeneration) Failure(0, "Connection timed out · retry scheduled");
                }
                catch (ApiException ex)
                {
                    if (!exiting && generation == currentGeneration)
                    {
                        string message = ex.StatusCode == 401 || ex.StatusCode == 403 ? "Authentication failed · update credentials" :
                            ex.StatusCode == 429 ? "Daily API limit · paused until 00:00 UTC" :
                            ex.StatusCode >= 300 && ex.StatusCode < 400 ? "Redirect blocked · check Gluroo URL" :
                            ex.StatusCode == 404 ? "Endpoint unavailable · check Gluroo URL" : "Gluroo unavailable · retry scheduled";
                        Failure(ex.StatusCode, message);
                    }
                }
                catch (FormatException) { if (!exiting && generation == currentGeneration) Failure(0, "Incompatible response · check connection"); }
                catch (Exception) { if (!exiting && generation == currentGeneration) Failure(0, "Connection unavailable · retry scheduled"); }
                finally
                {
                    busy = false;
                    if (!exiting && generation == currentGeneration)
                    {
                        try { SaveRates(); }
                        catch (Exception) { policy.Next = DateTimeOffset.UtcNow.AddMinutes(15); Overlay.State.Status = "Could not save request limit · paused 15m"; Overlay.State.Error = true; }
                    }
                }
            }
            else if (!busy && Overlay.State.Reading == null && !policy.Unauthorized)
                Overlay.State.Status = "Next request " + policy.Next.ToLocalTime().ToString("HH:mm:ss") + " · request interval protected";
            if (!exiting)
            {
                Overlay.Render();
                var r = Overlay.State.Reading;
                var ins = Overlay.State.Insulin;
                string iob = ins != null && ins.IsShowable(DateTimeOffset.UtcNow) ? " · IOB " + ins.IobText() + "u" : "";
                string tip = "N8 · " + (Demo ? "DEMO · " : "") + (r == null ? Overlay.State.Status : r.Value(Config.Mmol) + " " + (Config.Mmol ? "mmol/L" : "mg/dL") + " " + r.Arrow + iob + " · " + OverlayWindow.Age(DateTimeOffset.UtcNow - r.Time) + (r.IsStale(DateTimeOffset.UtcNow, Config.StaleMinutes) ? " · STALE" : ""));
                tray.Text = tip.Substring(0, Math.Min(63, tip.Length));
            }
        }
        private void Failure(int status, string message)
        {
            policy.Fail(status, DateTimeOffset.UtcNow); Overlay.State.Error = true; Overlay.State.Status = message;
        }
        /// <summary>Second GET in the same poll, at most every IobPollSeconds. A failure only blanks IOB; glucose is unaffected.</summary>
        private async Task RefreshIobAsync(int currentGeneration)
        {
            var known = Overlay.State.Insulin;
            if (known != null && DateTimeOffset.UtcNow - known.Fetched < TimeSpan.FromSeconds(IobPollSeconds - 5)) return;
            try
            {
                var status = await client.FetchIobAsync(Config.Server, header, request.Token);
                if (!exiting && generation == currentGeneration && status != null) Overlay.State.Insulin = status;
                // No tconnectsync on this PC: estimate pump-data age from Gluroo. One more GET per IOB refresh.
                if (!localPumpLog)
                {
                    var pumpTime = await client.FetchPumpDataTimeAsync(Config.Server, header, request.Token);
                    if (!exiting && generation == currentGeneration && !localPumpLog && pumpTime != null) { Overlay.State.PumpData = pumpTime; Overlay.State.PumpDataApprox = true; }
                }
            }
            catch (ApiException ex) { if (ex.StatusCode == 429 && !exiting && generation == currentGeneration) Failure(429, "Daily API limit · paused until 00:00 UTC"); }
            catch (Exception) { }
        }
        private const int IobPollSeconds = 600;
        private bool localPumpLog;
        private void RefreshPumpData()
        {
            if (Demo) return;
            var latest = PumpSync.Read(PumpSync.DefaultFolder);
            localPumpLog = latest != null;
            if (latest != null) { Overlay.State.PumpData = latest; Overlay.State.PumpDataApprox = false; }
        }
        private void ToggleVisibility()
        {
            if (Overlay.Visible) Overlay.Hide(); else { Overlay.Show(); Overlay.Render(); Overlay.KeepOnTop(); }
            UpdateMenus();
        }
        private void ToggleLock() { Config.Locked = !Config.Locked; Overlay.Apply(Config); SavePosition(); UpdateMenus(); }
        private void UpdateMenus()
        {
            lockMenu.Checked = Config.Locked;
            lockMenu.Text = Config.Locked ? "Click-through locked    Ctrl+Alt+G" : "Move mode    Ctrl+Alt+G";
            visibility.Text = Overlay.Visible ? "Hide overlay    Ctrl+Alt+H" : "Show overlay    Ctrl+Alt+H";
        }
        public void OpenSettings()
        {
            if (settingsWindow == null || settingsWindow.IsDisposed) settingsWindow = new SettingsWindow(this);
            settingsWindow.Show(); settingsWindow.Activate();
        }
        public static Icon MakeIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bitmap))
            using (var font = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.Clear(Color.FromArgb(16, 33, 44));
                using (var brush = new SolidBrush(Color.FromArgb(103, 232, 204))) g.FillRectangle(brush, 0, 28, 32, 4);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; g.DrawString("N8", font, Brushes.White, 5, 6);
                IntPtr handle = bitmap.GetHicon();
                try { using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        protected override void ExitThreadCore()
        {
            if (exiting) return;
            exiting = true; generation++; timer.Stop(); timer.Dispose(); request.Cancel();
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            if (settingsWindow != null) settingsWindow.Dispose();
            tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose();
            Overlay.Dispose(); TrayIcon.Dispose(); client.Dispose(); request.Dispose();
            base.ExitThreadCore();
        }
    }
}
