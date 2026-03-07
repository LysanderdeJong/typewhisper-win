# WebSocket Streaming for Deepgram & AssemblyAI Plugins

## Context

TypeWhisper's current "live transcription" is polling-based: StreamingHandler polls the audio buffer every 3s and calls `TranscribeAsync` (batch). This adds significant latency for cloud providers.

Both Deepgram and AssemblyAI offer real-time WebSocket streaming APIs that deliver partial transcripts within hundreds of milliseconds.

## Design

### New PluginSDK Interface: IStreamingSession

```csharp
public interface IStreamingSession : IAsyncDisposable
{
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct);
    event Action<StreamingTranscriptEvent> TranscriptReceived;
    Task FinalizeAsync(CancellationToken ct);
}

public sealed record StreamingTranscriptEvent(string Text, bool IsFinal);
```

### ITranscriptionEnginePlugin Extension

New DIM (default interface method):
```csharp
Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    => throw new NotSupportedException();
```

`SupportsStreaming` (existing DIM, default false) gates usage.

### StreamingHandler Changes

Two paths based on `SupportsStreaming`:

**True (WebSocket path):**
1. Call `StartStreamingAsync()` to get `IStreamingSession`
2. Subscribe to `AudioRecordingService.SamplesAvailable`
3. Convert float[] samples to PCM16 bytes, send via `session.SendAudioAsync()`
4. On `TranscriptReceived`: apply dictionary corrections, invoke `OnPartialTextUpdate`
5. On stop: `FinalizeAsync()` then `DisposeAsync()`

**False (Polling fallback):**
- Existing 3s polling loop unchanged

### Deepgram Streaming Session

- URL: `wss://api.deepgram.com/v1/listen?model={model}&encoding=linear16&sample_rate=16000&interim_results=true&punctuate=true&smart_format=true`
- Language: `&language={lang}` or `&detect_language=true`
- Auth: Header `Authorization: Token {key}`
- Send: Binary frames (PCM16 bytes)
- Receive: JSON `{ type: "Results", channel: { alternatives: [{ transcript }] }, is_final, speech_final }`
- Close: Send `{"type":"CloseStream"}`

### AssemblyAI Streaming Session

- URL: `wss://streaming.assemblyai.com/v3/ws?sample_rate=16000`
- Auth: Header `Authorization: {key}`
- Send: Binary frames (PCM16 bytes)
- Receive: JSON `{ type: "Turn", transcript, end_of_turn }`
- Close: Send `{"terminate_session": true}`

## Files

| # | File | Type |
|---|------|------|
| 1 | `src/TypeWhisper.PluginSDK/IStreamingSession.cs` | NEW |
| 2 | `src/TypeWhisper.PluginSDK/ITranscriptionEnginePlugin.cs` | EDIT |
| 3 | `src/TypeWhisper.Windows/Services/StreamingHandler.cs` | EDIT |
| 4 | `plugins/TypeWhisper.Plugin.Deepgram/DeepgramStreamingSession.cs` | NEW |
| 5 | `plugins/TypeWhisper.Plugin.Deepgram/DeepgramPlugin.cs` | EDIT |
| 6 | `plugins/TypeWhisper.Plugin.AssemblyAi/AssemblyAiStreamingSession.cs` | NEW |
| 7 | `plugins/TypeWhisper.Plugin.AssemblyAi/AssemblyAiPlugin.cs` | EDIT |

## Verification

1. `dotnet build -p:EnableWindowsTargeting=true` (solution + both plugins)
2. `dotnet test -p:EnableWindowsTargeting=true`
3. Manual: start app, select Deepgram/AssemblyAI, enable live transcription, speak, verify immediate text
