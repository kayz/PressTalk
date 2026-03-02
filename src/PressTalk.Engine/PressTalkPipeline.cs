using PressTalk.Contracts.Asr;
using PressTalk.Contracts.Commit;
using PressTalk.Contracts.Normalize;
using PressTalk.Contracts.Pipeline;

namespace PressTalk.Engine;

public sealed class PressTalkPipeline : IPressTalkPipeline
{
    private readonly IAsrBackend _asrBackend;
    private readonly ITextNormalizer _textNormalizer;
    private readonly ITextCommitter _textCommitter;
    private readonly Action<string>? _log;

    public PressTalkPipeline(
        IAsrBackend asrBackend,
        ITextNormalizer textNormalizer,
        ITextCommitter textCommitter,
        Action<string>? log = null)
    {
        _asrBackend = asrBackend;
        _textNormalizer = textNormalizer;
        _textCommitter = textCommitter;
        _log = log;
    }

    public async Task<PressTalkResult> ProcessAsync(
        PressTalkRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _log?.Invoke($"[Engine.Pipeline] start session={request.SessionId}, samples={request.AudioSamples.Length}, sampleRate={request.SampleRate}, lang={request.LanguageHint}");

        var asr = await _asrBackend.TranscribeAsync(
            request.AudioSamples,
            request.SampleRate,
            cancellationToken);
        _log?.Invoke($"[Engine.Pipeline] asr done, textLen={asr.Text.Length}, durationMs={asr.Duration.TotalMilliseconds:F1}");
        var normalizationOptions = new TextNormalizationOptions(
            EnableSemantic: request.EnableSemanticEnhancement,
            Scenario: request.IsStickyDictationMode ? "sticky-dictation" : "default",
            PreserveStructuredItems: request.IsStickyDictationMode);
        _log?.Invoke(
            $"[Engine.Pipeline] normalize options, semantic={normalizationOptions.EnableSemantic}, scenario={normalizationOptions.Scenario}, preserveStructured={normalizationOptions.PreserveStructuredItems}");

        var normalizedText = await _textNormalizer.NormalizeAsync(
            asr.Text,
            request.LanguageHint,
            normalizationOptions,
            cancellationToken);
        _log?.Invoke($"[Engine.Pipeline] normalize done, textLen={normalizedText.Length}");

        _log?.Invoke($"[Engine.Pipeline] commit start");
        await _textCommitter.CommitAsync(normalizedText, cancellationToken);
        _log?.Invoke($"[Engine.Pipeline] commit done");

        return new PressTalkResult(
            request.SessionId,
            asr.Text,
            normalizedText,
            DateTimeOffset.UtcNow - startedAt);
    }
}
