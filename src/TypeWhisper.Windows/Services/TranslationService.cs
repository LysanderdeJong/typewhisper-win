using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Net.Http;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Translation;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.Services;

public sealed class TranslationService : ITranslationService, IDisposable
{
    private readonly PluginManager _pluginManager;
    private readonly HttpClient _httpClient = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1);
    private readonly Dictionary<string, LoadedTranslationModel> _loadedModels = new();
    private readonly HashSet<string> _loadingModels = new();
    private bool _disposed;

    private const string TranslationSystemPrompt =
        "You are a professional translator. Translate the given text accurately and naturally. " +
        "Output ONLY the translation, nothing else. Do not add explanations, notes, or formatting.";

    public TranslationService(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
    }

    public bool IsModelReady(string sourceLang, string targetLang)
    {
        // Cloud translation is always ready when a provider is configured
        if (GetConfiguredTranslationProvider() is not null)
            return true;
        return _loadedModels.ContainsKey(ModelKey(sourceLang, targetLang));
    }

    public bool IsModelLoading(string sourceLang, string targetLang) =>
        _loadingModels.Contains(ModelKey(sourceLang, targetLang));

    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (sourceLang == targetLang) return text;

        // Prefer cloud LLM translation when available (faster, supports all language pairs)
        var llmProvider = GetConfiguredTranslationProvider();
        if (llmProvider is not null)
        {
            var model = llmProvider.SupportedModels.First().Id;
            var userText = $"Translate from {sourceLang} to {targetLang}:\n\n{text}";
            return await llmProvider.ProcessAsync(TranslationSystemPrompt, userText, model, ct);
        }

        // Fallback: local ONNX Marian models
        return await TranslateLocalAsync(text, sourceLang, targetLang, ct);
    }

    private ILlmProviderPlugin? GetConfiguredTranslationProvider() =>
        _pluginManager.LlmProviders.FirstOrDefault(p => p.IsAvailable);

    private async Task<string> TranslateLocalAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        // Direct model available?
        var directModel = TranslationModelInfo.FindModel(sourceLang, targetLang);
        if (directModel is not null)
        {
            var model = await GetOrLoadModelAsync(sourceLang, targetLang, ct);
            return await Task.Run(() => RunInference(model, text), ct);
        }

        // Chain through English: source->en + en->target
        if (sourceLang != "en" && targetLang != "en")
        {
            var toEn = TranslationModelInfo.FindModel(sourceLang, "en");
            var fromEn = TranslationModelInfo.FindModel("en", targetLang);
            if (toEn is not null && fromEn is not null)
            {
                var model1 = await GetOrLoadModelAsync(sourceLang, "en", ct);
                var english = await Task.Run(() => RunInference(model1, text), ct);

                var model2 = await GetOrLoadModelAsync("en", targetLang, ct);
                return await Task.Run(() => RunInference(model2, english), ct);
            }
        }

        throw new NotSupportedException(Loc.Instance.GetString("Error.TranslationNotAvailableFormat", sourceLang, targetLang));
    }

    private async Task<LoadedTranslationModel> GetOrLoadModelAsync(string sourceLang, string targetLang, CancellationToken ct) =>
        _loadedModels.TryGetValue(ModelKey(sourceLang, targetLang), out var model)
            ? model
            : await EnsureModelLoadedAsync(sourceLang, targetLang, ct);

    private async Task<LoadedTranslationModel> EnsureModelLoadedAsync(string sourceLang, string targetLang, CancellationToken ct)
    {
        var key = ModelKey(sourceLang, targetLang);

        await _downloadSemaphore.WaitAsync(ct);
        try
        {
            if (_loadedModels.TryGetValue(key, out var existing))
                return existing;

            _loadingModels.Add(key);

            var modelInfo = TranslationModelInfo.FindModel(sourceLang, targetLang)
                ?? throw new NotSupportedException($"No translation model for {sourceLang}->{targetLang}");

            var modelDir = Path.Combine(TypeWhisperEnvironment.ModelsPath, modelInfo.SubDirectory);
            Directory.CreateDirectory(modelDir);

            await DownloadMissingFilesAsync(modelInfo, modelDir, ct);

            var loaded = LoadModel(modelDir);

            _loadedModels[key] = loaded;
            _loadingModels.Remove(key);

            return loaded;
        }
        catch
        {
            _loadingModels.Remove(key);
            throw;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private async Task DownloadMissingFilesAsync(TranslationModelInfo modelInfo, string modelDir, CancellationToken ct)
    {
        foreach (var file in modelInfo.Files)
        {
            var filePath = Path.Combine(modelDir, file.FileName);
            if (File.Exists(filePath)) continue;

            System.Diagnostics.Debug.WriteLine($"Downloading translation model file: {file.FileName}");

            using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var tmpPath = filePath + ".tmp";
            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await contentStream.CopyToAsync(fileStream, ct);
            }

            File.Move(tmpPath, filePath, overwrite: true);
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Encoder/decoder ownership is transferred to LoadedTranslationModel on success; "
            + "the catch block disposes partially-constructed sessions along the failure path.")]
    private static LoadedTranslationModel LoadModel(string modelDir)
    {
        var config = MarianConfig.Load(Path.Combine(modelDir, "config.json"));
        var tokenizer = MarianTokenizer.Load(Path.Combine(modelDir, "tokenizer.json"), config.EosTokenId);

        // SessionOptions is only needed during session construction; InferenceSession
        // captures the settings internally, so disposing it afterwards is safe.
        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Environment.ProcessorCount
        };

        InferenceSession? encoder = null;
        InferenceSession? decoder = null;
        try
        {
            encoder = new InferenceSession(Path.Combine(modelDir, "encoder_model_quantized.onnx"), sessionOptions);
            decoder = new InferenceSession(Path.Combine(modelDir, "decoder_model_quantized.onnx"), sessionOptions);

            var model = new LoadedTranslationModel(encoder, decoder, tokenizer, config);
            // Ownership transferred to LoadedTranslationModel.
            encoder = null;
            decoder = null;
            return model;
        }
        catch
        {
            decoder?.Dispose();
            encoder?.Dispose();
            throw;
        }
    }

    private static string RunInference(LoadedTranslationModel model, string text)
    {
        var inputIds = model.Tokenizer.Encode(text);
        var seqLen = inputIds.Length;

        var inputIdsBuffer = new long[seqLen];
        var attentionMaskBuffer = new long[seqLen];
        for (var i = 0; i < seqLen; i++) inputIdsBuffer[i] = inputIds[i];
        Array.Fill(attentionMaskBuffer, 1L);

        var encoderDimensions = new[] { 1, seqLen };
        var inputIdsTensor = new DenseTensor<long>(inputIdsBuffer.AsMemory(), encoderDimensions);
        var attentionMask = new DenseTensor<long>(attentionMaskBuffer.AsMemory(), encoderDimensions);

        using var encoderResults = model.Encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
        ]);

        var encoderHidden = encoderResults.First().Value as DenseTensor<float>
            ?? throw new InvalidOperationException("Encoder output is not a float tensor");

        var maxTokens = Math.Min(model.Config.MaxLength, 200);
        var decodedIds = new int[maxTokens + 1];
        var decoderInputIdsBuffer = new long[maxTokens + 1];
        var decoderDimensions = new[] { 1, 1 };
        decodedIds[0] = model.Config.DecoderStartTokenId;
        decoderInputIdsBuffer[0] = model.Config.DecoderStartTokenId;
        var decodedCount = 1;
        var encoderAttentionMaskInput = NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attentionMask);
        var encoderHiddenInput = NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHidden);
        var decoderInputs = new NamedOnnxValue[3];
        decoderInputs[1] = encoderAttentionMaskInput;
        decoderInputs[2] = encoderHiddenInput;

        for (var step = 0; step < maxTokens; step++)
        {
            var decoderLen = decodedCount;
            decoderDimensions[1] = decoderLen;
            var decoderInputIds = new DenseTensor<long>(decoderInputIdsBuffer.AsMemory(0, decoderLen), decoderDimensions);
            decoderInputs[0] = NamedOnnxValue.CreateFromTensor("input_ids", decoderInputIds);

            using var decoderResults = model.Decoder.Run(decoderInputs);
            var logits = decoderResults.First().Value as DenseTensor<float>
                ?? throw new InvalidOperationException("Decoder output is not a float tensor");

            var vocabSize = logits.Dimensions[2];
            var lastTokenOffset = (decoderLen - 1) * vocabSize;
            var bestId = FindBestLogitIndex(logits.Buffer.Span.Slice(lastTokenOffset, vocabSize));

            if (bestId == model.Config.EosTokenId) break;
            decodedIds[decodedCount] = bestId;
            decoderInputIdsBuffer[decodedCount] = bestId;
            decodedCount++;
        }

        return model.Tokenizer.Decode(decodedIds.AsSpan(1, decodedCount - 1));
    }

    private static int FindBestLogitIndex(ReadOnlySpan<float> logits)
    {
        var maxValue = float.NegativeInfinity;
        var i = 0;

        if (Vector.IsHardwareAccelerated && logits.Length >= Vector<float>.Count)
        {
            var negativeInfinity = new Vector<float>(float.NegativeInfinity);
            var vectorMax = negativeInfinity;
            var lastVectorStart = logits.Length - Vector<float>.Count;
            for (; i <= lastVectorStart; i += Vector<float>.Count)
            {
                var vector = new Vector<float>(logits.Slice(i, Vector<float>.Count));
                var validMask = Vector.Equals(vector, vector);
                var sanitized = Vector.ConditionalSelect(validMask, vector, negativeInfinity);
                vectorMax = Vector.Max(vectorMax, sanitized);
            }

            for (var lane = 0; lane < Vector<float>.Count; lane++)
                maxValue = MathF.Max(maxValue, vectorMax[lane]);
        }

        for (; i < logits.Length; i++)
        {
            var value = logits[i];
            if (!float.IsNaN(value) && value > maxValue)
                maxValue = value;
        }

        for (var index = 0; index < logits.Length; index++)
        {
            if (logits[index] == maxValue)
                return index;
        }

        return 0;
    }

    private static string ModelKey(string sourceLang, string targetLang) =>
        $"{sourceLang}-{targetLang}";

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _downloadSemaphore.Dispose();
            foreach (var model in _loadedModels.Values)
            {
                model.Encoder.Dispose();
                model.Decoder.Dispose();
            }
            _loadedModels.Clear();
            _disposed = true;
        }
    }
}

internal sealed record LoadedTranslationModel(
    InferenceSession Encoder,
    InferenceSession Decoder,
    MarianTokenizer Tokenizer,
    MarianConfig Config);
