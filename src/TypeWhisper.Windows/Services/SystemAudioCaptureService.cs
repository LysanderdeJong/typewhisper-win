using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Captures system audio output (what you hear) via WASAPI Loopback.
/// Can mix with microphone input for combined capture.
/// </summary>
public sealed class SystemAudioCaptureService : IDisposable
{
    private const int DefaultSampleCapacity = 16000 * 60;

    private WasapiLoopbackCapture? _capture;
    private float[] _samples = new float[DefaultSampleCapacity];
    private int _samplesCount;
    private bool _isRecording;

    public bool IsRecording => _isRecording;
    public event Action<float>? AudioLevelChanged;

    /// <summary>
    /// Starts capturing system audio output.
    /// </summary>
    public void StartCapture()
    {
        if (_isRecording) return;

        _capture = new WasapiLoopbackCapture();
        _samplesCount = 0;

        _capture.DataAvailable += (_, e) =>
        {
            var bytesRecorded = e.BytesRecorded;
            if (bytesRecorded <= 0)
                return;

            var samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, bytesRecorded));
            EnsureCapacity(_samplesCount + samples.Length);
            samples.CopyTo(_samples.AsSpan(_samplesCount));
            _samplesCount += samples.Length;

            AudioLevelChanged?.Invoke(GetPeakLevel(samples));
        };

        _capture.RecordingStopped += (_, _) => { _isRecording = false; };

        _capture.StartRecording();
        _isRecording = true;
    }

    /// <summary>
    /// Stops capturing and returns the captured samples resampled to 16kHz mono.
    /// </summary>
    public float[] StopCapture()
    {
        if (!_isRecording || _capture is null) return [];

        _capture.StopRecording();
        _isRecording = false;

        var sourceSampleRate = _capture.WaveFormat.SampleRate;
        var sourceChannels = _capture.WaveFormat.Channels;

        _capture.Dispose();
        _capture = null;

        // Downmix to mono if stereo
        var captured = _samples.AsSpan(0, _samplesCount);
        var mono = sourceChannels > 1
            ? DownmixToMono(captured, sourceChannels)
            : captured.ToArray();

        // Resample to 16kHz
        if (sourceSampleRate != 16000)
            mono = Resample(mono, sourceSampleRate, 16000);

        return mono;
    }

    private void EnsureCapacity(int requiredLength)
    {
        if (requiredLength <= _samples.Length)
            return;

        var newLength = _samples.Length;
        while (newLength < requiredLength)
            newLength = Math.Max(requiredLength, newLength * 2);

        Array.Resize(ref _samples, newLength);
    }

    private static float GetPeakLevel(ReadOnlySpan<float> samples)
    {
        var max = 0f;
        var i = 0;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            var vectorMax = Vector<float>.Zero;
            var lastVectorStart = samples.Length - Vector<float>.Count;
            for (; i <= lastVectorStart; i += Vector<float>.Count)
                vectorMax = Vector.Max(vectorMax, Vector.Abs(new Vector<float>(samples.Slice(i, Vector<float>.Count))));

            for (var j = 0; j < Vector<float>.Count; j++)
                max = Math.Max(max, vectorMax[j]);
        }

        for (; i < samples.Length; i++)
            max = Math.Max(max, Math.Abs(samples[i]));

        return max;
    }

    private static float[] DownmixToMono(ReadOnlySpan<float> samples, int channels)
    {
        var frameCount = samples.Length / channels;
        var mono = new float[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
                sum += samples[(i * channels) + c];

            mono[i] = sum / channels;
        }

        return mono;
    }

    private static float[] Resample(ReadOnlySpan<float> samples, int fromRate, int toRate)
    {
        var ratio = (double)toRate / fromRate;
        var outputLength = (int)(samples.Length * ratio);
        var output = new float[outputLength];

        for (var i = 0; i < outputLength; i++)
        {
            var srcIndex = i / ratio;
            var idx = (int)srcIndex;
            var frac = (float)(srcIndex - idx);

            if (idx + 1 < samples.Length)
                output[i] = (samples[idx] * (1 - frac)) + (samples[idx + 1] * frac);
            else if (idx < samples.Length)
                output[i] = samples[idx];
        }

        return output;
    }

    public void Dispose()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
    }
}
