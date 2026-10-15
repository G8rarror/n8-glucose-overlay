using System;
using System.Drawing;
using System.Windows.Forms;

namespace N8
{
    public sealed class SettingsWindow : Form
    {
        private readonly OverlayApp app;
        private readonly TextBox server = new TextBox(), secret = new TextBox();
        private readonly ComboBox credentialType = new ComboBox(), units = new ComboBox(), monitors = new ComboBox();
        private readonly NumericUpDown x = Number(-100000, 100000), y = Number(-100000, 100000), scale = Number(75, 200), opacity = Number(0, 95), stale = Number(5, 30), poll = Number(120, 900);
        private readonly CheckBox demo = new CheckBox(), locked = new CheckBox(), showSecret = new CheckBox();
        private readonly Label feedback = new Label();
        private bool clearCredential;
        public SettingsWindow(OverlayApp application)
        {
            app = application;
            Text = "N8 Glucose Overlay · Settings";
            ClientSize = new Size(620, 740); MinimumSize = new Size(636, 779);
            AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(246, 248, 251); StartPosition = FormStartPosition.CenterScreen;
            Icon = app.TrayIcon;
            var header = new Panel { Dock = DockStyle.Top, Height = 91, BackColor = Color.FromArgb(16, 28, 40) };
            header.Controls.Add(new Label { Text = "N8  /  GLUCOSE OVERLAY", ForeColor = Color.White, Font = new Font("Segoe UI", 17, FontStyle.Bold), Location = new Point(22, 16), AutoSize = true });
            header.Controls.Add(new Label { Text = "A quiet glance, wherever you work or play.", ForeColor = Color.FromArgb(164, 190, 206), Location = new Point(24, 54), AutoSize = true });
            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 10) };
            var connection = Page(tabs, "Connection");
            var appearance = Page(tabs, "Overlay");
            var about = Page(tabs, "About");
            Heading(connection, "Connect to Gluroo", 20);
            LabelAt(connection, "Use the Nightscout base URL and API credential from Gluroo.\nConnections → DIY / Nightscout (Loop).", 56, 55);
            LabelAt(connection, "Nightscout URL", 118, 24);
            server.SetBounds(24, 145, 540, 28); server.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; connection.Controls.Add(server);
            LabelAt(connection, "Credential format", 184, 24);
            credentialType.SetBounds(24, 211, 330, 30); credentialType.DropDownStyle = ComboBoxStyle.DropDownList;
            credentialType.Items.AddRange(new object[] { "API Secret Token (plain)", "API Secret Header (SHA-1)" }); credentialType.SelectedIndex = 0; connection.Controls.Add(credentialType);
            secret.SetBounds(24, 253, 540, 28); secret.UseSystemPasswordChar = true; secret.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; connection.Controls.Add(secret);
            showSecret.Text = "Show credential"; showSecret.SetBounds(24, 288, 180, 28); showSecret.CheckedChanged += delegate { secret.UseSystemPasswordChar = !showSecret.Checked; }; connection.Controls.Add(showSecret);
            var forget = new Button { Text = "Forget credential", Location = new Point(390, 288), Size = new Size(174, 30) };
            forget.Click += delegate { clearCredential = true; secret.Text = ""; feedback.Text = "Credential will be removed when you save."; }; connection.Controls.Add(forget);
            LabelAt(connection, string.IsNullOrEmpty(app.Config.ProtectedCredential) ? "No credential saved. Encrypted for your Windows user when saved." : "Credential saved. Leave the field empty to keep it.\nChanging the URL requires entering its credential again.", 330, 48);
            demo.Text = "Offline demo (simulated readings, this session only)"; demo.SetBounds(24, 391, 540, 30); connection.Controls.Add(demo);

            Heading(appearance, "Make it yours", 20);
            LabelAt(appearance, "Ctrl+Alt+G: lock / move     ·     Ctrl+Alt+H: hide / show", 58, 28);
            LabelAt(appearance, "Monitor preset", 103, 24);
            monitors.DropDownStyle = ComboBoxStyle.DropDownList; monitors.SetBounds(24, 130, 355, 30);
            for (int i = 0; i < Screen.AllScreens.Length; i++) monitors.Items.Add("Display " + (i + 1) + " · " + Screen.AllScreens[i].Bounds.Width + " × " + Screen.AllScreens[i].Bounds.Height + (Screen.AllScreens[i].Primary ? " (primary)" : ""));
            monitors.SelectedIndex = 0; appearance.Controls.Add(monitors);
            var reset = new Button { Text = "Top right", Location = new Point(396, 130), Size = new Size(168, 30) };
            reset.Click += delegate
            {
                var area = Screen.AllScreens[monitors.SelectedIndex].WorkingArea;
                x.Value = area.Right - (int)(340 * (float)scale.Value / 100 * DeviceDpi / 96) - 24; y.Value = area.Top + 24;
            }; appearance.Controls.Add(reset);
            AddNumber(appearance, "X position (screen pixels)", x, 181, 24); AddNumber(appearance, "Y position", y, 181, 304);
            AddNumber(appearance, "Size (%)", scale, 254, 24); AddNumber(appearance, "Panel opacity (%)", opacity, 254, 304);
            AddNumber(appearance, "Mark stale after (minutes)", stale, 327, 24); AddNumber(appearance, "Poll interval (seconds)", poll, 327, 304);
            units.DropDownStyle = ComboBoxStyle.DropDownList; units.Items.AddRange(new object[] { "mg/dL", "mmol/L" }); units.SetBounds(24, 425, 160, 30);
            LabelAt(appearance, "Display units", 398, 24); appearance.Controls.Add(units);
            locked.Text = "Lock position and pass clicks through"; locked.SetBounds(235, 425, 330, 30); appearance.Controls.Add(locked);

