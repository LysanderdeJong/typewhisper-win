using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

return await ProgramMain(args);

static async Task<int> ProgramMain(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Usage: generate-wav <output-path> <seconds> | measure <target-dll> <scenario> <input-path> <iterations> <chunk-seconds>");
        return 1;
    }

    try
    {
        switch (args[0])
        {
            case "generate-wav" when args.Length == 3:
                WaveFileHelper.Generate(args[1], int.Parse(args[2], CultureInfo.InvariantCulture));
                return 0;

            case "measure" when args.Length == 6:
                var measurement = await MeasureAsync(
                    args[1],
                    args[2],
                    args[3],
                    int.Parse(args[4], CultureInfo.InvariantCulture),
                    int.Parse(args[5], CultureInfo.InvariantCulture));
                Console.WriteLine(JsonSerializer.Serialize(measurement));
                return 0;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return 1;
    }

    Console.Error.WriteLine("Invalid arguments.");
    return 1;
}

static async Task<MeasurementResult> MeasureAsync(string targetDll, string scenario, string inputPath, int iterations, int chunkSeconds)
{
    targetDll = Path.GetFullPath(targetDll);
    inputPath = Path.GetFullPath(inputPath);

    if (!File.Exists(targetDll))
        throw new FileNotFoundException("Target DLL not found.", targetDll);

    using var target = new TargetAudioApi(targetDll);
    var chunkFrames = chunkSeconds * 16000;
    var inputSeconds = WaveFileHelper.GetDurationSeconds(inputPath);

    Func<Task<RunMetrics>> run = scenario switch
    {
        "load-process" => () => target.LoadAndProcessAsync(inputPath, chunkFrames),
        "stream-process" => target.CanStream
            ? () => target.StreamAndProcessAsync(inputPath, chunkFrames)
            : () => Task.FromResult(RunMetrics.Unsupported()),
        _ => throw new ArgumentException($"Unknown scenario: {scenario}", nameof(scenario))
    };

    _ = await run();

    var runs = new List<RunMetrics>(iterations);
    for (var i = 0; i < iterations; i++)
        runs.Add(await RunMeasuredAsync(run));

    var okRuns = runs.Where(static run => run.Status == "ok").ToList();
    if (okRuns.Count == 0)
    {
        return new MeasurementResult(
            Scenario: scenario,
            Status: runs.Select(run => run.Status).FirstOrDefault() ?? "unsupported",
            Iterations: iterations,
            InputSeconds: inputSeconds,
            ChunkSeconds: chunkSeconds,
            MeanMs: 0,
            MinMs: 0,
            MaxMs: 0,
            MeanAllocatedMb: 0,
            MeanPeakPrivateDeltaMb: 0,
            MeanLiveManagedDeltaMb: 0,
            TotalSamples: 0,
            MeanChecksum: 0);
    }

    return new MeasurementResult(
        Scenario: scenario,
        Status: okRuns.Count == runs.Count ? "ok" : okRuns[0].Status,
        Iterations: okRuns.Count,
        InputSeconds: inputSeconds,
        ChunkSeconds: chunkSeconds,
        MeanMs: okRuns.Average(run => run.ElapsedMs),
        MinMs: okRuns.Min(run => run.ElapsedMs),
        MaxMs: okRuns.Max(run => run.ElapsedMs),
        MeanAllocatedMb: okRuns.Average(run => run.AllocatedBytes) / 1024d / 1024d,
        MeanPeakPrivateDeltaMb: okRuns.Average(run => run.PeakPrivateDeltaBytes) / 1024d / 1024d,
        MeanLiveManagedDeltaMb: okRuns.Average(run => run.LiveManagedDeltaBytes) / 1024d / 1024d,
        TotalSamples: okRuns[0].TotalSamples,
        MeanChecksum: okRuns.Average(run => run.Checksum));
}

