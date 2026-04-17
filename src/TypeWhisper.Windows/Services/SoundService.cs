using System.Diagnostics.CodeAnalysis;
using System.IO;
using NAudio.Wave;

namespace TypeWhisper.Windows.Services;

public sealed class SoundService
{
    private static readonly string SoundsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Sounds");

    private readonly byte[]? _start = LoadWav("start.wav");
    private readonly byte[]? _stop = LoadWav("stop.wav");
    private readonly byte[]? _success = LoadWav("success.wav");
    private readonly byte[]? _error = LoadWav("error.wav");

    public bool IsEnabled { get; set; } = true;

    public void PlayStartSound() => Play(_start);
    public void PlayStopSound() => Play(_stop);
    public void PlaySuccessSound() => Play(_success);
    public void PlayErrorSound() => Play(_error);

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership is transferred to the PlaybackStopped handler after a successful "
            + "Init+Play; the catch block disposes the locals along the failure path.")]
    private void Play(byte[]? wav)
    {
        if (!IsEnabled || wav is null) return;

        MemoryStream? ms = null;
        WaveFileReader? reader = null;
        WaveOutEvent? output = null;
        try
        {
            ms = new MemoryStream(wav);
            reader = new WaveFileReader(ms);
            output = new WaveOutEvent();
            output.Init(reader);

            // Capture locals so the handler closes over concrete non-null references;
            // the outer variables are nulled below to prevent the catch-fallback from disposing.
            var capturedReader = reader;
            var capturedMs = ms;
            var capturedOutput = output;
            output.PlaybackStopped += (_, _) =>
            {
                capturedReader.Dispose();
                capturedMs.Dispose();
                capturedOutput.Dispose();
            };
            output.Play();

            // Ownership has been transferred to the PlaybackStopped handler.
            reader = null;
            ms = null;
            output = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Sound playback failed: {ex.Message}");
            output?.Dispose();
            reader?.Dispose();
            ms?.Dispose();
        }
    }

    private static byte[]? LoadWav(string fileName)
    {
        try
        {
            var path = Path.Combine(SoundsPath, fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
