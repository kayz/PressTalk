using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading.Channels;
using System.Windows.Forms;
using PressTalk.App.Configuration;
using PressTalk.App.Hotkey;
using PressTalk.App.Ui;
using PressTalk.Asr;
using PressTalk.Audio;
using PressTalk.Commit;
using PressTalk.Contracts.Asr;
using PressTalk.Contracts.Commit;
using PressTalk.Engine;
using PressTalk.Normalize;

namespace PressTalk.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var host = new PressTalkUiHost(args);

        try
        {
            host.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            Application.Run(host.Form);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"PressTalk startup failed:\n\n{ex.Message}",
                "PressTalk",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }
}

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
    private readonly object _sync = new();
    private readonly string _logPath;

    public AppLogger(AppLogLevel minimumLevel)
    {
        _minimumLevel = minimumLevel;
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PressTalk",
            "logs",
            $"{DateTime.Now:yyyyMMdd}.log");
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

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level.ToString().ToUpperInvariant()}] {message}";
        System.Diagnostics.Debug.WriteLine(line);

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must not break app runtime.
            }
        }
    }
}

internal sealed class PressTalkUiHost : IDisposable
{
    private const int StickySemanticMinChars = 80;

    private readonly string[] _args;
    private readonly UserConfigStore _configStore = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<HoldSignal> _signalChannel = Channel.CreateUnbounded<HoldSignal>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private AppLogger? _logger;
    private Action<string>? _log;

    private AppUserConfig? _config;
    private GlobalHoldKeyHook? _hotkeyHook;
    private QwenRuntimeClient? _runtime;
    private WasapiAudioCaptureService? _audioCapture;
    private HoldToTalkController? _controller;
    private ForegroundWindowTracker? _windowTracker;
    private Task? _workerTask;

    private bool _isRecording;
    private bool _isStickyRecording;
    private bool _isHotkeyPressed;

    private bool _enableLiveCaption;
    private bool _enableManualSemanticLlm;
    private bool _enableStickyDictationSemantic;

    public PressTalkUiHost(string[] args)
    {
        _args = args;
        Form = new FloatingRecorderForm();
        Form.FormClosing += (_, _) =>
        {
            SaveWindowPosition();
            ShutdownAsync().GetAwaiter().GetResult();
        };
        Form.SettingsRequested += (_, _) => OpenSettingsDialog();
    }

    public FloatingRecorderForm Form { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var minimumLogLevel = ResolveLogLevel(_args);
        _logger = new AppLogger(minimumLogLevel);
        _log = _logger.AsDelegate(AppLogLevel.Debug);

        _logger.Info($"[App] log level={minimumLogLevel}");
        _log($"[App] process started, pid={Environment.ProcessId}, cwd='{Environment.CurrentDirectory}'");

        if (_args.Any(a => string.Equals(a, "--reset-hotkey", StringComparison.OrdinalIgnoreCase)))
        {
            _configStore.Delete();
            _logger.Info("[App] Hotkey config was reset.");
        }

        _config = await EnsureConfigAsync(cancellationToken);
        _enableLiveCaption = _config.EnableLiveCaption || HasFlag("--enable-live-caption");
        _enableManualSemanticLlm = _config.EnableManualSemanticLlm || HasFlag("--enable-semantic-llm");
        _enableStickyDictationSemantic = _config.EnableStickyDictationSemantic;

        ApplyFormConfig(_config);
        _logger.Info("[App] UI mode enabled with floating button");
        _log(
            $"[App] settings loaded, holdKey={_config.HoldKeyName}, liveCaption={_enableLiveCaption}, manualSemantic={_enableManualSemanticLlm}, stickySemantic={_enableStickyDictationSemantic}, topMost={_config.AlwaysOnTop}");

        var commitMode = ResolveCommitMode(_args);
        _log($"[App] commit mode={commitMode}");
        var committer = CreateCommitter(commitMode, _log);

        var runtimeSemanticEnabled = _enableManualSemanticLlm || _enableStickyDictationSemantic;
        var runtimeOptions = BuildQwenRuntimeOptions(_args, runtimeSemanticEnabled, _log);
        _log($"[App] asr mode=qwen, finalModel={runtimeOptions.AsrFinalModel}, previewModel={runtimeOptions.AsrPreviewModel}");
        _log($"[App] qwen device={runtimeOptions.Device}");
        _log(
            $"[App] semantic llm runtime={(runtimeOptions.EnableSemanticLlm ? "on" : "off")}, manual={_enableManualSemanticLlm}, stickyDictation={_enableStickyDictationSemantic}, model={runtimeOptions.LlmModel}");

        _runtime = new QwenRuntimeClient(runtimeOptions, _log);
        await _runtime.EnsureReadyAsync(cancellationToken);

        var asrBackend = new QwenAsrBackend(_runtime, _log);
        var textNormalizer = new AdaptiveSemanticNormalizer(
            new RuleBasedNormalizer(_log),
            new QwenSemanticNormalizer(_runtime, _log),
            _log);

        var pipeline = new PressTalkPipeline(
            asrBackend,
            textNormalizer,
            committer,
            _log);

        _audioCapture = new WasapiAudioCaptureService(_log);
        _windowTracker = new ForegroundWindowTracker(_log);
        _controller = new HoldToTalkController(
            _audioCapture,
            pipeline,
            log: _log);

        BindHotkey(_config.HoldKeyVirtualKey);
        _workerTask = RunSessionWorkerAsync(_shutdownCts.Token);

        Form.SetHintText("Click to settings");
        _logger.Info("[App] Ready");
    }

