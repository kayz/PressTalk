using PressTalk.Contracts.Asr;
using PressTalk.Contracts.Commit;
using PressTalk.Contracts.Pipeline;

namespace PressTalk.Engine;

public sealed class StreamingPipeline : IStreamingPipeline
{
    private readonly IStreamingAsrBackend _asrBackend;
    private readonly ITextCommitter _textCommitter;
    private readonly Action<string>? _log;

    public StreamingPipeline(
        IStreamingAsrBackend asrBackend,
        ITextCommitter textCommitter,
        Action<string>? log = null)
    {
        _asrBackend = asrBackend;
        _textCommitter = textCommitter;
        _log = log;
    }

    public async Task StartSessionAsync(
        string sessionId,
        string languageHint,
        IReadOnlyList<string> hotwords,
        bool enableSpeakerDiarization,
        CancellationToken cancellationToken)
    {
        _textCommitter.ResetIncrementalState();
        _log?.Invoke(
            $"[Engine.StreamingPipeline] start session={sessionId}, lang={languageHint}, hotwords={hotwords.Count}, speaker={enableSpeakerDiarization}");

        await _asrBackend.StartStreamingSessionAsync(
            sessionId,
            languageHint,
            hotwords,
            enableSpeakerDiarization,
            cancellationToken);
    }

    public async Task<StreamingAsrResult> PushChunkAsync(
        string sessionId,
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        var result = await _asrBackend.PushAudioChunkAsync(
            sessionId,
            audioSamples,
            sampleRate,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.ConfirmedText))
        {
            await _textCommitter.CommitIncrementalAsync(
                result.ConfirmedText,
                isFinal: result.IsFinal,
                cancellationToken);
        }

        return result;
    }

    public async Task<StreamingAsrResult> EndSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var result = await _asrBackend.EndStreamingSessionAsync(sessionId, cancellationToken);

        await _textCommitter.CommitIncrementalAsync(
            result.ConfirmedText,
            isFinal: true,
            cancellationToken);

        _log?.Invoke(
            $"[Engine.StreamingPipeline] end session={sessionId}, chars={result.ConfirmedText.Length}, speakers={result.SpeakerSegments.Count}");
        return result;
    }
}
