using AudioBridge.Tray.Audio;
using AudioBridge.Tray.UI;
using AudioBridge.Tray.Usb;

namespace AudioBridge.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\WirelessBark.Tray", out bool first);
        if (!first)
        {
            MessageBox.Show("Wireless Bark is already running (see the system tray).", "Wireless Bark");
            return;
        }

        ApplicationConfiguration.Initialize();
        var settings = AppSettings.Load();
        var engine = new AudioEngine(settings.SilencePcSpeakers);
        var adb = new AdbManager();

        try
        {
            engine.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            MessageBox.Show($"Could not open network ports (47800/47802): {ex.Message}", "Wireless Bark");
            return;
        }
        adb.Start();

        // Always restore the speakers, even on an unexpected exit.
        bool disposed = false;
        void Shutdown()
        {
            if (disposed) return;
            disposed = true;
            engine.Dispose();
            adb.Dispose();
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Shutdown();

        var form = new MainForm(engine, adb, settings);
        using var tray = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "Wireless Bark",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        tray.ContextMenuStrip.Items.Add("Open Wireless Bark", null, (_, _) => ShowForm(form));
        tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Application.Exit());
        tray.DoubleClick += (_, _) => ShowForm(form);

        bool startHidden = args.Contains("--minimized");
        if (!startHidden) form.Show();

        Application.ApplicationExit += (_, _) => Shutdown();
        Application.Run();
        tray.Visible = false;
        Shutdown();
    }

    private static void ShowForm(Form f)
    {
        f.Show();
        if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
        f.Activate();
    }
}