            Heading(about, "Small window. Clear context.", 20);
            LabelAt(about, "Version 1.0.0 · Native Windows desktop app\n\nTray: settings, show / hide, move / lock, refresh, exit.\nDrag in move mode, then lock to restore click-through.\nPanel opacity 0% leaves only the text visible.\nPosition is kept within a connected display.\n\nWorks over desktop and borderless-windowed games.\nExclusive fullscreen and protected game surfaces may cover it.\n\nSecondary display only. Use your CGM or meter for treatment\ndecisions. This app supplies no alarms or dosing advice.\n\nGGC is experimental. Readings can be delayed or unavailable.\nThe displayed time is the measurement time in your local zone.\nCredentials are encrypted with Windows DPAPI for your user.\nNo glucose history, credentials, or response bodies are logged.", 65, 345);
            var link = new LinkLabel { Text = "Gluroo Global Connect API documentation", Location = new Point(24, 422), AutoSize = true };
            link.LinkClicked += delegate { System.Diagnostics.Process.Start("https://gluroo.com/support/gluroo-global-connect-ggc-nightscout-compatible-api/"); }; about.Controls.Add(link);

            var footer = new Panel { Dock = DockStyle.Bottom, Width = 620, Height = 96, BackColor = Color.White };
            feedback.SetBounds(24, 8, 572, 36); feedback.Font = new Font("Segoe UI", 9); feedback.ForeColor = Color.FromArgb(145, 62, 42); feedback.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; footer.Controls.Add(feedback);
            var save = new Button { Text = "Save & connect", BackColor = Color.FromArgb(24, 97, 90), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Location = new Point(398, 49), Size = new Size(198, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            save.Click += Save; footer.Controls.Add(save);
            var cancel = new Button { Text = "Close", Location = new Point(282, 49), Size = new Size(102, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            cancel.Click += delegate { Close(); }; footer.Controls.Add(cancel);
            Controls.Add(tabs); Controls.Add(footer); Controls.Add(header);
            AcceptButton = save; CancelButton = cancel;
            server.Text = app.Config.Server ?? ""; x.Value = Clamp(app.Config.X, x); y.Value = Clamp(app.Config.Y, y);
            scale.Value = app.Config.ScalePercent; opacity.Value = app.Config.PanelOpacity; stale.Value = app.Config.StaleMinutes; poll.Value = app.Config.PollSeconds;
            units.SelectedIndex = app.Config.Mmol ? 1 : 0; locked.Checked = app.Config.Locked; demo.Checked = app.Demo;
            if (!app.Overlay.HotkeysAvailable) feedback.Text = "Global shortcuts are unavailable in this session. Use the tray controls.";
        }
        private void Save(object sender, EventArgs e)
        {
            try
            {
                var next = app.Config.Copy();
                string inputServer = server.Text.Trim();
                next.Server = inputServer.Length == 0 && demo.Checked ? "" : Credentials.ValidateServer(inputServer);
                if (clearCredential) next.ProtectedCredential = null;
                if (secret.Text.Length > 0) next.ProtectedCredential = Credentials.Protect(Credentials.Header(secret.Text, credentialType.SelectedIndex == 1));
                else if (next.Server != app.Config.Server && !string.IsNullOrEmpty(next.Server)) throw new ArgumentException("Enter the credential for this URL.");
                if (!demo.Checked && !clearCredential && string.IsNullOrEmpty(next.ProtectedCredential)) throw new ArgumentException("Enter an API credential or turn on the offline demo.");
                next.X = (int)x.Value; next.Y = (int)y.Value; next.ScalePercent = (int)scale.Value; next.PanelOpacity = (int)opacity.Value;
                next.StaleMinutes = (int)stale.Value; next.PollSeconds = (int)poll.Value; next.Mmol = units.SelectedIndex == 1; next.Locked = locked.Checked;
                app.ApplySettings(next, demo.Checked);
                secret.Text = ""; Close();
            }
            catch (ArgumentException ex) { feedback.Text = ex.Message; }
            catch (System.Security.Cryptography.CryptographicException) { feedback.Text = "Windows could not decrypt the saved credential. Enter it again."; }
            catch (Exception) { feedback.Text = "Could not save settings. Check access to your local app data folder."; }
        }
        private static decimal Clamp(int value, NumericUpDown control) { return Math.Max(control.Minimum, Math.Min(control.Maximum, value)); }
        private static NumericUpDown Number(int minimum, int maximum) { return new NumericUpDown { Minimum = minimum, Maximum = maximum, ThousandsSeparator = false }; }
        private static TabPage Page(TabControl tabs, string name) { var p = new TabPage(name) { BackColor = Color.FromArgb(246, 248, 251), AutoScroll = true }; tabs.TabPages.Add(p); return p; }
        private static void Heading(Control parent, string text, int top) { parent.Controls.Add(new Label { Text = text, Location = new Point(24, top), AutoSize = true, Font = new Font("Segoe UI", 17, FontStyle.Bold), ForeColor = Color.FromArgb(22, 41, 55) }); }
        private static void LabelAt(Control parent, string text, int top, int height) { parent.Controls.Add(new Label { Text = text, Location = new Point(24, top), Size = new Size(548, height), ForeColor = Color.FromArgb(65, 85, 101) }); }
        private static void AddNumber(Control parent, string title, NumericUpDown control, int top, int left)
        {
            parent.Controls.Add(new Label { Text = title, Location = new Point(left, top), Size = new Size(260, 24), ForeColor = Color.FromArgb(65, 85, 101) });
            control.SetBounds(left, top + 28, 240, 28); parent.Controls.Add(control);
        }
    }
}
