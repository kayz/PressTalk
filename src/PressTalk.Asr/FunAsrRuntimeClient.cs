using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PressTalk.Contracts.Asr;

namespace PressTalk.Asr;

public sealed class FunAsrRuntimeClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly FunAsrRuntimeOptions _options;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _startupLock = new(1, 1);
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private Task? _stderrPumpTask;
    private bool _disposed;

    public FunAsrRuntimeClient(
        FunAsrRuntimeOptions? options = null,
        Action<string>? log = null)
    {
        _options = options ?? new FunAsrRuntimeOptions();
        _log = log;
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);

        var response = await SendCommandAsync(
            new Dictionary<string, object?>
            {
                ["action"] = "ping"
            },
            cancellationToken);

        var runtimeVersion = TryReadString(response, "runtime_version") ?? "unknown";
        _log?.Invoke($"[FunASR.Runtime] ready, version={runtimeVersion}");
    }

    public async Task PreloadAsync(bool includeSpeakerDiarization, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);

        var response = await SendCommandAsync(
            new Dictionary<string, object?>
            {
                ["action"] = "preload",
                ["include_speaker_diarization"] = includeSpeakerDiarization
            },
            cancellationToken);

        var durationMs = TryReadDouble(response, "duration_ms");
        _log?.Invoke(
            $"[FunASR.Runtime] preload complete, speaker={includeSpeakerDiarization}, durationMs={durationMs?.ToString("F1") ?? "n/a"}");
    }

    public async Task StartStreamingSessionAsync(
        string sessionId,
        string languageHint,
        IReadOnlyList<string> hotwords,
        bool enableSpeakerDiarization,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);

        await SendCommandAsync(
            new Dictionary<string, object?>
            {
                ["action"] = "start_streaming_session",
                ["session_id"] = sessionId,
                ["language"] = languageHint,
                ["hotwords"] = hotwords,
                ["enable_speaker_diarization"] = enableSpeakerDiarization
            },
            cancellationToken);
    }

    public async Task<StreamingAsrResult> PushAudioChunkAsync(
        string sessionId,
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        if (audioSamples.Length == 0)
        {
            return new StreamingAsrResult(
                SessionId: sessionId,
                PreviewText: string.Empty,
                ConfirmedText: string.Empty,
                DeltaText: string.Empty,
                IsFinal: false,
                Duration: TimeSpan.Zero,
                SpeakerSegments: []);
        }

        await EnsureStartedAsync(cancellationToken);

        var samples = audioSamples.ToArray();
        if (sampleRate != 16000)
        {
            _log?.Invoke($"[FunASR.Runtime] resample {sampleRate} -> 16000, samples={samples.Length}");
            samples = ResampleLinear(samples, sampleRate, 16000);
            sampleRate = 16000;
        }

        var response = await SendCommandAsync(
            new Dictionary<string, object?>
            {
                ["action"] = "push_audio_chunk",
                ["session_id"] = sessionId,
                ["sample_rate"] = sampleRate,
                ["audio_base64"] = Convert.ToBase64String(ToPcm16(samples))
            },
            cancellationToken);

        return ParseStreamingAsrResult(sessionId, response);
    }

    public async Task<StreamingAsrResult> EndStreamingSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);

        var response = await SendCommandAsync(
            new Dictionary<string, object?>
            {
                ["action"] = "end_streaming_session",
                ["session_id"] = sessionId
            },
            cancellationToken);

        return ParseStreamingAsrResult(sessionId, response);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_process is not null && !_process.HasExited)
            {
                try
                {
                    await SendCommandAsync(
                        new Dictionary<string, object?> { ["action"] = "shutdown" },
                        CancellationToken.None);
                }
                catch
                {
                    // Ignore shutdown failures.
                }

                if (!_process.WaitForExit(3000))
                {
                    _log?.Invoke("[FunASR.Runtime] graceful shutdown timeout, killing process tree");
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            _stdin?.Dispose();
            _stdout?.Dispose();
            _process?.Dispose();
            _startupLock.Dispose();
            _commandLock.Dispose();

            if (_stderrPumpTask is not null)
            {
                try
                {
                    await _stderrPumpTask;
                }
                catch
                {
                    // Ignore stderr pump failure during disposal.
                }
            }
        }
    }

    private StreamingAsrResult ParseStreamingAsrResult(string sessionId, JsonElement response)
    {
        var previewText = TryReadString(response, "preview_text") ?? string.Empty;
        var confirmedText = TryReadString(response, "confirmed_text") ?? string.Empty;
        var deltaText = TryReadString(response, "delta_text") ?? string.Empty;
        var isFinal = response.TryGetProperty("is_final", out var isFinalElement) && isFinalElement.GetBoolean();
        var durationMs = TryReadDouble(response, "duration_ms") ?? 0;

        return new StreamingAsrResult(
            SessionId: sessionId,
            PreviewText: previewText,
            ConfirmedText: confirmedText,
            DeltaText: deltaText,
            IsFinal: isFinal,
            Duration: TimeSpan.FromMilliseconds(Math.Max(durationMs, 0)),
            SpeakerSegments: ParseSpeakerSegments(response));
    }

    private static IReadOnlyList<SpeakerSegment> ParseSpeakerSegments(JsonElement response)
    {
        if (!response.TryGetProperty("speaker_segments", out var segmentsElement)
            || segmentsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var segments = new List<SpeakerSegment>();
        foreach (var element in segmentsElement.EnumerateArray())
        {
            var speakerId = TryReadString(element, "speaker_id") ?? "speaker-1";
            var text = TryReadString(element, "text") ?? string.Empty;
            var startMs = TryReadInt(element, "start_ms");
            var endMs = TryReadInt(element, "end_ms");
            segments.Add(new SpeakerSegment(speakerId, text, startMs, endMs));
        }

        return segments;
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is not null && !_process.HasExited)
        {
            return;
        }

        await _startupLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                return;
            }

            if (!File.Exists(_options.ScriptPath))
            {
                throw new FileNotFoundException($"FunASR runtime script not found: {_options.ScriptPath}");
            }

            _log?.Invoke(
                $"[FunASR.Runtime] starting process, python='{_options.PythonExecutable}', script='{_options.ScriptPath}', model='{_options.StreamingModel}', device='{_options.Device}', int8={_options.EnableInt8Quantization}, speakerModel='{_options.SpeakerModel}'");

            var startInfo = new ProcessStartInfo
            {
                FileName = _options.PythonExecutable,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.Environment["PYTHONUTF8"] = "1";
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

            startInfo.ArgumentList.Add(_options.ScriptPath);
            startInfo.ArgumentList.Add("--streaming-model");
            startInfo.ArgumentList.Add(_options.StreamingModel);
            startInfo.ArgumentList.Add("--device");
            startInfo.ArgumentList.Add(_options.Device);
            startInfo.ArgumentList.Add("--speaker-model");
            startInfo.ArgumentList.Add(_options.SpeakerModel);
            startInfo.ArgumentList.Add("--int8");
            startInfo.ArgumentList.Add(_options.EnableInt8Quantization ? "1" : "0");

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start FunASR runtime process.");
            }

            _process = process;
            process.Exited += (_, _) =>
            {
                _log?.Invoke($"[FunASR.Runtime] process exited, pid={process.Id}, code={process.ExitCode}");
            };

            _stdin = process.StandardInput;
            _stdout = process.StandardOutput;
            _stderrPumpTask = PumpStdErrAsync(process.StandardError, CancellationToken.None);
            _log?.Invoke($"[FunASR.Runtime] process started, pid={process.Id}");
        }
        finally
        {
            _startupLock.Release();
        }
    }

    private async Task<JsonElement> SendCommandAsync(
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);

            if (_process is null || _stdin is null || _stdout is null)
            {
                throw new InvalidOperationException("FunASR runtime process is not initialized.");
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException($"FunASR runtime exited unexpectedly with code {_process.ExitCode}.");
            }

            var command = new Dictionary<string, object?>(payload)
            {
                ["request_id"] = Guid.NewGuid().ToString("N")
            };

            var action = payload.TryGetValue("action", out var actionObj) ? actionObj?.ToString() ?? "unknown" : "unknown";
            var requestId = command["request_id"]?.ToString() ?? string.Empty;
            var startedAt = Stopwatch.GetTimestamp();
            var line = JsonSerializer.Serialize(command, JsonOptions);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_options.CommandTimeoutMs));

            _log?.Invoke($"[FunASR.Runtime] command send, action={action}, request={requestId}");
            await _stdin.WriteLineAsync(line).WaitAsync(timeoutCts.Token);
            await _stdin.FlushAsync().WaitAsync(timeoutCts.Token);

            while (true)
            {
                var responseLine = await _stdout.ReadLineAsync().WaitAsync(timeoutCts.Token);
                if (string.IsNullOrWhiteSpace(responseLine))
                {
                    continue;
                }

                JsonDocument json;
                try
                {
                    json = JsonDocument.Parse(responseLine);
                }
                catch (JsonException)
                {
                    _log?.Invoke($"[FunASR.Runtime] non-json stdout ignored: {responseLine}");
                    continue;
                }

                using (json)
                {
                    var root = json.RootElement;
                    var responseRequestId = TryReadString(root, "request_id");
                    if (!string.IsNullOrWhiteSpace(responseRequestId)
                        && !string.Equals(responseRequestId, requestId, StringComparison.Ordinal))
                    {
                        _log?.Invoke(
                            $"[FunASR.Runtime] response request_id mismatch ignored, expected={requestId}, actual={responseRequestId}");
                        continue;
                    }

                    if (root.TryGetProperty("ok", out var okElement) && !okElement.GetBoolean())
                    {
                        var error = TryReadString(root, "error") ?? "unknown runtime error";
                        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                        _log?.Invoke(
                            $"[FunASR.Runtime] command failed, action={action}, request={requestId}, elapsedMs={elapsedMs:F1}, error='{error}'");
                        throw new InvalidOperationException($"FunASR runtime error: {error}");
                    }

                    var okElapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                    _log?.Invoke($"[FunASR.Runtime] command ok, action={action}, request={requestId}, elapsedMs={okElapsed:F1}");
                    return root.Clone();
                }
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task PumpStdErrAsync(StreamReader stderr, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await stderr.ReadLineAsync(cancellationToken);
            }
            catch
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                _log?.Invoke($"[FunASR.Runtime.Stderr] {line}");
            }
        }
    }

    private static byte[] ToPcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var sample = (short)Math.Round(clamped * short.MaxValue);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(short), sizeof(short)), sample);
        }

        return bytes;
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
            output[i] = input[left] + ((input[right] - input[left]) * t);
        }

        return output;
    }

    private static string? TryReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.ToString();
    }

    private static double? TryReadDouble(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int TryReadInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return 0;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        return 0;
    }
}
