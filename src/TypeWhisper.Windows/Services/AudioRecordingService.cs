using System.Buffers;
using System.Numerics;
using NAudio.Wave;
using TypeWhisper.Core.Audio;

namespace TypeWhisper.Windows.Services;

public sealed class AudioRecordingService : IDisposable
{
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    private const float AgcTargetRms = 0.1f;
    private const float AgcMaxGain = 20f;
    private const float AgcMinGain = 1f;
    private const float NormalizationTarget = 0.707f;

    /// <summary>
    /// Minimum per-chunk RMS level to consider as containing speech.
    /// Below this threshold, audio is treated as silence.
    /// </summary>
    public const float SpeechEnergyThreshold = 0.01f;

    private WaveInEvent? _waveIn;
    private WaveInEvent? _previewWaveIn;
    private float[]? _sampleBuffer;
    private int _sampleBufferCount;
    private readonly object _bufferLock = new();
    private bool _isRecording;
    private bool _isWarmedUp;
    private bool _isPreviewing;
    private bool _disposed;
    private DateTime _recordingStartTime;
    private int? _configuredDeviceNumber;
    private int _activeDeviceNumber;
    private float _peakRmsLevel;
    private float _preGainPeakRms;
    private float _currentRmsLevel;
    private System.Timers.Timer? _devicePollTimer;
    private int _lastKnownDeviceCount;

    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;
    public event EventHandler<AudioLevelEventArgs>? PreviewLevelChanged;
    public event EventHandler<SamplesAvailableEventArgs>? SamplesAvailable;
    public event EventHandler? DevicesChanged;
    public event EventHandler? DeviceLost;
    public event EventHandler? DeviceAvailable;

    public bool HasDevice => WaveInEvent.DeviceCount > 0;
    public bool WhisperModeEnabled { get; set; }
    public bool NormalizationEnabled { get; set; } = true;
    public bool IsRecording => _isRecording;
    public float PeakRmsLevel => _peakRmsLevel;
    public float CurrentRmsLevel => _currentRmsLevel;
    public bool HasSpeechEnergy => _preGainPeakRms >= SpeechEnergyThreshold;
    public TimeSpan RecordingDuration => _isRecording ? DateTime.UtcNow - _recordingStartTime : TimeSpan.Zero;

    public void SetMicrophoneDevice(int? deviceNumber)
    {
        var newDevice = deviceNumber ?? FindBestMicrophoneDevice();
        if (_isWarmedUp && newDevice != _activeDeviceNumber)
        {
            DisposeWaveIn();
            _isWarmedUp = false;
        }
        _configuredDeviceNumber = deviceNumber;
        _activeDeviceNumber = newDevice;
    }

    public bool WarmUp()
    {
        if (_isWarmedUp || _disposed) return _isWarmedUp;

        if (WaveInEvent.DeviceCount == 0)
        {
            System.Diagnostics.Debug.WriteLine("WarmUp: No audio input devices available.");
            StartDevicePolling();
            return false;
        }

        _activeDeviceNumber = _configuredDeviceNumber ?? FindBestMicrophoneDevice();
        if (_activeDeviceNumber < 0)
        {
            StartDevicePolling();
            return false;
        }

        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = _activeDeviceNumber,
                WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                BufferMilliseconds = 30
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();

            _isWarmedUp = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WarmUp failed: {ex.Message}");
            DisposeWaveIn();
        }