    public void Dispose()
    {
        ShutdownAsync().GetAwaiter().GetResult();
        _shutdownCts.Dispose();
        _hotkeyHook?.Dispose();
        _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task ShutdownAsync()
    {
        if (_shutdownCts.IsCancellationRequested)
        {
            return;
        }

        _shutdownCts.Cancel();
        _signalChannel.Writer.TryComplete();

        _hotkeyHook?.Dispose();
        _hotkeyHook = null;

        if (_workerTask is not null)
        {
            try
            {
                await _workerTask;
            }
            catch
            {
                // Ignore shutdown exceptions.
            }
        }

        if (_runtime is not null)
        {
            try
            {
                await _runtime.DisposeAsync();
            }
            catch
            {
                // Ignore runtime shutdown exceptions.
            }

            _runtime = null;
        }
    }

    private void OpenSettingsDialog()
    {
        if (_config is null)
        {
            return;
        }

        if (_isRecording)
        {
            MessageBox.Show(
                "Stop recording before opening settings.",
                "PressTalk",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SettingsForm(CloneConfig(_config));
        if (dialog.ShowDialog(Form) != DialogResult.OK)
        {
            return;
        }

        var updated = dialog.BuildUpdatedConfig(_config);
        updated.FloatingWindowX = Form.Left;
        updated.FloatingWindowY = Form.Top;
        _config = updated;

        _enableLiveCaption = updated.EnableLiveCaption;
        _enableManualSemanticLlm = updated.EnableManualSemanticLlm;
        _enableStickyDictationSemantic = updated.EnableStickyDictationSemantic;

        ApplyFormConfig(updated);
        _log?.Invoke(
            $"[App] settings updated, holdKey={updated.HoldKeyName}, liveCaption={_enableLiveCaption}, manualSemantic={_enableManualSemanticLlm}, stickySemantic={_enableStickyDictationSemantic}, topMost={updated.AlwaysOnTop}");

        BindHotkey(updated.HoldKeyVirtualKey);
        _configStore.SaveAsync(updated, CancellationToken.None).GetAwaiter().GetResult();
    }

    private void ApplyFormConfig(AppUserConfig config)
    {
        Form.SetHotkeyText(HoldKeyPresetCatalog.Resolve(config.HoldKeyVirtualKey).DisplayName);
        Form.SetTopMost(config.AlwaysOnTop);

        var start = ResolveWindowLocation(config);
        Form.Location = start;
    }

    private Point ResolveWindowLocation(AppUserConfig config)
    {
        if (config.FloatingWindowX is int x && config.FloatingWindowY is int y)
        {
            return new Point(x, y);
        }

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return new Point(area.Right - Form.Width - 32, Math.Max(area.Top + 24, area.Height / 4));
    }

    private void SaveWindowPosition()
    {
        if (_config is null)
        {
            return;
        }

        _config.FloatingWindowX = Form.Left;
        _config.FloatingWindowY = Form.Top;
        _configStore.SaveAsync(_config, CancellationToken.None).GetAwaiter().GetResult();
    }

    private void BindHotkey(uint virtualKey)
    {
        _hotkeyHook?.Dispose();
        _hotkeyHook = new GlobalHoldKeyHook(virtualKey, _log);
        _hotkeyHook.HoldStarted += OnHotkeyStarted;
        _hotkeyHook.HoldEnded += OnHotkeyEnded;
        _hotkeyHook.StickyModeToggleRequested += OnStickyToggleRequested;
        _hotkeyHook.Start();
    }

    private void OnHotkeyStarted()
    {
        _isHotkeyPressed = true;
        UpdateButtonVisual();
        var ok = _signalChannel.Writer.TryWrite(HoldSignal.Start);
        _log?.Invoke($"[App] enqueue signal Start, ok={ok}");
    }

    private void OnHotkeyEnded()
    {
        _isHotkeyPressed = false;
        UpdateButtonVisual();
        var ok = _signalChannel.Writer.TryWrite(HoldSignal.End);
        _log?.Invoke($"[App] enqueue signal End, ok={ok}");
    }

    private void OnStickyToggleRequested()
    {
        var ok = _signalChannel.Writer.TryWrite(HoldSignal.StickyModeToggle);
        _log?.Invoke($"[App] enqueue signal StickyModeToggle, ok={ok}");
    }

    private void UpdateButtonVisual()
    {
        Form.SetVisualState(
            isPressed: _isHotkeyPressed,
            isRecording: _isRecording,
            isSticky: _isStickyRecording);
    }

    private async Task RunSessionWorkerAsync(CancellationToken cancellationToken)
    {
        if (_controller is null || _windowTracker is null || _audioCapture is null || _runtime is null)
        {
            throw new InvalidOperationException("Worker started before initialization.");
        }

        Task? liveCaptionTask = null;
        CancellationTokenSource? liveCaptionCts = null;
        var stickyMode = false;

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
                    _log?.Invoke("[App.Live] caption loop did not stop within 150ms; proceeding with commit");
                }
            }

            var activated = _windowTracker.TryActivateCapturedWindow();
            _log?.Invoke($"[App.Worker] target activation result={activated}");
            await Task.Delay(30, cancellationToken);

            var semanticEnabledForSession =
                _enableManualSemanticLlm
                || (_enableStickyDictationSemantic && stickyMode);

            var result = await _controller.OnReleaseAsync(
                cancellationToken,
                isStickyDictationMode: stickyMode,
                enableSemanticEnhancement: semanticEnabledForSession);

            var semanticCandidateChars = result.RawText.Trim().Length;
            var semanticExpected = semanticEnabledForSession && semanticCandidateChars >= StickySemanticMinChars;
            _log?.Invoke(
                $"[App.Worker] commit completed, session={result.SessionId}, stickyDictation={stickyMode}, semanticEnabled={semanticEnabledForSession}, semanticCandidateChars={semanticCandidateChars}, semanticExpected={semanticExpected}");
        }

        try
        {
            while (await _signalChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_signalChannel.Reader.TryRead(out var signal))
                {
                    _log?.Invoke($"[App.Worker] dequeued signal={signal}, isRecording={_isRecording}, sticky={stickyMode}");

                    if (signal == HoldSignal.StickyModeToggle && _isRecording && !stickyMode)
                    {
                        stickyMode = true;
                        _isStickyRecording = true;
                        UpdateButtonVisual();
                        Form.SetHintText("Sticky ON, press hotkey again to stop");
                        _log?.Invoke("[App.Worker] sticky dictation enabled");
                        continue;
                    }

                    if (signal == HoldSignal.Start && !_isRecording)
                    {
                        try
                        {
                            _windowTracker.CaptureAtPress();
                            var sessionId = await _controller.OnPressAsync("auto", cancellationToken);
                            _isRecording = true;
                            stickyMode = false;
                            _isStickyRecording = false;
                            Form.SetHintText("Recording...");
                            UpdateButtonVisual();
                            _log?.Invoke($"[App.Worker] recording started, session={sessionId}");

                            if (_enableLiveCaption)
                            {
                                liveCaptionCts = new CancellationTokenSource();
                                liveCaptionTask = RunLiveCaptionLoopAsync(
                                    sessionId,
                                    _audioCapture,
                                    _runtime,
                                    liveCaptionCts.Token,
                                    _log!);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log?.Invoke($"[App.Worker] start failed: {ex}");
                            _isRecording = false;
                            stickyMode = false;
                            _isStickyRecording = false;
                            UpdateButtonVisual();
                            Form.SetHintText("Start failed, click to settings");
                        }

                        continue;
                    }

                    if (signal == HoldSignal.Start && _isRecording && stickyMode)
                    {
                        try
                        {
                            _log?.Invoke("[App.Worker] sticky stop requested by hotkey press");
                            await StopAndCommitAsync();
                        }
                        catch (Exception ex)
                        {
                            _log?.Invoke($"[App.Worker] commit failed: {ex}");
                        }
                        finally
                        {
                            liveCaptionTask = null;
                            liveCaptionCts?.Dispose();
                            liveCaptionCts = null;
                            _isRecording = false;
                            stickyMode = false;
                            _isStickyRecording = false;
                            _isHotkeyPressed = false;
                            Form.SetHintText("Click to settings");
                            UpdateButtonVisual();
                        }

                        continue;
                    }

                    if (signal == HoldSignal.End && _isRecording)
                    {
                        if (stickyMode)
                        {
                            _log?.Invoke("[App.Worker] hold-key release ignored because sticky dictation is active");
                            continue;
                        }

                        try
                        {
                            await StopAndCommitAsync();
                        }
                        catch (Exception ex)
                        {
                            _log?.Invoke($"[App.Worker] commit failed: {ex}");
                        }
                        finally
                        {
                            liveCaptionTask = null;
                            liveCaptionCts?.Dispose();
                            liveCaptionCts = null;
                            _isRecording = false;
                            stickyMode = false;
                            _isStickyRecording = false;
                            _isHotkeyPressed = false;
                            Form.SetHintText("Click to settings");
                            UpdateButtonVisual();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke("[App.Worker] canceled");
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
                    // Ignore preview errors during shutdown.
                }
            }

            _audioCapture.ForceStop();
            _isRecording = false;
            _isStickyRecording = false;
            _isHotkeyPressed = false;
            UpdateButtonVisual();
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

    private async Task<AppUserConfig> EnsureConfigAsync(CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadAsync(cancellationToken);

        if (config is not null && HoldKeyPresetCatalog.IsSupported(config.HoldKeyVirtualKey))
        {
            return config;
        }

        var preset = HoldKeyPresetCatalog.Default;
        var selected = new AppUserConfig
        {
            SchemaVersion = 1,
            HoldKeyName = preset.DisplayName,
            HoldKeyVirtualKey = preset.VirtualKey,
            EnableLiveCaption = false,
            EnableManualSemanticLlm = false,
            EnableStickyDictationSemantic = true,
            AlwaysOnTop = true
        };

        await _configStore.SaveAsync(selected, cancellationToken);
        return selected;
    }

    private static AppUserConfig CloneConfig(AppUserConfig source)
    {
        return new AppUserConfig
        {
            SchemaVersion = source.SchemaVersion,
            HoldKeyName = source.HoldKeyName,
            HoldKeyVirtualKey = source.HoldKeyVirtualKey,
            EnableLiveCaption = source.EnableLiveCaption,
            EnableManualSemanticLlm = source.EnableManualSemanticLlm,
            EnableStickyDictationSemantic = source.EnableStickyDictationSemantic,
            AlwaysOnTop = source.AlwaysOnTop,
            FloatingWindowX = source.FloatingWindowX,
            FloatingWindowY = source.FloatingWindowY
        };
    }

    private bool HasFlag(string flag)
    {
        return _args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveCommitMode(string[] args)
    {
        const string prefix = "--commit-mode=";
        var arg = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (arg is null)
        {
            return "paste";
        }

        var mode = arg[prefix.Length..].Trim().ToLowerInvariant();
        return mode is "sendinput" or "paste" ? mode : "paste";
    }

    private static ITextCommitter CreateCommitter(string mode, Action<string> log)
    {
        return mode switch
        {
            "sendinput" => new SendInputTextCommitter(log),
            _ => new ClipboardPasteTextCommitter(log)
        };
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
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("import torch; print('cuda:0' if torch.cuda.is_available() else 'cpu')");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                log("[App] device detect failed to start python, fallback=cpu");
                return "cpu";
            }

            if (!process.WaitForExit(7000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore cleanup failure.
                }

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
