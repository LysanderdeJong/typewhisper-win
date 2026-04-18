using System.Runtime.InteropServices;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Windows.Services;

public sealed partial class MediaPauseService : IMediaPauseService
{
    private bool _didPause;

    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    public void PauseMedia() => TryRun("pause", () =>
    {
        if (_didPause) return;
        SendMediaPlayPause();
        _didPause = true;
    });

    public void ResumeMedia()
    {
        if (!_didPause) return;
        TryRun("resume", SendMediaPlayPause);
        _didPause = false;
    }

    private static void TryRun(string label, Action action)
    {
        try { action(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MediaPause {label} failed: {ex.Message}"); }
    }

    private static void SendMediaPlayPause()
    {
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY, 0);
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);
    }
}
