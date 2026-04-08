using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using TypeWhisper.Core.Audio;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Monitors a folder for new audio files and triggers automatic transcription.
/// Supports WAV, MP3, FLAC, M4A, OGG files.
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".flac", ".m4a", ".ogg", ".wma", ".webm"
    };

    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _processedFiles = [];
    private readonly ConcurrentQueue<string> _pendingFiles = [];
    private CancellationTokenSource? _cts;
    private Task? _processingTask;

    private Func<string, CancellationToken, Task<string?>>? _transcribeHandler;

    public string? WatchPath { get; private set; }
    public bool IsRunning => _watcher is not null;
    public event Action<string, string>? FileTranscribed; // filePath, transcribedText
    public event Action<string, string>? FileError;       // filePath, errorMessage

    /// <summary>
    /// Starts watching the given folder. The transcribeHandler receives the file path
    /// and returns the transcribed text (or null on failure).
    /// </summary>
    public void Start(string folderPath, Func<string, CancellationToken, Task<string?>> transcribeHandler)
    {
        Stop();

        if (!Directory.Exists(folderPath))
            Directory.CreateDirectory(folderPath);

        WatchPath = folderPath;
        _transcribeHandler = transcribeHandler;
        _cts = new CancellationTokenSource();

        _watcher = new FileSystemWatcher(folderPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileCreated;
        _watcher.Renamed += (_, e) => OnFileCreated(null, new FileSystemEventArgs(WatcherChangeTypes.Created, folderPath, e.Name));

        _processingTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        WatchPath = null;
    }

    private void OnFileCreated(object? sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.FullPath);
        if (!AudioExtensions.Contains(ext)) return;

        // Debounce: skip if we've seen this file very recently
        if (_processedFiles.TryGetValue(e.FullPath, out var lastSeen) &&
            DateTime.UtcNow - lastSeen < TimeSpan.FromSeconds(5))
            return;

        _pendingFiles.Enqueue(e.FullPath);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_pendingFiles.TryDequeue(out var filePath))
            {
                // Wait for file to be fully written
                await WaitForFileReady(filePath, ct);

                _processedFiles[filePath] = DateTime.UtcNow;

                try
                {
                    var text = await _transcribeHandler!(filePath, ct);
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Write transcript as sidecar .txt file
                        var txtPath = Path.ChangeExtension(filePath, ".txt");
                        await File.WriteAllTextAsync(txtPath, text, ct);
                        FileTranscribed?.Invoke(filePath, text);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"WatchFolder transcription failed: {ex.Message}");
                    FileError?.Invoke(filePath, ex.Message);
                }
            }
            else
            {
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static async Task WaitForFileReady(string path, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(250, ct);
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
