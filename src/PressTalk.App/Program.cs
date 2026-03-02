using System.Threading.Channels;
using PressTalk.App.Configuration;
using PressTalk.App.Hotkey;
using PressTalk.Asr;
using PressTalk.Audio;
using PressTalk.Commit;
using PressTalk.Contracts.Asr;
using PressTalk.Contracts.Commit;
using PressTalk.Engine;
using PressTalk.Normalize;
using System.Text;

var app = new PressTalkConsoleApp(args);
await app.RunAsync(CancellationToken.None);

internal enum AppLogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

internal sealed class AppLogger
{
    private readonly AppLogLevel _minimumLevel;

    public AppLogger(AppLogLevel minimumLevel)
    {
        _minimumLevel = minimumLevel;
    }

    public void Debug(string message) => Log(AppLogLevel.Debug, message);
    public void Info(string message) => Log(AppLogLevel.Info, message);
    public void Warn(string message) => Log(AppLogLevel.Warn, message);
    public void Error(string message) => Log(AppLogLevel.Error, message);

    public Action<string> AsDelegate(AppLogLevel level)
    {
        return message => Log(level, message);
    }

    private void Log(AppLogLevel level, string message)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level.ToString().ToUpperInvariant()}] {message}");
    }
}

internal sealed class PressTalkConsoleApp
{
    private const int StickySemanticMinChars = 80;

    private readonly string[] _args;
    private readonly UserConfigStore _configStore = new();

