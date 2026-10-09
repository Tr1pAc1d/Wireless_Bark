using NAudio.CoreAudioApi;

namespace AudioBridge.Tray.Audio;

public enum SilenceMethod { None, VirtualCable, Mute }

/// <summary>
/// Makes the PC's own speakers silent while a phone is connected, and always restores them.
/// <list type="bullet">
/// <item><b>VirtualCable</b> (preferred): if VB-CABLE ("CABLE Input") is installed, switch the default
/// output to it; we capture from it, so nothing reaches the real speakers.</item>
/// <item><b>Mute</b> (fallback): mute the default device and capture its loopback (works on most drivers).</item>
/// </list>
/// The original state is persisted to disk before changing anything so it can be restored after a crash.
/// Must be used from a single (MTA) thread.
/// </summary>
internal sealed class SpeakerSilencer
{
    private static readonly string StateDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioBridge");
    private static readonly string StatePath = Path.Combine(StateDir, "silence-state.txt");

    private string? _originalDefaultId;
    private string? _mutedId;
    private bool _mutedPrev;

    public SilenceMethod Active { get; private set; }

    public static MMDevice? FindVirtualCable(MMDeviceEnumerator en)
    {
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)) return d;
        }
        return null;
    }

    public void Engage(MMDeviceEnumerator en)
    {
        if (Active != SilenceMethod.None) return;
        using var current = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var cable = FindVirtualCable(en);
        if (cable != null)
        {
            if (current.ID != cable.ID)
            {
                _originalDefaultId = current.ID;
                SaveState();
                DefaultDeviceSwitcher.SetDefault(cable.ID);
            }
            Active = SilenceMethod.VirtualCable;
        }
        else
        {
            MuteDevice(current);
            Active = SilenceMethod.Mute;
        }
    }

    /// <summary>In Mute mode, follow the user if they switch default output while streaming.</summary>
    public void FollowDefault(MMDevice current)
    {
        if (Active != SilenceMethod.Mute || current.ID == _mutedId) return;
        RestoreMute();
        MuteDevice(current);
    }

    public void Disengage(MMDeviceEnumerator en)
    {
        try
        {
            if (Active == SilenceMethod.VirtualCable && _originalDefaultId != null)
            {
                try { DefaultDeviceSwitcher.SetDefault(_originalDefaultId); } catch { }
            }
            if (Active == SilenceMethod.Mute) RestoreMute();
        }
        finally
        {
            _originalDefaultId = null;
            Active = SilenceMethod.None;
            DeleteState();
        }
    }

    /// <summary>Undo changes left behind by a previous crash.</summary>
    public static void RecoverFromCrash(MMDeviceEnumerator en)
    {
        if (!File.Exists(StatePath)) return;
        try
        {
            foreach (var line in File.ReadAllLines(StatePath))
            {
                var parts = line.Split('|');
                if (parts[0] == "default" && parts.Length >= 2)
                {
                    try { DefaultDeviceSwitcher.SetDefault(parts[1]); } catch { }
                }
                else if (parts[0] == "mute" && parts.Length >= 3)
                {
                    try
                    {
                        using var d = en.GetDevice(parts[1]);
                        d.AudioEndpointVolume.Mute = parts[2] == "1";
                    }
                    catch { }
                }
            }
        }
        finally { DeleteState(); }
    }

    private void MuteDevice(MMDevice d)
    {
        _mutedId = d.ID;
        _mutedPrev = d.AudioEndpointVolume.Mute;
        SaveState();
        d.AudioEndpointVolume.Mute = true;
    }

    private void RestoreMute()
    {
        if (_mutedId == null) return;
        try
        {
            using var en = new MMDeviceEnumerator();
            using var d = en.GetDevice(_mutedId);
            d.AudioEndpointVolume.Mute = _mutedPrev;
        }
        catch { }
        _mutedId = null;
    }

    private void SaveState()
    {
        Directory.CreateDirectory(StateDir);
        var lines = new List<string>();
        if (_originalDefaultId != null) lines.Add($"default|{_originalDefaultId}");
        if (_mutedId != null) lines.Add($"mute|{_mutedId}|{(_mutedPrev ? 1 : 0)}");
        File.WriteAllLines(StatePath, lines);
    }

    private static void DeleteState()
    {
        try { File.Delete(StatePath); } catch { }
    }
}
