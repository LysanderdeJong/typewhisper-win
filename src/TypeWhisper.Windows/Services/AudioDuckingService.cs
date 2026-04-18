using NAudio.CoreAudioApi;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Windows.Services;

public sealed class AudioDuckingService : IAudioDuckingService
{
    private float _savedVolume;
    private bool _isDucked;

    public void DuckAudio(float factor) => TryRun("duck", () =>
    {
        if (_isDucked) return;
        using var enumerator = new MMDeviceEnumerator();
        var volume = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioEndpointVolume;
        _savedVolume = volume.MasterVolumeLevelScalar;
        volume.MasterVolumeLevelScalar = Math.Clamp(_savedVolume * factor, 0f, 1f);
        _isDucked = true;
    });

    public void RestoreAudio()
    {
        if (!_isDucked) return;
        TryRun("restore", () =>
        {
            using var enumerator = new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                .AudioEndpointVolume.MasterVolumeLevelScalar = _savedVolume;
        });
        _isDucked = false;
    }

    private static void TryRun(string label, Action action)
    {
        try { action(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"AudioDucking {label} failed: {ex.Message}"); }
    }
}
