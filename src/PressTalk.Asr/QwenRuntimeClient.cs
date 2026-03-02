using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PressTalk.Contracts.Asr;
using PressTalk.Contracts.Normalize;

namespace PressTalk.Asr;

public sealed class QwenRuntimeClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly QwenRuntimeOptions _options;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _startupLock = new(1, 1);
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private Task? _stderrPumpTask;
    private bool _disposed;
    private bool _suppressedGenerationWarningLogged;
    private bool _suppressedPadTokenWarningLogged;

    public QwenRuntimeClient(
        QwenRuntimeOptions? options = null,
        Action<string>? log = null)
    {
        _options = options ?? new QwenRuntimeOptions();
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
        _log?.Invoke($"[Qwen.Runtime] ready, version={runtimeVersion}");
    }

    public async Task WarmUpAsync(bool includePreview, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken);

        _log?.Invoke($"[Qwen.Runtime] warmup start, includePreview={includePreview}");
        await PreloadAsync(includePreview, includeSemantic: false, cancellationToken);

        _log?.Invoke("[Qwen.Runtime] warmup done");
    }

    public async Task PreloadAsync(bool includePreview, bool includeSemantic, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);

        _log?.Invoke($"[Qwen.Runtime] preload request, includePreview={includePreview}, includeSemantic={includeSemantic}");
        var response = await SendCommandAsync(
            new Dictionary<string, object?>
            {
                ["action"] = "preload",
                ["include_preview"] = includePreview,
                ["include_semantic"] = includeSemantic
            },
            cancellationToken);
        var durationMs = TryReadDouble(response, "duration_ms");
        var durationText = durationMs is double ms ? ms.ToString("F1") : "n/a";
        _log?.Invoke($"[Qwen.Runtime] preload completed, durationMs={durationText}");
    }

    public bool IsBusy => _commandLock.CurrentCount == 0;

    public async Task<AsrResult> TranscribeFinalAsync(
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        string languageHint,
        CancellationToken cancellationToken)
    {
        return await TranscribeInternalAsync(
            audioSamples,
            sampleRate,
            languageHint,
            "final",
            cancellationToken);
    }

    public async Task<AsrResult> TranscribePreviewAsync(
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        string languageHint,
        CancellationToken cancellationToken)
    {
        return await TranscribeInternalAsync(
            audioSamples,
            sampleRate,
            languageHint,
            "preview",
            cancellationToken);
    }

    public async Task<string> NormalizeAsync(
        string text,
        string languageHint,
        TextNormalizationOptions options,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableSemanticLlm || !options.EnableSemantic || string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        await EnsureStartedAsync(cancellationToken);

        var response = await SendCommandAsync(
            new Dictionary<string, object?>
            {
                ["action"] = "normalize",
                ["text"] = text,
                ["language"] = languageHint,
                ["scenario"] = options.Scenario,
                ["preserve_structured_items"] = options.PreserveStructuredItems
            },
            cancellationToken);

        return TryReadString(response, "text") ?? text;
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
                    // Ignore shutdown errors.
                }

                if (!_process.WaitForExit(3000))
                {
                    _log?.Invoke("[Qwen.Runtime] graceful shutdown timeout, killing process tree");
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
                    // Ignore stderr pump errors during disposal.
                }
            }
        }
    }

    private async Task<AsrResult> TranscribeInternalAsync(
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        string languageHint,
        string mode,
        CancellationToken cancellationToken)
    {
        if (audioSamples.Length == 0)
        {
            return new AsrResult(string.Empty, true, TimeSpan.Zero);
        }

        await EnsureStartedAsync(cancellationToken);

        var startedAt = DateTimeOffset.UtcNow;
        var input = audioSamples.ToArray();
        if (sampleRate != 16000)
        {
            _log?.Invoke($"[Qwen.Runtime] resample {sampleRate} -> 16000, samples={input.Length}");
            input = ResampleLinear(input, sampleRate, 16000);
            sampleRate = 16000;
            _log?.Invoke($"[Qwen.Runtime] resample done, samples={input.Length}");
        }

        var tempFile = WriteTempWav(input, sampleRate);
        _log?.Invoke($"[Qwen.Runtime] temp wav created, mode={mode}, sampleRate={sampleRate}, samples={input.Length}, path='{tempFile}'");

        try
        {
            var response = await SendCommandAsync(
                new Dictionary<string, object?>
                {
                    ["action"] = "asr",
                    ["mode"] = mode,
                    ["audio_path"] = tempFile,
                    ["language"] = languageHint
                },
                cancellationToken);

            var text = TryReadString(response, "text") ?? string.Empty;
            var durationMs = TryReadDouble(response, "duration_ms") ?? (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

            return new AsrResult(text, true, TimeSpan.FromMilliseconds(Math.Max(durationMs, 0)));
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Qwen.Runtime] temp wav cleanup failed, path='{tempFile}', error='{ex.Message}'");
            }
        }
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
                throw new FileNotFoundException(
                    $"Qwen runtime script not found: {_options.ScriptPath}");
            }

            _log?.Invoke(
                $"[Qwen.Runtime] starting process, python='{_options.PythonExecutable}', script='{_options.ScriptPath}', device='{_options.Device}', finalModel='{_options.AsrFinalModel}', previewModel='{_options.AsrPreviewModel}', semantic={_options.EnableSemanticLlm}");

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
            startInfo.ArgumentList.Add("--asr-final-model");
            startInfo.ArgumentList.Add(_options.AsrFinalModel);
            startInfo.ArgumentList.Add("--asr-preview-model");
            startInfo.ArgumentList.Add(_options.AsrPreviewModel);
            startInfo.ArgumentList.Add("--llm-model");
            startInfo.ArgumentList.Add(_options.LlmModel);
            startInfo.ArgumentList.Add("--device");
            startInfo.ArgumentList.Add(_options.Device);
            startInfo.ArgumentList.Add("--semantic-enabled");
            startInfo.ArgumentList.Add(_options.EnableSemanticLlm ? "1" : "0");

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start Qwen runtime process.");
            }

            _process = process;
            process.Exited += (_, _) =>
            {
                _log?.Invoke($"[Qwen.Runtime] process exited, pid={process.Id}, code={process.ExitCode}");
            };
            _stdin = process.StandardInput;
            _stdout = process.StandardOutput;
            _stderrPumpTask = PumpStdErrAsync(process.StandardError, CancellationToken.None);

            _log?.Invoke($"[Qwen.Runtime] process started, pid={process.Id}, script='{_options.ScriptPath}'");
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
                throw new InvalidOperationException("Qwen runtime process is not initialized.");
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException($"Qwen runtime exited unexpectedly with code {_process.ExitCode}.");
            }

            var command = new Dictionary<string, object?>(payload)
            {
                ["request_id"] = Guid.NewGuid().ToString("N")
            };
            var requestId = command["request_id"]?.ToString() ?? string.Empty;
            var action = payload.TryGetValue("action", out var actionObj) ? actionObj?.ToString() ?? "unknown" : "unknown";
            var startedAt = Stopwatch.GetTimestamp();
            _log?.Invoke($"[Qwen.Runtime] command send, action={action}, request={requestId}");

            var line = JsonSerializer.Serialize(command, JsonOptions);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_options.CommandTimeoutMs));

            await _stdin.WriteLineAsync(line).WaitAsync(timeoutCts.Token);
            await _stdin.FlushAsync().WaitAsync(timeoutCts.Token);

            var responseLine = await _stdout.ReadLineAsync().WaitAsync(timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                throw new InvalidOperationException("Qwen runtime returned an empty response.");
            }

            using var responseJson = JsonDocument.Parse(responseLine);
            var root = responseJson.RootElement;

            if (root.TryGetProperty("ok", out var okElement) && !okElement.GetBoolean())
            {
                var error = TryReadString(root, "error") ?? "unknown runtime error";
                var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                _log?.Invoke($"[Qwen.Runtime] command failed, action={action}, request={requestId}, elapsedMs={elapsedMs:F1}, error='{error}'");
                throw new InvalidOperationException($"Qwen runtime error: {error}");
            }

            var elapsedOkMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            _log?.Invoke($"[Qwen.Runtime] command ok, action={action}, request={requestId}, elapsedMs={elapsedOkMs:F1}");
            return root.Clone();
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
                if (line.Contains("generation flags are not valid", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_suppressedGenerationWarningLogged)
                    {
                        _suppressedGenerationWarningLogged = true;
                        _log?.Invoke("[Qwen.Runtime.Stderr] generation warnings suppressed");
                    }

                    continue;
                }

                if (line.Contains("Setting `pad_token_id`", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_suppressedPadTokenWarningLogged)
                    {
                        _suppressedPadTokenWarningLogged = true;
                        _log?.Invoke("[Qwen.Runtime.Stderr] pad_token warnings suppressed");
                    }

                    continue;
                }

                _log?.Invoke($"[Qwen.Runtime.Stderr] {line}");
            }
        }
    }

    private static string WriteTempWav(ReadOnlySpan<float> samples, int sampleRate)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PressTalk",
            "runtime-tmp");

        Directory.CreateDirectory(root);

        var path = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.wav");

        var pcm16 = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            pcm16[i] = (short)Math.Round(clamped * short.MaxValue);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        var dataLength = pcm16.Length * sizeof(short);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        foreach (var s in pcm16)
        {
            writer.Write(s);
        }

        return path;
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
}