static async Task<RunMetrics> RunMeasuredAsync(Func<Task<RunMetrics>> action)
{
    ForceGc();

    using var process = Process.GetCurrentProcess();
    process.Refresh();

    var baselinePrivateBytes = process.PrivateMemorySize64;
    var baselineManagedBytes = GC.GetTotalMemory(true);
    var allocatedBytesBefore = GC.GetTotalAllocatedBytes(true);
    var peakPrivateBytes = baselinePrivateBytes;

    using var pollCts = new CancellationTokenSource();
    var sampler = Task.Run(async () =>
    {
        while (!pollCts.Token.IsCancellationRequested)
        {
            process.Refresh();
            peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);
            await Task.Delay(10, pollCts.Token);
        }
    });

    var stopwatch = Stopwatch.StartNew();
    RunMetrics metrics;
    try
    {
        metrics = await action();
    }
    finally
    {
        stopwatch.Stop();
        pollCts.Cancel();
        try { await sampler; } catch (OperationCanceledException) { }
    }

    return metrics with
    {
        ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
        AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedBytesBefore,
        PeakPrivateDeltaBytes = Math.Max(0, peakPrivateBytes - baselinePrivateBytes),
        LiveManagedDeltaBytes = Math.Max(0, GC.GetTotalMemory(false) - baselineManagedBytes)
    };
}

static void ForceGc()
{
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, true, true);
}

sealed record MeasurementResult(
    string Scenario,
    string Status,
    int Iterations,
    double InputSeconds,
    int ChunkSeconds,
    double MeanMs,
    double MinMs,
    double MaxMs,
    double MeanAllocatedMb,
    double MeanPeakPrivateDeltaMb,
    double MeanLiveManagedDeltaMb,
    long TotalSamples,
    double MeanChecksum);

sealed record RunMetrics(
    [property: JsonIgnore] string Status,
    [property: JsonIgnore] double ElapsedMs,
    [property: JsonIgnore] long AllocatedBytes,
    [property: JsonIgnore] long PeakPrivateDeltaBytes,
    [property: JsonIgnore] long LiveManagedDeltaBytes,
    long TotalSamples,
    double Checksum)
{
    public static RunMetrics Unsupported() => new("unsupported", 0, 0, 0, 0, 0, 0);
}

sealed class TargetAudioApi : IDisposable
{
    private readonly AssemblyLoadContext _loadContext;
    private readonly Assembly _assembly;
    private readonly object _audioFileService;
    private readonly MethodInfo _loadAudioAsync;
    private readonly MethodInfo? _streamAudioChunksAsync;

    public TargetAudioApi(string targetDll)
    {
        _loadContext = new IsolatedLoadContext(targetDll);
        _assembly = _loadContext.LoadFromAssemblyPath(targetDll);

        var audioFileServiceType = _assembly.GetType("TypeWhisper.Windows.Services.AudioFileService")
            ?? throw new InvalidOperationException("AudioFileService type not found.");

        _audioFileService = Activator.CreateInstance(audioFileServiceType)
            ?? throw new InvalidOperationException("Failed to create AudioFileService.");

        _loadAudioAsync = audioFileServiceType.GetMethod("LoadAudioAsync", [typeof(string), typeof(CancellationToken)])
            ?? throw new InvalidOperationException("LoadAudioAsync(string, CancellationToken) not found.");

        _streamAudioChunksAsync = audioFileServiceType.GetMethod("StreamAudioChunksAsync", [typeof(string), typeof(int), typeof(CancellationToken)]);
    }

    public bool CanStream => _streamAudioChunksAsync is not null;

    public async Task<RunMetrics> LoadAndProcessAsync(string inputPath, int chunkFrames)
    {
        var task = (Task)_loadAudioAsync.Invoke(_audioFileService, [inputPath, CancellationToken.None])!;
        await task;
        var samples = (float[])task.GetType().GetProperty("Result")!.GetValue(task)!;

        try
        {
            return ProcessSamples(samples, chunkFrames);
        }
        finally
        {
            Array.Clear(samples, 0, samples.Length);
        }
    }

