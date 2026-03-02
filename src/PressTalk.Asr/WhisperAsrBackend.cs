using System.Text;
using PressTalk.Contracts.Asr;
using Whisper.net;
using Whisper.net.Ggml;

namespace PressTalk.Asr;

public sealed class WhisperAsrBackend : IAsrBackend, IAsyncDisposable
{
    private const int TargetSampleRate = 16000;

    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _transcribeLock = new(1, 1);

    private WhisperFactory? _factory;
    private string? _modelPath;

    public WhisperAsrBackend(Action<string>? log = null)
    {
        _log = log;
    }

    public string Name => "whisper-base";

    public async Task<AsrResult> TranscribeAsync(
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var startedAt = DateTimeOffset.UtcNow;
        var input = audioSamples.ToArray();

        if (input.Length == 0)
        {
            _log?.Invoke("[ASR.Whisper] skip empty audio");
            return new AsrResult(string.Empty, true, TimeSpan.Zero);
        }

        if (sampleRate != TargetSampleRate)
        {
            _log?.Invoke($"[ASR.Whisper] resample {sampleRate} -> {TargetSampleRate}, samples={input.Length}");
            input = ResampleLinear(input, sampleRate, TargetSampleRate);
            sampleRate = TargetSampleRate;
            _log?.Invoke($"[ASR.Whisper] resample done, samples={input.Length}");
        }

        await _transcribeLock.WaitAsync(cancellationToken);
        try
        {
            using var processor = _factory!
                .CreateBuilder()
                .WithLanguage("auto")
                .Build();

            using var wav = CreateWavStream(input, sampleRate);
            var text = new StringBuilder();

            _log?.Invoke($"[ASR.Whisper] transcribe start, samples={input.Length}, sampleRate={sampleRate}");

            await foreach (var segment in processor.ProcessAsync(wav).WithCancellation(cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    text.Append(segment.Text.Trim());
                    text.Append(' ');
                }
            }

            var output = text.ToString().Trim();
            var duration = DateTimeOffset.UtcNow - startedAt;

            _log?.Invoke($"[ASR.Whisper] transcribe end, textLen={output.Length}, durationMs={duration.TotalMilliseconds:F1}");

            return new AsrResult(output, true, duration);
        }
        finally
        {
            _transcribeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _factory?.Dispose();
        _initLock.Dispose();
        _transcribeLock.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_factory is not null)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_factory is not null)
            {
                return;
            }

            var modelRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PressTalk",
                "models");

            Directory.CreateDirectory(modelRoot);

            _modelPath = Path.Combine(modelRoot, "ggml-base.bin");

            if (!File.Exists(_modelPath))
            {
                _log?.Invoke($"[ASR.Whisper] model not found, downloading to {_modelPath}");
                using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    GgmlType.Base);

                await using var modelFile = File.Open(_modelPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await modelStream.CopyToAsync(modelFile, cancellationToken);
                _log?.Invoke("[ASR.Whisper] model download completed");
            }
            else
            {
                _log?.Invoke($"[ASR.Whisper] model found: {_modelPath}");
            }

            _factory = WhisperFactory.FromPath(_modelPath);
            _log?.Invoke("[ASR.Whisper] model initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static MemoryStream CreateWavStream(float[] samples, int sampleRate)
    {
        var pcm16 = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            pcm16[i] = (short)Math.Round(clamped * short.MaxValue);
        }

        var dataLength = pcm16.Length * sizeof(short);
        var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // PCM chunk size
        writer.Write((short)1); // PCM format
        writer.Write((short)1); // Mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short)); // byte rate
        writer.Write((short)sizeof(short)); // block align
        writer.Write((short)16); // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        foreach (var s in pcm16)
        {
            writer.Write(s);
        }

        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    private static float[] ResampleLinear(float[] input, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate || input.Length == 0)
        {
            return input;
        }

        var ratio = (double)targetRate / sourceRate;
        var outputLength = Math.Max(1, (int)Math.Round(input.Length * ratio));
        var output = new float[outputLength];

        for (var i = 0; i < outputLength; i++)
        {
            var position = i / ratio;
            var left = (int)Math.Floor(position);
            var right = Math.Min(left + 1, input.Length - 1);
            var t = (float)(position - left);
            output[i] = input[left] + (input[right] - input[left]) * t;
        }

        return output;
    }
}
