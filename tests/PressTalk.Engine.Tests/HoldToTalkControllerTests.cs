using PressTalk.Contracts.Audio;
using PressTalk.Contracts.Pipeline;
using PressTalk.Contracts.Session;
using PressTalk.Engine;
using Xunit;

namespace PressTalk.Engine.Tests;

public sealed class HoldToTalkControllerTests
{
    [Fact]
    public async Task OnPressThenRelease_CompletesSessionAndResetsState()
    {
        var audio = new FakeAudioCaptureService();
        var pipeline = new FakePipeline();
        var controller = new HoldToTalkController(audio, pipeline);

        var pressedSessionId = await controller.OnPressAsync("zh", CancellationToken.None);
        var result = await controller.OnReleaseAsync(CancellationToken.None);

        Assert.Equal(pressedSessionId, result.SessionId);
        Assert.Equal(SessionState.Idle, controller.CurrentState);
        Assert.Null(controller.CurrentSessionId);
        Assert.Equal(1, pipeline.ProcessCount);
    }

    [Fact]
    public async Task OnReleaseWithoutPress_Throws()
    {
        var controller = new HoldToTalkController(new FakeAudioCaptureService(), new FakePipeline());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.OnReleaseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OnRelease_WhenPipelineFails_ResetsState()
    {
        var controller = new HoldToTalkController(
            new FakeAudioCaptureService(),
            new FailingPipeline());

        await controller.OnPressAsync("en", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.OnReleaseAsync(CancellationToken.None));

        Assert.Equal(SessionState.Idle, controller.CurrentState);
        Assert.Null(controller.CurrentSessionId);
    }

    private sealed class FakeAudioCaptureService : IAudioCaptureService
    {
        private string? _activeSessionId;

        public Task StartCaptureAsync(string sessionId, CancellationToken cancellationToken)
        {
            _activeSessionId = sessionId;
            return Task.CompletedTask;
        }

        public Task<AudioCaptureResult> StopCaptureAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (_activeSessionId != sessionId)
            {
                throw new InvalidOperationException("Session mismatch.");
            }

            _activeSessionId = null;

            return Task.FromResult(new AudioCaptureResult(
                AudioSamples: new float[] { 0.1f, 0.2f },
                SampleRate: 16000,
                Duration: TimeSpan.FromMilliseconds(100)));
        }
    }

    private sealed class FakePipeline : IPressTalkPipeline
    {
        public int ProcessCount { get; private set; }

        public Task<PressTalkResult> ProcessAsync(PressTalkRequest request, CancellationToken cancellationToken)
        {
            ProcessCount++;

            return Task.FromResult(new PressTalkResult(
                SessionId: request.SessionId,
                RawText: "raw",
                NormalizedText: "raw",
                Duration: TimeSpan.FromMilliseconds(10)));
        }
    }

    private sealed class FailingPipeline : IPressTalkPipeline
    {
        public Task<PressTalkResult> ProcessAsync(PressTalkRequest request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Pipeline failed.");
        }
    }
}
