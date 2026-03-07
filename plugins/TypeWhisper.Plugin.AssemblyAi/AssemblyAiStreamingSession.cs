using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.AssemblyAi;

internal sealed class AssemblyAiStreamingSession : IStreamingSession
{
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _receiveCts = new();
    private Task? _receiveTask;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<AssemblyAiStreamingSession> ConnectAsync(
        string apiKey, string? language, CancellationToken ct)
    {
        var session = new AssemblyAiStreamingSession();

        var url = "wss://streaming.assemblyai.com/v3/ws?sample_rate=16000";
        if (!string.IsNullOrEmpty(language))
            url += $"&language={language}";

        session._ws.Options.SetRequestHeader("Authorization", apiKey);
        await session._ws.ConnectAsync(new Uri(url), ct);
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return;
        await _ws.SendAsync(pcm16Audio, WebSocketMessageType.Binary, true, ct);
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return;
        var msg = Encoding.UTF8.GetBytes("""{"terminate_session":true}""");
        await _ws.SendAsync(msg, WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;

                var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                ParseAndEmit(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private void ParseAndEmit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "Turn")
                return;

            var transcript = root.TryGetProperty("transcript", out var textEl)
                ? textEl.GetString() ?? ""
                : "";

            if (string.IsNullOrWhiteSpace(transcript)) return;

            var isFinal = root.TryGetProperty("end_of_turn", out var eotEl) && eotEl.GetBoolean();

            TranscriptReceived?.Invoke(new StreamingTranscriptEvent(transcript, isFinal));
        }
        catch { /* malformed message, skip */ }
    }

    public async ValueTask DisposeAsync()
    {
        _receiveCts.Cancel();

        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { /* best effort */ }
        }

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch { /* expected */ }
        }

        _receiveCts.Dispose();
        _ws.Dispose();
    }
}
