using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("N8 Glucose Overlay")]
[assembly: AssemblyDescription("A secondary Gluroo glucose display for Windows")]
[assembly: AssemblyCompany("N8")]
[assembly: AssemblyProduct("N8 Glucose Overlay")]
[assembly: AssemblyVersion("1.1.0.0")]

namespace N8
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        internal static string MutexName() { return "Local\\N8GlucoseOverlay-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; }
        [STAThread]
        private static int Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--self-test")) return Verification.Run(args);
            if (args.Contains("--preview")) return Verification.Preview(args);
            if (args.Contains("--smoke-test")) return Verification.Smoke(args);
            if (args.Contains("--treatments-check")) return TreatmentsCheck.Run(args);
            bool created;
            using (var mutex = new Mutex(true, MutexName(), out created))
            {
                if (!created) { MessageBox.Show("N8 Glucose Overlay is already running. Open it from the system tray (including hidden icons).", "N8 Glucose Overlay"); return 0; }
                try
                {
                    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "N8GlucoseOverlay");
                    using (var context = new OverlayApp(root, args.Contains("--demo"), false)) Application.Run(context);
                    return 0;
                }
                catch (Exception)
                {
                    MessageBox.Show("N8 could not start. Check Windows .NET Framework 4.8 and write access to your local app data folder.", "N8 Glucose Overlay", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