        StartDevicePolling();
        return _isWarmedUp;
    }

    public static IReadOnlyList<(int DeviceNumber, string Name)> GetAvailableDevices() =>
        Enumerable.Range(0, WaveInEvent.DeviceCount)
            .Select(i => (i, WaveInEvent.GetCapabilities(i).ProductName))
            .ToList();

    public void StartRecording()
    {
        if (_isRecording) return;

        // The settings microphone preview uses its own WaveIn instance and can
        // block real capture while the settings window stays open on Dictation.
        // Always stop preview before entering recording mode.
        if (_isPreviewing)
            StopPreview();

        if (!_isWarmedUp && !WarmUp())
            return;

        if (_waveIn is null) return;

        _sampleBuffer = new float[SampleRate * 60]; // Pre-alloc ~1 min
        _sampleBufferCount = 0;
        _peakRmsLevel = 0;
        _preGainPeakRms = 0;
        _recordingStartTime = DateTime.UtcNow;
        _isRecording = true;
    }

    public float[]? GetCurrentBuffer()
    {
        if (!_isRecording || _sampleBuffer is null) return null;
        lock (_bufferLock)
        {
            var snapshot = new float[_sampleBufferCount];
            Array.Copy(_sampleBuffer, snapshot, _sampleBufferCount);
            return snapshot;
        }
    }

    public float[]? StopRecording()
    {
        if (!_isRecording || _waveIn is null)
            return null;

        _isRecording = false;

        float[]? samples;
        lock (_bufferLock)
        {
            if (_sampleBuffer is null || _sampleBufferCount == 0)
            {
                samples = null;
            }
            else
            {
                samples = new float[_sampleBufferCount];
                Array.Copy(_sampleBuffer, samples, _sampleBufferCount);
            }

            _sampleBuffer = null;
            _sampleBufferCount = 0;
        }

        if (samples is null || samples.Length == 0)
            return null;

        if (NormalizationEnabled)
            NormalizeAudio(samples);

        return samples;
    }

    internal void ReleaseCapture()
    {
        _isRecording = false;
        lock (_bufferLock)
        {
            _sampleBuffer = null;
            _sampleBufferCount = 0;
        }

        DisposeWaveIn();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRecording) return;

        var sampleCount = e.BytesRecorded / 2;
        if (sampleCount == 0) return;

        // Decode PCM16 -> float once using the vectorized converter. We still need a second
        // per-sample pass for the AGC/peak/RMS work below, but this saves the per-sample
        // BitConverter overhead (previously executed twice).
        var floatBuffer = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            PcmSampleConverter.ConvertPcm16LeToFloat(
                e.Buffer.AsSpan(0, sampleCount * 2),
                floatBuffer.AsSpan(0, sampleCount));

            // Compute pre-gain RMS for speech energy detection (unaffected by AGC).
            ComputePeakAndSumSquares(floatBuffer.AsSpan(0, sampleCount), out _, out var preGainSum);
            var preGainRms = MathF.Sqrt(preGainSum / sampleCount);
            if (preGainRms > _preGainPeakRms) _preGainPeakRms = preGainRms;

            float agcGain = 1f;
            if (WhisperModeEnabled)
            {
                if (preGainRms > 0.0001f)
                    agcGain = Math.Clamp(AgcTargetRms / preGainRms, AgcMinGain, AgcMaxGain);
            }

            var shouldPublishSamples = SamplesAvailable is not null;
            var chunkBuffer = shouldPublishSamples ? new float[sampleCount] : null;
            var processedSamples = chunkBuffer is not null
                ? chunkBuffer.AsSpan()
                : floatBuffer.AsSpan(0, sampleCount);

            for (var i = 0; i < sampleCount; i++)
            {
                var sample = floatBuffer[i];
                if (WhisperModeEnabled)
                    sample = Math.Clamp(sample * agcGain, -1f, 1f);

                processedSamples[i] = sample;
            }

            lock (_bufferLock)
            {
                if (_sampleBuffer is not null)
                {
                    EnsureSampleBufferCapacity(sampleCount);
                    processedSamples.CopyTo(_sampleBuffer.AsSpan(_sampleBufferCount));
                    _sampleBufferCount += sampleCount;
                }
            }

            ComputePeakAndSumSquares(processedSamples, out var peak, out var sumSquares);

            var rms = MathF.Sqrt(sumSquares / sampleCount);
            _currentRmsLevel = rms;
            if (rms > _peakRmsLevel) _peakRmsLevel = rms;

            AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(peak, rms));

            if (chunkBuffer is not null && SamplesAvailable is not null && _sampleBuffer is not null)
            {
                SamplesAvailable.Invoke(this, new SamplesAvailableEventArgs(chunkBuffer));
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatBuffer);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) { }

    private static void NormalizeAudio(float[] samples)
    {
        ComputePeakAndSumSquares(samples, out var peakAmplitude, out _);
        if (peakAmplitude < 0.01f) return;

        var gain = NormalizationTarget / peakAmplitude;
        if (gain > 1.0f) ApplyGainAndClamp(samples, gain);
    }

    private static int FindBestMicrophoneDevice()
    {
        var deviceCount = WaveInEvent.DeviceCount;

        for (var i = 0; i < deviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            if (caps.ProductName.Contains("Microphone", StringComparison.OrdinalIgnoreCase) ||
                caps.ProductName.Contains("Mikrofon", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        for (var i = 0; i < deviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            if (!caps.ProductName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                !caps.ProductName.Contains("Mix", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return deviceCount > 0 ? 0 : -1;
    }

    private void StartDevicePolling()
    {
        _lastKnownDeviceCount = WaveInEvent.DeviceCount;
        _devicePollTimer?.Dispose();
        _devicePollTimer = new System.Timers.Timer(2000);
        _devicePollTimer.Elapsed += (_, _) => CheckForDeviceChanges();
        _devicePollTimer.AutoReset = true;
        _devicePollTimer.Start();
    }

    private void CheckForDeviceChanges()
    {
        try
        {
            var currentCount = WaveInEvent.DeviceCount;
            if (currentCount == _lastKnownDeviceCount) return;

            var previousCount = _lastKnownDeviceCount;
            _lastKnownDeviceCount = currentCount;
            DevicesChanged?.Invoke(this, EventArgs.Empty);

            if (currentCount == 0 && _isWarmedUp)
            {
                DeviceLost?.Invoke(this, EventArgs.Empty);
                DisposeWaveIn();
                _configuredDeviceNumber = null;
            }
            else if (currentCount > 0 && previousCount == 0)
            {
                DeviceAvailable?.Invoke(this, EventArgs.Empty);
                WarmUp();
            }
            else if (_isWarmedUp && _activeDeviceNumber >= currentCount)
            {
                DeviceLost?.Invoke(this, EventArgs.Empty);
                DisposeWaveIn();
                _configuredDeviceNumber = null;
                WarmUp();
            }
        }
        catch { }
    }

    public void StartPreview(int? deviceNumber)
    {
        if (_isRecording)
            return;

        StopPreview();
        if (_disposed || WaveInEvent.DeviceCount == 0) return;

        var deviceIndex = deviceNumber ?? FindBestMicrophoneDevice();
        if (deviceIndex < 0) return;

        try
        {
            _previewWaveIn = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                BufferMilliseconds = 50
            };
            _previewWaveIn.DataAvailable += OnPreviewDataAvailable;
            _previewWaveIn.StartRecording();
            _isPreviewing = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartPreview failed: {ex.Message}");
            StopPreview();
        }
    }

    public void StopPreview()
    {
        if (_previewWaveIn is not null)
        {
            _previewWaveIn.DataAvailable -= OnPreviewDataAvailable;
            try { _previewWaveIn.StopRecording(); } catch { }
            _previewWaveIn.Dispose();
            _previewWaveIn = null;
        }
        _isPreviewing = false;
    }

    public bool IsPreviewing => _isPreviewing;

    private void OnPreviewDataAvailable(object? sender, WaveInEventArgs e)
    {
        var sampleCount = e.BytesRecorded / 2;
        if (sampleCount == 0) return;

        var floatBuffer = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            PcmSampleConverter.ConvertPcm16LeToFloat(
                e.Buffer.AsSpan(0, sampleCount * 2),
                floatBuffer.AsSpan(0, sampleCount));

            ComputePeakAndSumSquares(floatBuffer.AsSpan(0, sampleCount), out var peak, out var sumSquares);

            var rms = MathF.Sqrt(sumSquares / sampleCount);
            PreviewLevelChanged?.Invoke(this, new AudioLevelEventArgs(peak, rms));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatBuffer);
        }
    }

    private void DisposeWaveIn()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            try { _waveIn.StopRecording(); } catch { }
            _waveIn.Dispose();
            _waveIn = null;
        }
        _isWarmedUp = false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _devicePollTimer?.Dispose();
            _isRecording = false;
            StopPreview();
            DisposeWaveIn();
            _disposed = true;
        }
    }

    private void EnsureSampleBufferCapacity(int additionalSamples)
    {
        if (_sampleBuffer is null)
            return;

        var requiredLength = _sampleBufferCount + additionalSamples;
        if (requiredLength <= _sampleBuffer.Length)
            return;

        var newLength = _sampleBuffer.Length;
        while (newLength < requiredLength)
            newLength = Math.Max(requiredLength, newLength * 2);

        Array.Resize(ref _sampleBuffer, newLength);
    }

    private static void ComputePeakAndSumSquares(ReadOnlySpan<float> samples, out float peak, out float sumSquares)
    {
        peak = 0;
        sumSquares = 0;
        var i = 0;

        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            var vectorPeak = Vector<float>.Zero;
            var vectorSumSquares = Vector<float>.Zero;
            var lastVectorStart = samples.Length - Vector<float>.Count;
            for (; i <= lastVectorStart; i += Vector<float>.Count)
            {
                var vector = new Vector<float>(samples.Slice(i, Vector<float>.Count));
                var abs = Vector.Abs(vector);
                vectorPeak = Vector.Max(vectorPeak, abs);
                vectorSumSquares += vector * vector;
            }

            for (var lane = 0; lane < Vector<float>.Count; lane++)
            {
                peak = MathF.Max(peak, vectorPeak[lane]);
                sumSquares += vectorSumSquares[lane];
            }
        }

        for (; i < samples.Length; i++)
        {
            var sample = samples[i];
            peak = MathF.Max(peak, MathF.Abs(sample));
            sumSquares += sample * sample;
        }
    }

    private static void ApplyGainAndClamp(Span<float> samples, float gain)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            var gainVector = new Vector<float>(gain);
            var minVector = new Vector<float>(-1f);
            var maxVector = new Vector<float>(1f);
            var lastVectorStart = samples.Length - Vector<float>.Count;
            for (; i <= lastVectorStart; i += Vector<float>.Count)
            {
                var scaled = new Vector<float>(samples.Slice(i, Vector<float>.Count)) * gainVector;
                Vector.Min(Vector.Max(scaled, minVector), maxVector)
                    .CopyTo(samples.Slice(i, Vector<float>.Count));
            }
        }

        for (; i < samples.Length; i++)
            samples[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
    }
}

public sealed record AudioLevelEventArgs(float PeakLevel, float RmsLevel);

public sealed class SamplesAvailableEventArgs(float[] samples) : EventArgs
{
    public float[] Samples { get; } = samples;
}
