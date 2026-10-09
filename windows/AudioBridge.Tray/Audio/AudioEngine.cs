using System.Collections.Concurrent;
using AudioBridge.Core.Session;
using AudioBridge.Core.Streaming;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace AudioBridge.Tray.Audio;

/// <summary>
/// Orchestrates capture → packetizer → <see cref="StreamServer"/>, follows the default output device,
/// and engages/disengages <see cref="SpeakerSilencer"/> as phones connect/disconnect.
/// All Core Audio COM work happens on one dedicated MTA "control" thread.
/// </summary>
internal sealed class AudioEngine : IDisposable, IMMNotificationClient
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _control;
    private readonly SpeakerSilencer _silencer = new();
    private readonly System.Threading.Timer _diagTimer;

    private MMDeviceEnumerator? _enumerator;
    private LoopbackCapture? _capture;
    private WasapiOut? _keepAlive;
    private Packetizer? _packetizer;
    private volatile bool _restartQueued;
    private int _silentLoopbackStrikes;
    private bool _silenceEnabled;

    public AudioEngine(bool silencePcSpeakers)
    {
        _silenceEnabled = silencePcSpeakers;
        Server = new StreamServer();
        Server.ClientsChanged += () => Post(UpdateSilencer);
        _control = new Thread(ControlLoop) { IsBackground = true, Name = "AudioBridge control" };
        _control.SetApartmentState(ApartmentState.MTA);
        _diagTimer = new System.Threading.Timer(_ => Post(Diagnostics), null, Timeout.Infinite, Timeout.Infinite);
    }

    public StreamServer Server { get; }

    // ---- state for the UI (read from the UI thread) --------------------------------------
    public string CaptureDevice { get; private set; } = "(starting)";
    public int SampleRate { get; private set; }
    public int DeviceChannels { get; private set; }
    public double CaptureChunkMs => _capture?.ChunkMs ?? 0;
    public SilenceMethod ActiveSilence => _silencer.Active;
    public bool VirtualCableInstalled { get; private set; }
    public bool MuteSeemsToSilenceLoopback { get; private set; }
    public string? LastError { get; private set; }

    public bool SilencePcSpeakers
    {
        get => _silenceEnabled;
        set { _silenceEnabled = value; Post(UpdateSilencer); }
    }

    public void Start()
    {
        _control.Start();
        Post(() =>
        {
            _enumerator = new MMDeviceEnumerator();
            SpeakerSilencer.RecoverFromCrash(_enumerator);
            _enumerator.RegisterEndpointNotificationCallback(this);
            RestartCapture();
        });
        Server.Start();
        _diagTimer.Change(1000, 1000);
    }

    private void Post(Action a)
    {
        if (!_queue.IsAddingCompleted) { try { _queue.Add(a); } catch (InvalidOperationException) { } }
    }

    private void ControlLoop()
    {
        foreach (var a in _queue.GetConsumingEnumerable())
        {
            try { a(); }
            catch (Exception ex) { LastError = ex.Message; }
        }
    }

    // ---- capture lifecycle (control thread) -----------------------------------------------

    private void RestartCapture()
    {
        _restartQueued = false;
        StopCapture();
        if (_enumerator == null) return;

        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        VirtualCableInstalled = SpeakerSilencer.FindVirtualCable(_enumerator) != null;
        _silencer.FollowDefault(device);
        CaptureDevice = device.FriendlyName;
        _silentLoopbackStrikes = 0;
        MuteSeemsToSilenceLoopback = false;

        // Keep the audio engine running with an inaudible stream: without it, WASAPI loopback delivers
        // nothing while no app is playing, which would drain the phone's buffer on every pause.
        try
        {
            _keepAlive = new WasapiOut(device, AudioClientShareMode.Shared, false, 50);
            var mix = device.AudioClient.MixFormat;
            _keepAlive.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels)));
            _keepAlive.Play();
        }
        catch { _keepAlive?.Dispose(); _keepAlive = null; }

        var cap = new LoopbackCapture(device.ID);
        cap.FormatReady = (rate, ch) =>
        {
            SampleRate = rate;
            DeviceChannels = ch;
            var p = new Packetizer(rate, Packetizer.DefaultFramesPerPacket(rate), Server.SendAudio);
            _packetizer = p;
            Server.SetFormat(p.Format);
        };
        cap.StereoFrames = span => _packetizer?.PushStereo(span);
        cap.SilentFrames = n => _packetizer?.PushSilence(n);
        cap.Faulted = ex =>
        {
            LastError = "Capture: " + ex.Message;
            ScheduleRestart(500);
        };
        _capture = cap;
        cap.Start();
        LastError = null;
    }

    private void StopCapture()
    {
        _capture?.Dispose();
        _capture = null;
        try { _keepAlive?.Stop(); } catch { }
        _keepAlive?.Dispose();
        _keepAlive = null;
    }

    private void ScheduleRestart(int delayMs)
    {
        if (_restartQueued) return;
        _restartQueued = true;
        Task.Delay(delayMs).ContinueWith(_ => Post(RestartCapture));
    }

    private void UpdateSilencer()
    {
        if (_enumerator == null) return;
        bool want = _silenceEnabled && Server.ClientCount > 0;
        if (want && _silencer.Active == SilenceMethod.None)
        {
            _silencer.Engage(_enumerator);   // may change default device → notification → capture restart
        }
        else if (!want && _silencer.Active != SilenceMethod.None)
        {
            _silencer.Disengage(_enumerator);
        }
    }

    /// <summary>Detect drivers whose mute also silences loopback (then VB-CABLE is required).</summary>
    private void Diagnostics()
    {
        var cap = _capture;
        if (cap == null || _enumerator == null) return;
        float capturedPeak = cap.ReadAndResetPeak();
        if (_silencer.Active != SilenceMethod.Mute) { _silentLoopbackStrikes = 0; MuteSeemsToSilenceLoopback = false; return; }

        float sessionPeak = 0;
        try
        {
            using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
                sessionPeak = Math.Max(sessionPeak, sessions[i].AudioMeterInformation.MasterPeakValue);
        }
        catch { return; }

        if (sessionPeak > 0.01f && capturedPeak < 0.0001f) _silentLoopbackStrikes++;
        else if (capturedPeak > 0.0001f) _silentLoopbackStrikes = 0;
        MuteSeemsToSilenceLoopback = _silentLoopbackStrikes >= 3;
    }

    // ---- IMMNotificationClient (arbitrary COM threads → marshal to control thread) ---------

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) ScheduleRestart(150);
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    // ---- shutdown ------------------------------------------------------------------------

    public void Dispose()
    {
        _diagTimer.Dispose();
        var done = new ManualResetEventSlim();
        Post(() =>
        {
            try
            {
                StopCapture();
                if (_enumerator != null)
                {
                    _silencer.Disengage(_enumerator);
                    try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
                    _enumerator.Dispose();
                }
            }
            finally { done.Set(); }
        });
        done.Wait(3000);
        _queue.CompleteAdding();
        Server.Dispose();
    }
}
