using System.Drawing.Drawing2D;
using AudioBridge.Core.Session;
using AudioBridge.Tray.Audio;
using AudioBridge.Tray.Usb;

namespace AudioBridge.Tray.UI;

/// <summary>Small status window: connected phones, live latency, silence toggle, USB status.</summary>
internal sealed class MainForm : Form
{
    private readonly AudioEngine _engine;
    private readonly AdbManager _adb;
    private readonly AppSettings _settings;

    private readonly Label _captureLabel = new() { AutoSize = true };
    private readonly Label _silenceLabel = new() { AutoSize = true };
    private readonly Label _usbLabel = new() { AutoSize = true };
    private readonly Label _hintLabel = new() { AutoSize = true, ForeColor = Color.DarkOrange, MaximumSize = new Size(680, 0) };
    private readonly CheckBox _silenceCheck = new() { AutoSize = true, Text = "Silence PC speakers while a phone is connected" };
    private readonly ListView _clients = new()
    {
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Dock = DockStyle.Fill, GridLines = false,
    };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };

    public MainForm(AudioEngine engine, AdbManager adb, AppSettings settings)
    {
        _engine = engine;
        _adb = adb;
        _settings = settings;

        Text = "Wireless Bark";
        Icon = TrayIconFactory.Create();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 330);
        MinimumSize = new Size(560, 280);
        Font = new Font("Segoe UI", 9.5f);

        foreach (var (name, width) in new[]
                 {
                     ("Phone", 150), ("Link", 55), ("Est. latency", 90), ("Buffer", 70), ("Phone out", 75),
                     ("RTT", 60), ("Jitter", 60), ("Lost/Late/Under", 120),
                 })
            _clients.Columns.Add(name, width);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(10, 8, 10, 4), WrapContents = false,
        };
        top.Controls.AddRange(new Control[] { _captureLabel, _silenceLabel, _usbLabel, _silenceCheck, _hintLabel });

        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 10) };
        listHost.Controls.Add(_clients);

        Controls.Add(listHost);
        Controls.Add(top);

        _silenceCheck.Checked = _settings.SilencePcSpeakers;
        _silenceCheck.CheckedChanged += (_, _) =>
        {
            _settings.SilencePcSpeakers = _silenceCheck.Checked;
            _settings.Save();
            _engine.SilencePcSpeakers = _silenceCheck.Checked;
        };

        _timer.Tick += (_, _) => RefreshUi();
        _timer.Start();
        RefreshUi();
    }

    private void RefreshUi()
    {
        var fmt = _engine.Server.Format;
        _captureLabel.Text = $"Capturing: {_engine.CaptureDevice}" +
                             (fmt is { } f ? $"   •   {f.SampleRate} Hz, {_engine.DeviceChannels}ch → stereo, {f.PacketMs:0.#} ms packets, capture chunk {_engine.CaptureChunkMs:0.#} ms" : "");
        _silenceLabel.Text = _engine.ActiveSilence switch
        {
            SilenceMethod.VirtualCable => "PC speakers: silent (routed to VB-CABLE)",
            SilenceMethod.Mute => "PC speakers: muted",
            _ => _engine.Server.ClientCount > 0 ? "PC speakers: playing" : "PC speakers: playing (no phone connected)",
        } + (_engine.VirtualCableInstalled ? "" : "   •   VB-CABLE not installed (using mute fallback)");
        _usbLabel.Text = $"USB: {_adb.Status}   •   Wi-Fi: listening on UDP {_engine.Server.UdpPort}";

        string hint = "";
        if (_engine.MuteSeemsToSilenceLoopback)
            hint = "Your audio driver silences captured audio when muted. Install VB-CABLE (vb-audio.com/Cable) for silent-PC streaming, or untick the box above.";
        if (_engine.LastError is { } err) hint = (hint.Length > 0 ? hint + "\n" : "") + "Error: " + err;
        _hintLabel.Text = hint;
        _hintLabel.Visible = hint.Length > 0;

        var clients = _engine.Server.Clients;
        var rows = clients.Count == 0
            ? new List<string[]> { new[] { "Waiting for a phone…", "", "", "", "", "", "", "" } }
            : clients.Select(BuildRow).ToList();
        _clients.BeginUpdate();
        while (_clients.Items.Count > rows.Count) _clients.Items.RemoveAt(_clients.Items.Count - 1);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i >= _clients.Items.Count) { _clients.Items.Add(new ListViewItem(row)); continue; }
            var item = _clients.Items[i];
            for (int c = 0; c < row.Length; c++)
                if (item.SubItems[c].Text != row[c]) item.SubItems[c].Text = row[c];
        }
        _clients.EndUpdate();
    }

    private string[] BuildRow(ClientSession c)
    {
        var s = c.Stats;
        string mode = c.Mode == ClientMode.Usb ? "USB" : "Wi-Fi";
        if (!c.HasStats) return new[] { c.Name, mode, "…", "", "", "", "", "" };

        double rtt = double.IsNaN(c.RttMs) ? 0 : c.RttMs;
        double est = _engine.CaptureChunkMs + rtt / 2 + s.BufferMs + s.OutputLatencyMs;
        return new[]
        {
            c.Name, mode, $"{est:0} ms", $"{s.BufferMs:0.0} ms", $"{s.OutputLatencyMs:0.0} ms",
            double.IsNaN(c.RttMs) ? "…" : $"{c.RttMs:0.0} ms", $"{s.JitterMs:0.0} ms",
            $"{s.PacketsLost}/{s.PacketsLate}/{s.Underruns}",
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window just hides it; quit from the tray menu.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}

internal static class TrayIconFactory
{
    private static Icon? _cached;

    /// <summary>Loads the embedded AudioBridge dog/audio icon.</summary>
    public static Icon Create()
    {
        if (_cached != null) return _cached;
        try
        {
            var asm = typeof(TrayIconFactory).Assembly;
            using var stream = asm.GetManifestResourceStream("AudioBridge.Tray.Resources.AudioBridge.ico");
            if (stream != null)
            {
                _cached = new Icon(stream, 32, 32);
                return _cached;
            }
        }
        catch { }

        // Fallback to process icon
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon != null) { _cached = icon; return _cached; }
        }
        catch { }

        // Fallback
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(0x1E, 0x88, 0xE5));
        }
        _cached = Icon.FromHandle(bmp.GetHicon());
        return _cached;
    }
}
