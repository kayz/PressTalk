using PressTalk.Contracts.Asr;
using PressTalk.Contracts.Commit;
using PressTalk.Contracts.Pipeline;

namespace PressTalk.Engine;

public sealed class StreamingPipeline : IStreamingPipeline
{
    private sealed class SessionCommitState
    {
        public int LastCommittedLength { get; set; }
    }

    private readonly IStreamingAsrBackend _asrBackend;
    private readonly ITextCommitter _textCommitter;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, SessionCommitState> _sessions = new();
    private readonly object _sync = new();
    private readonly int _liveCommitMinChars;

    public StreamingPipeline(
        IStreamingAsrBackend asrBackend,
        ITextCommitter textCommitter,
        Action<string>? log = null)
    {
        _asrBackend = asrBackend;
        _textCommitter = textCommitter;
        _log = log;
        _liveCommitMinChars = ResolveLiveCommitMinChars();
    }

    public async Task StartSessionAsync(
        string sessionId,
        string languageHint,
        IReadOnlyList<string> hotwords,
        bool enableSpeakerDiarization,
        CancellationToken cancellationToken)
    {
        _textCommitter.ResetIncrementalState();
        lock (_sync)
        {
            _sessions[sessionId] = new SessionCommitState();
        }

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

        if (ShouldCommitIncremental(sessionId, result, out var textToCommit))
        {
            await _textCommitter.CommitIncrementalAsync(
                textToCommit,
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

        lock (_sync)
        {
            _sessions.Remove(sessionId);
        }

        _log?.Invoke(
            $"[Engine.StreamingPipeline] end session={sessionId}, chars={result.ConfirmedText.Length}, speakers={result.SpeakerSegments.Count}");
        return result;
    }

    private bool ShouldCommitIncremental(
        string sessionId,
        StreamingAsrResult result,
        out string textToCommit)
    {
        textToCommit = result.ConfirmedText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(textToCommit))
        {
            return false;
        }

        SessionCommitState state;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out state!))
            {
                state = new SessionCommitState();
                _sessions[sessionId] = state;
            }
        }

        var committedLen = state.LastCommittedLength;
        if (committedLen > textToCommit.Length)
        {
            committedLen = 0;
        }

        var newChars = textToCommit.Length - committedLen;
        if (newChars <= 0)
        {
            return false;
        }

        if (!result.IsFinal && committedLen > 0)
        {
            var tail = textToCommit[committedLen..];
            var hasBoundary = tail.IndexOfAny(['，', '。', '！', '？', ',', '.', '!', '?', '\n']) >= 0;
            if (newChars < _liveCommitMinChars && !hasBoundary)
            {
                return false;
            }
        }

        state.LastCommittedLength = textToCommit.Length;
        return true;
    }

    private static int ResolveLiveCommitMinChars()
    {
        var raw = Environment.GetEnvironmentVariable("PRESSTALK_LIVE_COMMIT_MIN_CHARS");
        if (int.TryParse(raw, out var parsed))
        {
            return Math.Clamp(parsed, 2, 40);
        }

        return 8;
    }
}
