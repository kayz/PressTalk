using PressTalk.Contracts.Asr;
using PressTalk.Contracts.Commit;
using PressTalk.Contracts.Session;
using PressTalk.Engine;
using Xunit;

namespace PressTalk.Engine.Tests;

public sealed class StreamingPipelineTests
{
    [Fact]
    public void SessionStateMachine_StreamingPath_ReachesCompleted()
    {
        var machine = new SessionStateMachine();

        machine.Transition(SessionTrigger.StartStreaming);
        machine.Transition(SessionTrigger.StreamingChunk);
        machine.Transition(SessionTrigger.StopStreaming);
        machine.Transition(SessionTrigger.AsrComplete);
        machine.Transition(SessionTrigger.CommitComplete);

        Assert.Equal(SessionState.Completed, machine.CurrentState);
    }

    [Fact]
    public async Task StreamingPipeline_PushAndEnd_UsesIncrementalCommit()
    {
        var backend = new FakeStreamingBackend();
        var committer = new FakeTextCommitter();
        var pipeline = new StreamingPipeline(backend, committer);

        await pipeline.StartSessionAsync("s1", "auto", [], enableSpeakerDiarization: false, CancellationToken.None);
        _ = await pipeline.PushChunkAsync("s1", new float[] { 0.1f }, 16000, CancellationToken.None);
        _ = await pipeline.EndSessionAsync("s1", CancellationToken.None);

        Assert.Equal(2, committer.IncrementalCalls.Count);
        Assert.Equal("你", committer.IncrementalCalls[0].Text);
        Assert.False(committer.IncrementalCalls[0].IsFinal);
        Assert.Equal("你好", committer.IncrementalCalls[1].Text);
        Assert.True(committer.IncrementalCalls[1].IsFinal);
        Assert.Equal(1, backend.StartCount);
        Assert.Equal(1, backend.EndCount);
    }

    private sealed class FakeStreamingBackend : IStreamingAsrBackend
    {
        private int _pushCount;

        public int StartCount { get; private set; }
        public int EndCount { get; private set; }

        public string Name => "fake-streaming";

        public Task StartStreamingSessionAsync(
            string sessionId,
            string languageHint,
            IReadOnlyList<string> hotwords,
            bool enableSpeakerDiarization,
            CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task<StreamingAsrResult> PushAudioChunkAsync(
            string sessionId,
            ReadOnlyMemory<float> audioSamples,
            int sampleRate,
            CancellationToken cancellationToken)
        {
            _pushCount++;
            var text = _pushCount == 1 ? "你" : "你好";
            return Task.FromResult(
                new StreamingAsrResult(
                    SessionId: sessionId,
                    PreviewText: text,
                    ConfirmedText: text,
                    DeltaText: text,
                    IsFinal: false,
                    Duration: TimeSpan.FromMilliseconds(10),
                    SpeakerSegments: []));
        }

        public Task<StreamingAsrResult> EndStreamingSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            EndCount++;
            return Task.FromResult(
                new StreamingAsrResult(
                    SessionId: sessionId,
                    PreviewText: "你好",
                    ConfirmedText: "你好",
                    DeltaText: "好",
                    IsFinal: true,
                    Duration: TimeSpan.FromMilliseconds(10),
                    SpeakerSegments: []));
        }
    }

    private sealed class FakeTextCommitter : ITextCommitter
    {
        public List<(string Text, bool IsFinal)> IncrementalCalls { get; } = [];

        public Task CommitAsync(string text, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task CommitIncrementalAsync(string confirmedText, bool isFinal, CancellationToken cancellationToken)
        {
            IncrementalCalls.Add((confirmedText, isFinal));
            return Task.CompletedTask;
        }

        public void ResetIncrementalState()
        {
        }
    }
}
