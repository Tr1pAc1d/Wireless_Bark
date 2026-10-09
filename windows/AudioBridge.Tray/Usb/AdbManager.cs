using System.Diagnostics;
using AudioBridge.Core.Protocol;

namespace AudioBridge.Tray.Usb;

/// <summary>
/// Watches for USB-connected phones (USB debugging on) and sets up
/// <c>adb reverse tcp:47802 tcp:47802</c> so the phone app can reach the PC at 127.0.0.1:47802.
/// </summary>
internal sealed class AdbManager : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _reversed = new();
    private readonly int _port;

    public AdbManager(int port = Wire.TcpPort)
    {
        _port = port;
        AdbPath = LocateAdb();
    }

    public string? AdbPath { get; }
    public string Status { get; private set; } = "Starting…";
    public IReadOnlyList<string> Devices { get; private set; } = Array.Empty<string>();

    public void Start()
    {
        if (AdbPath == null)
        {
            Status = "adb not found - USB mode unavailable";
            return;
        }
        _ = Task.Run(Loop);
    }

    private static string? LocateAdb()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools", "adb.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "adb.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var (code, output) = await Run("devices");
                var ready = new List<string>();
                var unauthorized = 0;
                foreach (var line in output.Split('\n'))
                {
                    var parts = line.Trim().Split('\t');
                    if (parts.Length != 2) continue;
                    if (parts[1] == "device") ready.Add(parts[0]);
                    else if (parts[1] == "unauthorized") unauthorized++;
                }

                foreach (var serial in ready)
                {
                    if (_reversed.Contains(serial)) continue;
                    var (rc, _) = await Run($"-s {serial} reverse tcp:{_port} tcp:{_port}");
                    if (rc == 0) _reversed.Add(serial);
                }
                _reversed.RemoveWhere(s => !ready.Contains(s));
                Devices = ready;

                Status = ready.Count > 0
                    ? $"USB ready: {string.Join(", ", ready)}"
                    : unauthorized > 0
                        ? "Phone connected - accept the 'Allow USB debugging' prompt on the phone"
                        : "No USB phone (enable USB debugging & plug in)";
            }
            catch (Exception ex)
            {
                Status = "adb error: " + ex.Message;
            }
            try { await Task.Delay(2000, _cts.Token); } catch { return; }
        }
    }

    private async Task<(int, string)> Run(string args)
    {
        var psi = new ProcessStartInfo(AdbPath!, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(); } catch { } return (-1, ""); }
        return (p.ExitCode, await stdout + await stderr);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (AdbPath == null) return;
        // Remove our tunnels so we don't leave state behind on the phone.
        foreach (var s in _reversed.ToArray())
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(AdbPath, $"-s {s} reverse --remove tcp:{_port}")
                    { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit(2000);
            }
            catch { }
        }
    }
}