    public async Task<RunMetrics> StreamAndProcessAsync(string inputPath, int chunkFrames)
    {
        if (_streamAudioChunksAsync is null)
            return RunMetrics.Unsupported();

        var asyncEnumerable = _streamAudioChunksAsync.Invoke(_audioFileService, [inputPath, chunkFrames, CancellationToken.None])!;
        var asyncEnumerableInterface = asyncEnumerable.GetType().GetInterfaces()
            .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            ?? throw new InvalidOperationException("IAsyncEnumerable<T> interface not found.");
        var getAsyncEnumerator = asyncEnumerableInterface.GetMethod("GetAsyncEnumerator")
            ?? throw new InvalidOperationException("GetAsyncEnumerator not found.");
        var enumerator = getAsyncEnumerator.Invoke(asyncEnumerable, [CancellationToken.None])
            ?? throw new InvalidOperationException("Failed to create async enumerator.");

        var asyncEnumeratorInterface = enumerator.GetType().GetInterfaces()
            .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerator<>))
            ?? throw new InvalidOperationException("IAsyncEnumerator<T> interface not found.");
        var moveNextAsync = asyncEnumeratorInterface.GetMethod("MoveNextAsync")
            ?? throw new InvalidOperationException("MoveNextAsync not found.");
        var currentProperty = asyncEnumeratorInterface.GetProperty("Current")
            ?? throw new InvalidOperationException("Current property not found.");

        long totalSamples = 0;
        double checksum = 0;

        try
        {
            while (await (ValueTask<bool>)moveNextAsync.Invoke(enumerator, null)!)
            {
                var current = currentProperty.GetValue(enumerator)
                    ?? throw new InvalidOperationException("Enumerator returned null current item.");
                var samples = (float[])current.GetType().GetProperty("Samples")!.GetValue(current)!;
                var processed = ProcessSamples(samples, chunkFrames);
                totalSamples += processed.TotalSamples;
                checksum += processed.Checksum;
                current.GetType().GetMethod("ReleaseSamples")?.Invoke(current, null);
            }
        }
        finally
        {
            switch (enumerator)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        return new RunMetrics("ok", 0, 0, 0, 0, totalSamples, checksum);
    }

    private static RunMetrics ProcessSamples(float[] samples, int chunkFrames)
    {
        long totalSamples = 0;
        double checksum = 0;

        for (var offset = 0; offset < samples.Length; offset += chunkFrames)
        {
            var length = Math.Min(chunkFrames, samples.Length - offset);
            totalSamples += length;

            for (var i = offset; i < offset + length; i += 4096)
                checksum += samples[i];

            if (length > 0)
                checksum += samples[offset + length - 1];
        }

        return new RunMetrics("ok", 0, 0, 0, 0, totalSamples, checksum);
    }

    public void Dispose()
    {
        if (_audioFileService is IDisposable disposable)
            disposable.Dispose();
        _loadContext.Unload();
    }
}

static class WaveFileHelper
{
    public static void Generate(string outputPath, int seconds)
    {
        const int sampleRate = 16000;
        const short bitsPerSample = 16;
        const short channels = 1;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var sampleCount = checked(sampleRate * seconds);
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var dataSize = sampleCount * blockAlign;

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleRate;
            var value = Math.Sin(2 * Math.PI * 220 * t) * 0.4 + Math.Sin(2 * Math.PI * 440 * t) * 0.15;
            writer.Write((short)Math.Round(Math.Clamp(value, -1, 1) * short.MaxValue));
        }
    }

    public static double GetDurationSeconds(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        stream.Position = 22;
        var channels = reader.ReadInt16();
        var sampleRate = reader.ReadInt32();
        stream.Position = 34;
        var bitsPerSample = reader.ReadInt16();
        stream.Position = 40;
        var dataSize = reader.ReadInt32();
        var bytesPerSecond = sampleRate * channels * (bitsPerSample / 8d);
        return dataSize / bytesPerSecond;
    }
}

sealed class IsolatedLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public IsolatedLoadContext(string mainAssemblyPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is not null ? LoadFromAssemblyPath(assemblyPath) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is not null ? LoadUnmanagedDllFromPath(libraryPath) : nint.Zero;
    }
}