    public PressTalkConsoleApp(string[] args)
    {
        _args = args;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var minimumLogLevel = ResolveLogLevel(_args);
        var logger = new AppLogger(minimumLogLevel);
        Action<string> log = logger.AsDelegate(AppLogLevel.Debug);
        logger.Info($"[App] log level={minimumLogLevel}");
        log($"[App] process started, pid={Environment.ProcessId}, cwd='{Environment.CurrentDirectory}'");

        if (_args.Any(a => string.Equals(a, "--reset-hotkey", StringComparison.OrdinalIgnoreCase)))
        {
            _configStore.Delete();
            logger.Info("[App] Hotkey config was reset.");
        }

        var config = await EnsureConfigAsync(cancellationToken);
        var selectedPreset = HoldKeyPresetCatalog.Resolve(config.HoldKeyVirtualKey);
        log($"[App] hold key resolved, vk=0x{selectedPreset.VirtualKey:X}, name='{selectedPreset.DisplayName}'");

        Console.WriteLine("PressTalk is running.");
        Console.WriteLine($"Hold key: {selectedPreset.DisplayName}");
        Console.WriteLine($"Config file: {_configStore.ConfigPath}");
        Console.WriteLine("Hold the selected key to start recording, release to commit.");
        Console.WriteLine("Press hold-key + Space to lock dictation mode; press hold-key once to stop and commit.");
        Console.WriteLine("Press ESC in this console window or Ctrl+C to exit.");
        logger.Info("[App] Diagnostic log mode is enabled.");

        var commitMode = ResolveCommitMode(_args);
        log($"[App] commit mode={commitMode}");
        var committer = CreateCommitter(commitMode, log);

        log("[App] audio mode=wasapi");

        var enableSemanticLlm = _args.Any(a => string.Equals(a, "--enable-semantic-llm", StringComparison.OrdinalIgnoreCase));
        var enableLiveCaption = _args.Any(a => string.Equals(a, "--enable-live-caption", StringComparison.OrdinalIgnoreCase));
        var enableStickyDictationSemantic = true;

        var runtimeOptions = BuildQwenRuntimeOptions(_args, enableSemanticLlm || enableStickyDictationSemantic, log);
        log($"[App] asr mode=qwen, finalModel={runtimeOptions.AsrFinalModel}, previewModel={runtimeOptions.AsrPreviewModel}");
        log($"[App] qwen device={runtimeOptions.Device}");
        log($"[App] semantic llm runtime={(runtimeOptions.EnableSemanticLlm ? "on" : "off")}, manual={(enableSemanticLlm ? "on" : "off")} (use --enable-semantic-llm to turn on), stickyDictation={(enableStickyDictationSemantic ? "on" : "off")}, model={runtimeOptions.LlmModel}");
        log($"[App] live caption={(enableLiveCaption ? "on" : "off")} (use --enable-live-caption to turn on)");

        await using var qwenRuntime = new QwenRuntimeClient(runtimeOptions, log);
        await qwenRuntime.EnsureReadyAsync(cancellationToken);

        var asrBackend = new QwenAsrBackend(qwenRuntime, log);
        var hasSelfCheck = HasSelfCheck(_args);

        if (hasSelfCheck)
        {
            await RunSelfCheckAsync(asrBackend, log, cancellationToken);
            return;
        }

        var textNormalizer = new AdaptiveSemanticNormalizer(
            new RuleBasedNormalizer(log),
            new QwenSemanticNormalizer(qwenRuntime, log),
            log);

        var pipeline = new PressTalkPipeline(
            asrBackend,
            textNormalizer,
            committer,
            log);

        var windowTracker = new ForegroundWindowTracker(log);
        var audioCapture = new WasapiAudioCaptureService(log);

        var controller = new HoldToTalkController(
            audioCapture,
            pipeline,
            log: log);

        using var hotkeyHook = new GlobalHoldKeyHook(config.HoldKeyVirtualKey, log);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var warmupCts = new CancellationTokenSource();
        Task? warmupTask = null;
        var enableWarmup = _args.Any(a => string.Equals(a, "--enable-warmup", StringComparison.OrdinalIgnoreCase));

        var signalChannel = Channel.CreateUnbounded<HoldSignal>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        hotkeyHook.HoldStarted += () =>
        {
            var enqueued = signalChannel.Writer.TryWrite(HoldSignal.Start);
            log($"[App] enqueue signal Start, ok={enqueued}");
        };
        hotkeyHook.HoldEnded += () =>
        {
            var enqueued = signalChannel.Writer.TryWrite(HoldSignal.End);
            log($"[App] enqueue signal End, ok={enqueued}");
        };
        hotkeyHook.StickyModeToggleRequested += () =>
        {
            var enqueued = signalChannel.Writer.TryWrite(HoldSignal.StickyModeToggle);
            log($"[App] enqueue signal StickyModeToggle, ok={enqueued}");
        };

        var workerTask = RunSessionWorkerAsync(
            controller,
            windowTracker,
            audioCapture,
            qwenRuntime,
            enableLiveCaption,
            enableSemanticLlm,
            enableStickyDictationSemantic,
            signalChannel.Reader,
            cts.Token,
            log);

        var loopThreadId = NativeMethods.GetCurrentThreadId();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            NativeMethods.PostThreadMessage(loopThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            log("[App] Ctrl+C received, shutting down");
        };

        var exitListenerTask = StartExitListenerAsync(loopThreadId, cts.Token, log);

        hotkeyHook.Start();
        log("[App] message loop started");

        if (enableWarmup)
        {
            log("[App] warmup enabled");
            warmupTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await qwenRuntime.WarmUpAsync(
                            includePreview: !string.Equals(runtimeOptions.AsrFinalModel, runtimeOptions.AsrPreviewModel, StringComparison.Ordinal),
                            cancellationToken: warmupCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Ignore cancellation during shutdown.
                    }
                    catch (Exception ex)
                    {
                        log($"[App] warmup failed: {ex.Message}");
                    }
                },
                CancellationToken.None);
        }
        else
        {
            log("[App] warmup disabled by default (use --enable-warmup to turn on)");
        }

        while (true)
        {
            var result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);
            if (result == -1)
            {
                throw new InvalidOperationException("Message loop failed.");
            }

            if (result == 0)
            {
                break;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }

        cts.Cancel();
        warmupCts.Cancel();
        signalChannel.Writer.TryComplete();
        log("[App] message loop ended");

        await workerTask;
        await exitListenerTask;
        if (warmupTask is not null)
        {
            await Task.WhenAny(warmupTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    private async Task<AppUserConfig> EnsureConfigAsync(CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadAsync(cancellationToken);

        if (config is not null && HoldKeyPresetCatalog.IsSupported(config.HoldKeyVirtualKey))
        {
            return config;
        }

        Console.WriteLine("First launch setup: choose a hold key.");
        var preset = FirstRunSetupWizard.SelectPreset();

        var selected = new AppUserConfig
        {
            SchemaVersion = 1,
            HoldKeyName = preset.DisplayName,
            HoldKeyVirtualKey = preset.VirtualKey
        };

        await _configStore.SaveAsync(selected, cancellationToken);

        Console.WriteLine($"Saved hold key: {preset.DisplayName}");
        Console.WriteLine();

        return selected;
    }

    private static Task StartExitListenerAsync(uint loopThreadId, CancellationToken cancellationToken, Action<string> log)
    {
        return Task.Run(
            () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!Console.KeyAvailable)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Escape)
                    {
                        log("[App] ESC received, shutting down");
                        NativeMethods.PostThreadMessage(loopThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                        return;
                    }
                }
            },
            CancellationToken.None);
    }

    private static async Task RunSessionWorkerAsync(
        HoldToTalkController controller,
        ForegroundWindowTracker windowTracker,
        WasapiAudioCaptureService audioCapture,
        QwenRuntimeClient runtime,
        bool enableLiveCaption,
        bool enableManualSemanticLlm,
        bool enableStickyDictationSemantic,
        ChannelReader<HoldSignal> reader,
        CancellationToken cancellationToken,
        Action<string> log)
    {
        var isRecording = false;
        var isStickyDictationMode = false;
        Task? liveCaptionTask = null;
        CancellationTokenSource? liveCaptionCts = null;

        async Task StopAndCommitAsync()
        {
            if (liveCaptionCts is not null)
            {
                liveCaptionCts.Cancel();
            }

            if (liveCaptionTask is not null)
            {
                var completed = await Task.WhenAny(
                    liveCaptionTask,
                    Task.Delay(150, cancellationToken));

                if (completed != liveCaptionTask)
                {
                    log("[App.Live] caption loop did not stop within 150ms; proceeding with commit");
                }
            }

            if (enableLiveCaption)
            {
                Console.WriteLine("[PressTalk.Live] ");
            }

            var activated = windowTracker.TryActivateCapturedWindow();
            log($"[App.Worker] target activation result={activated}");
            await Task.Delay(30, cancellationToken);
            var result = await controller.OnReleaseAsync(
                cancellationToken,
                isStickyDictationMode: isStickyDictationMode,
                enableSemanticEnhancement: enableManualSemanticLlm || (enableStickyDictationSemantic && isStickyDictationMode));

            var semanticEnabledForSession = enableManualSemanticLlm || (enableStickyDictationSemantic && isStickyDictationMode);
            var semanticCandidateChars = result.RawText.Trim().Length;
            var semanticExpected = semanticEnabledForSession && semanticCandidateChars >= StickySemanticMinChars;
            log(
                $"[App.Worker] commit completed, session={result.SessionId}, stickyDictation={isStickyDictationMode}, semanticEnabled={semanticEnabledForSession}, semanticCandidateChars={semanticCandidateChars}, semanticExpected={semanticExpected}");
            Console.WriteLine($"[PressTalk] {DateTime.Now:HH:mm:ss} -> {result.NormalizedText}");
        }

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var signal))
                {
                    log($"[App.Worker] dequeued signal={signal}, isRecording={isRecording}, sticky={isStickyDictationMode}");

                    if (signal == HoldSignal.StickyModeToggle && isRecording && !isStickyDictationMode)
                    {
                        isStickyDictationMode = true;
                        Console.WriteLine("[PressTalk] Sticky dictation ON. Press hold key once to stop.");
                        log("[App.Worker] sticky dictation enabled");
                        continue;
                    }

                    if (signal == HoldSignal.Start && !isRecording)
                    {
                        try
                        {
                            windowTracker.CaptureAtPress();
                            var sessionId = await controller.OnPressAsync("auto", cancellationToken);
                            isRecording = true;
                            isStickyDictationMode = false;
                            Console.WriteLine("[PressTalk] Recording started...");
                            log("[App.Worker] recording started");

                            if (enableLiveCaption)
                            {
                                liveCaptionCts = new CancellationTokenSource();
                                liveCaptionTask = RunLiveCaptionLoopAsync(
                                    sessionId,
                                    audioCapture,
                                    runtime,
                                    liveCaptionCts.Token,
                                    log);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[PressTalk] Failed to start recording: {ex.Message}");
                            log($"[App.Worker] start failed: {ex}");
                        }
                    }
                    else if (signal == HoldSignal.Start && isRecording && isStickyDictationMode)
                    {
                        try
                        {
                            log("[App.Worker] sticky stop requested by hold-key press");
                            await StopAndCommitAsync();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[PressTalk] Failed to commit: {ex.Message}");
                            log($"[App.Worker] commit failed: {ex}");
                        }
                        finally
                        {
                            liveCaptionTask = null;
                            liveCaptionCts?.Dispose();
                            liveCaptionCts = null;
                            isRecording = false;
                            isStickyDictationMode = false;
                            log("[App.Worker] recording reset");
                        }
                    }

                    if (signal == HoldSignal.End && isRecording)
                    {
                        if (isStickyDictationMode)
                        {
                            log("[App.Worker] hold-key release ignored because sticky dictation is active");
                            continue;
                        }

                        try
                        {
                            await StopAndCommitAsync();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[PressTalk] Failed to commit: {ex.Message}");
                            log($"[App.Worker] commit failed: {ex}");
                        }
                        finally
                        {
                            liveCaptionTask = null;
                            liveCaptionCts?.Dispose();
                            liveCaptionCts = null;
                            isRecording = false;
                            isStickyDictationMode = false;
                            log("[App.Worker] recording reset");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            log("[App.Worker] canceled");
        }
        finally
        {
            if (liveCaptionCts is not null)
            {
                liveCaptionCts.Cancel();
                liveCaptionCts.Dispose();
            }

            if (liveCaptionTask is not null)
            {
                try
                {
                    await Task.WhenAny(liveCaptionTask, Task.Delay(200));
                }
                catch
                {
                    // Ignore background caption errors during shutdown.
                }
            }

            audioCapture.ForceStop();
        }
    }

    private static async Task RunLiveCaptionLoopAsync(
        string sessionId,
        WasapiAudioCaptureService audioCapture,
        QwenRuntimeClient runtime,
        CancellationToken cancellationToken,
        Action<string> log)
    {
        var lastPreview = string.Empty;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(450, cancellationToken);

                if (runtime.IsBusy)
                {
                    continue;
                }

                if (!audioCapture.TryGetLiveSnapshot(TimeSpan.FromSeconds(0.8), out var snapshot))
                {
                    continue;
                }

                if (snapshot.AudioSamples.Length < snapshot.SampleRate / 4)
                {
                    continue;
                }

                var preview = await runtime.TranscribePreviewAsync(
                    snapshot.AudioSamples,
                    snapshot.SampleRate,
                    "auto",
                    CancellationToken.None);

                var text = preview.Text.Trim();
                if (text.Length == 0 || string.Equals(text, lastPreview, StringComparison.Ordinal))
                {
                    continue;
                }

                lastPreview = text;
                Console.WriteLine($"[PressTalk.Live] {DateTime.Now:HH:mm:ss} {text}");
                log($"[App.Live] preview updated, session={sessionId}, textLen={text.Length}, durationMs={preview.Duration.TotalMilliseconds:F1}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log($"[App.Live] preview stopped: {ex.Message}");
                return;
            }
        }
    }

    private static string ResolveCommitMode(string[] args)
    {
        const string prefix = "--commit-mode=";
        var arg = args.FirstOrDefault(
            a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (arg is null)
        {
            return "paste";
        }

        var mode = arg[prefix.Length..].Trim().ToLowerInvariant();
        if (mode is "sendinput" or "paste")
        {
            return mode;
        }

        return "paste";
    }

    private static ITextCommitter CreateCommitter(string mode, Action<string> log)
    {
        return mode switch
        {
            "sendinput" => new SendInputTextCommitter(log),
            _ => new ClipboardPasteTextCommitter(log)
        };
    }

    private static bool HasSelfCheck(string[] args)
    {
        return args.Any(a => string.Equals(a, "--self-check", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task RunSelfCheckAsync(IAsrBackend backend, Action<string> log, CancellationToken cancellationToken)
    {
        log("[App.SelfCheck] started");
        var oneSecondSilence = new float[16000];
        var result = await backend.TranscribeAsync(oneSecondSilence, 16000, cancellationToken);
        log($"[App.SelfCheck] asr returned textLen={result.Text.Length}, durationMs={result.Duration.TotalMilliseconds:F1}");
        log($"[App.SelfCheck] asr text='{result.Text}'");
        log("[App.SelfCheck] finished");
    }

    private static QwenRuntimeOptions BuildQwenRuntimeOptions(string[] args, bool enableSemanticLlm, Action<string> log)
    {
        var scriptOverride = ResolveOption(args, "--qwen-script=");
        var pythonOverride = ResolveOption(args, "--qwen-python=");
        var finalModelOverride = ResolveOption(args, "--qwen-asr-final=");
        var previewModelOverride = ResolveOption(args, "--qwen-asr-preview=");
        var llmModelOverride = ResolveOption(args, "--qwen-llm=");
        var deviceOverride = ResolveOption(args, "--qwen-device=");
        var envDevice = Environment.GetEnvironmentVariable("PRESSTALK_QWEN_DEVICE");
        var pythonExecutable = pythonOverride
            ?? Environment.GetEnvironmentVariable("PRESSTALK_QWEN_PYTHON")
            ?? "python";
        var configuredDevice = deviceOverride ?? envDevice;
        var resolvedDevice = string.IsNullOrWhiteSpace(configuredDevice) || string.Equals(configuredDevice, "auto", StringComparison.OrdinalIgnoreCase)
            ? DetectPreferredDevice(pythonExecutable, log)
            : configuredDevice!;

        if (!string.IsNullOrWhiteSpace(deviceOverride))
        {
            log($"[App] device override from arg={resolvedDevice}");
        }
        else if (!string.IsNullOrWhiteSpace(envDevice))
        {
            log($"[App] device override from env={resolvedDevice}");
        }

        var defaultFinalModel = "Qwen/Qwen3-ASR-0.6B";
        var configuredFinalModel = finalModelOverride
            ?? Environment.GetEnvironmentVariable("PRESSTALK_QWEN_ASR_FINAL")
            ?? defaultFinalModel;
        var configuredPreviewModel = previewModelOverride
            ?? Environment.GetEnvironmentVariable("PRESSTALK_QWEN_ASR_PREVIEW")
            ?? configuredFinalModel;

        return new QwenRuntimeOptions
        {
            ScriptPath = scriptOverride
                ?? Environment.GetEnvironmentVariable("PRESSTALK_QWEN_SCRIPT")
                ?? Path.Combine(AppContext.BaseDirectory, "qwen_runtime.py"),
            PythonExecutable = pythonExecutable,
            AsrFinalModel = configuredFinalModel,
            AsrPreviewModel = configuredPreviewModel,
            LlmModel = llmModelOverride
                ?? Environment.GetEnvironmentVariable("PRESSTALK_QWEN_LLM")
                ?? "Qwen/Qwen3-0.6B",
            Device = resolvedDevice,
            EnableSemanticLlm = enableSemanticLlm,
            CommandTimeoutMs = 180000
        };
    }

    private static string DetectPreferredDevice(string pythonExecutable, Action<string> log)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("import torch; print('cuda:0' if torch.cuda.is_available() else 'cpu')");

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                log("[App] device detect failed to start python, fallback=cpu");
                return "cpu";
            }

            if (!process.WaitForExit(7000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                log("[App] device detect timeout, fallback=cpu");
                return "cpu";
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode == 0 && output.StartsWith("cuda", StringComparison.OrdinalIgnoreCase))
            {
                log($"[App] device detect result={output}");
                return output;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                log($"[App] device detect stderr='{error}'");
            }

            log("[App] device detect result=cpu");
            return "cpu";
        }
        catch (Exception ex)
        {
            log($"[App] device detect error='{ex.Message}', fallback=cpu");
            return "cpu";
        }
    }

    private static AppLogLevel ResolveLogLevel(string[] args)
    {
        var fromArg = ResolveOption(args, "--log-level=");
        var fromEnv = Environment.GetEnvironmentVariable("PRESSTALK_LOG_LEVEL");
        var raw = fromArg ?? fromEnv;

        if (!string.IsNullOrWhiteSpace(raw) && TryParseLogLevel(raw!, out var parsed))
        {
            return parsed;
        }

#if DEBUG
        return AppLogLevel.Debug;
#else
        return AppLogLevel.Info;
#endif
    }

    private static bool TryParseLogLevel(string raw, out AppLogLevel level)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "debug":
                level = AppLogLevel.Debug;
                return true;
            case "info":
                level = AppLogLevel.Info;
                return true;
            case "warn":
            case "warning":
                level = AppLogLevel.Warn;
                return true;
            case "error":
                level = AppLogLevel.Error;
                return true;
            default:
                level = AppLogLevel.Info;
                return false;
        }
    }

    private static string? ResolveOption(string[] args, string prefix)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (arg is null)
        {
            return null;
        }

        var value = arg[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
