using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
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
using PressTalk.Data;
using PressTalk.Engine;

namespace PressTalk.App;

internal static class Program
{
    private const int SwHide = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [STAThread]
    private static int Main(string[] args)
    {
        // Hide console host window when launched via `dotnet run`.
        var console = GetConsoleWindow();
        if (console != IntPtr.Zero)
        {
            _ = ShowWindow(console, SwHide);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var host = new PressTalkUiHost(args);

        try
        {
            host.Form.Shown += (_, _) => host.StartInitialization();
            Application.Run(host.Form);
            return host.ExitCode;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"PressTalk 启动失败：\n\n{ex.Message}",
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
    private readonly string[] _args;
    private readonly UserConfigStore _configStore = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<ToggleSignal> _signalChannel = Channel.CreateUnbounded<ToggleSignal>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private AppLogger? _logger;
    private Action<string>? _log;

    private AppUserConfig? _config;
    private GlobalHoldKeyHook? _hotkeyHook;
    private FunAsrRuntimeClient? _runtime;
    private WasapiAudioCaptureService? _audioCapture;
    private StreamingController? _controller;
    private ForegroundWindowTracker? _windowTracker;
    private JsonHistoryStore? _historyStore;
    private Task? _workerTask;

    private bool _isRecording;
    private bool _isHotkeyPressed;
    private bool _initialized;
    private bool _initializationStarted;
    private bool _hotkeyConflictActive;
    private bool _shutdownInProgress;

    private string _lastPreviewText = string.Empty;
    private string _lastConfirmedText = string.Empty;
    private IReadOnlyList<SpeakerSegment> _lastSpeakerSegments = [];

    public PressTalkUiHost(string[] args)
    {
        _args = args;
        Form = new FloatingRecorderForm();
        Form.SetHintText("启动中...");
        Form.SettingsRequested += (_, _) => OpenSettingsDialog();
        Form.ExitRequested += (_, _) => BeginShutdownAndExit(forceExit: true);
        Form.FormClosing += (_, e) =>
        {
            e.Cancel = true;
            BeginShutdownAndExit(forceExit: false);
        };
    }

    public FloatingRecorderForm Form { get; }
    public int ExitCode { get; private set; }

    public void StartInitialization()
    {
        if (_initializationStarted)
        {
            return;
        }

        _initializationStarted = true;
        _ = InitializeInBackgroundAsync();
    }

    public void Dispose()
    {
        ShutdownAsync().GetAwaiter().GetResult();
        _shutdownCts.Dispose();
    }

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
            _logger.Info("[App] hotkey config reset requested");
        }

        _config = await EnsureConfigAsync(cancellationToken);
        ApplyFormConfig(_config);

        _log(
            $"[App] settings loaded, holdKey={_config.HoldKeyName}, hotwords={_config.HotwordConfig.GetNormalizedTerms().Count}, speaker={_config.EnableSpeakerDiarization}, mode={_config.TranscriptionMode}, topMost={_config.AlwaysOnTop}");

        ITextCommitter committer = new ClipboardPasteTextCommitter(_log);

        var runtimeOptions = BuildFunAsrRuntimeOptions(_args, _config, _log);
        _runtime = new FunAsrRuntimeClient(runtimeOptions, _log);
        await _runtime.EnsureReadyAsync(cancellationToken);
        await _runtime.PreloadAsync(_config.EnableSpeakerDiarization, cancellationToken);

        var backend = new FunAsrBackend(_runtime, _log);
        var streamingPipeline = new StreamingPipeline(backend, committer, _log);

        _audioCapture = new WasapiAudioCaptureService(_log);
        _windowTracker = new ForegroundWindowTracker(_log);
        _historyStore = new JsonHistoryStore();

        _controller = new StreamingController(_audioCapture, streamingPipeline, log: _log);
        _controller.ResultUpdated += OnStreamingResult;

        BindHotkey(_config.HoldKeyVirtualKey);
        _workerTask = RunSessionWorkerAsync(_shutdownCts.Token);

        Form.SetHintText(_hotkeyConflictActive ? "热键冲突，请改键" : "点击开始/停止");
        _logger.Info("[App] Ready");
    }

    private async Task InitializeInBackgroundAsync()
    {
        try
        {
            await InitializeAsync(_shutdownCts.Token);
            _initialized = true;
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
        catch (Exception ex)
        {
            ExitCode = 1;
            _logger?.Error($"[App] background initialization failed: {ex}");
            if (Form.IsDisposed)
            {
                return;
            }

            if (Form.InvokeRequired)
            {
                Form.BeginInvoke(new MethodInvoker(() => ShowInitFailureAndClose(ex)));
                return;
            }

            ShowInitFailureAndClose(ex);
        }
    }

    private async Task ShutdownAsync()
    {
        _log?.Invoke("[App] === Shutdown started ===");

        if (_shutdownCts.IsCancellationRequested)
        {
            _log?.Invoke("[App] Shutdown already requested, returning");
            return;
        }

        _log?.Invoke("[App] Disposing hotkey hook");
        _hotkeyHook?.Dispose();
        _hotkeyHook = null;

        if (_isRecording && _controller is not null)
        {
            _log?.Invoke("[App] Stopping active recording");
            try
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var finalResult = await _controller.StopStreamingSessionAsync(stopTimeout.Token);
                _log?.Invoke("[App] Recording stopped successfully");
                _isRecording = false;
                _isHotkeyPressed = false;
                UpdateButtonVisual();
                Form.SetLivePreview(
                    finalResult.PreviewText,
                    finalResult.ConfirmedText,
                    finalResult.SpeakerSegments);
                await SaveHistoryAsync(finalResult, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[App] shutdown stop recording failed: {ex.Message}");
                _isRecording = false;
                _isHotkeyPressed = false;
                UpdateButtonVisual();
            }
        }

        _log?.Invoke("[App] Cancelling shutdown token");
        _shutdownCts.Cancel();
        _signalChannel.Writer.TryComplete();

        if (_workerTask is not null)
        {
            _log?.Invoke("[App] Waiting for worker task");
            try
            {
                var completed = await Task.WhenAny(_workerTask, Task.Delay(1500));
                if (completed != _workerTask)
                {
                    _log?.Invoke("[App] worker shutdown timed out");
                }
                else
                {
                    _log?.Invoke("[App] worker task completed");
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[App] worker error: {ex.Message}");
            }
        }

        if (_runtime is not null)
        {
            _log?.Invoke("[App] Disposing runtime");
            try
            {
                var disposeTask = _runtime.DisposeAsync().AsTask();
                var completed = await Task.WhenAny(disposeTask, Task.Delay(2500));
                if (completed != disposeTask)
                {
                    _log?.Invoke("[App] runtime shutdown timed out");
                }
                else
                {
                    _log?.Invoke("[App] runtime disposed successfully");
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[App] runtime error: {ex.Message}");
            }

            _runtime = null;
        }

        _log?.Invoke("[App] === Shutdown completed ===");
    }

    private void BeginShutdownAndExit(bool forceExit)
    {
        if (_shutdownInProgress)
        {
            _log?.Invoke("[App] Shutdown already in progress or completed");
            return;
        }

        _log?.Invoke("[App] BeginShutdownAndExit called");
        _shutdownInProgress = true;

        // CRITICAL: exit immediately to avoid UI deadlock path.
        // Cleanup runs in background best-effort; process will terminate regardless.
        _ = Task.Run(async () =>
        {
            try
            {
                _log?.Invoke("[App] Starting best-effort shutdown cleanup");
                var shutdownTask = ShutdownAsync();
                var timeoutTask = Task.Delay(1200);
                var completedTask = await Task.WhenAny(shutdownTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    _log?.Invoke("[App] Cleanup timeout (1.2s)");
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[App] Cleanup error: {ex.Message}");
            }
        });

        _log?.Invoke("[App] Force exiting process now");
        Environment.Exit(0);
    }

    private async Task RunSessionWorkerAsync(CancellationToken cancellationToken)
    {
        if (_controller is null || _windowTracker is null)
        {
            throw new InvalidOperationException("Worker started before initialization.");
        }

        try
        {
            while (await _signalChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_signalChannel.Reader.TryRead(out var signal))
                {
                    if (signal != ToggleSignal.Toggle)
                    {
                        continue;
                    }

                    _log?.Invoke($"[App.Worker] toggle received, recording={_isRecording}");
                    if (!_isRecording)
                    {
                        try
                        {
                            _windowTracker.CaptureAtPress();
                            var hotwords = _config?.HotwordConfig.GetNormalizedTerms() ?? [];
                            var enableSpeaker = _config?.EnableSpeakerDiarization ?? false;
                            var sessionId = await _controller.StartStreamingSessionAsync(
                                languageHint: "auto",
                                hotwords: hotwords,
                                enableSpeakerDiarization: enableSpeaker,
                                cancellationToken: cancellationToken);

                            _windowTracker.TryActivateCapturedWindow();
                            _isRecording = true;
                            _lastPreviewText = string.Empty;
                            _lastConfirmedText = string.Empty;
                            _lastSpeakerSegments = [];
                            Form.SetLivePreview("", "");
                            Form.SetHintText("录音中");
                            UpdateButtonVisual();
                            _log?.Invoke($"[App.Worker] recording started, session={sessionId}");
                        }
                        catch (Exception ex)
                        {
                            _log?.Invoke($"[App.Worker] recording start failed: {ex.Message}");
                            _isRecording = false;
                            Form.SetHintText("启动失败，点击设置");
                            UpdateButtonVisual();
                        }

                        continue;
                    }

                    try
                    {
                        _windowTracker.TryActivateCapturedWindow();
                        await Task.Delay(30, cancellationToken);
                        var finalResult = await _controller.StopStreamingSessionAsync(cancellationToken);
                        _isRecording = false;
                        _isHotkeyPressed = false;
                        UpdateButtonVisual();
                        Form.SetHintText(_hotkeyConflictActive ? "热键冲突，请改键" : "点击开始/停止");
                        Form.SetLivePreview(
                            finalResult.PreviewText,
                            finalResult.ConfirmedText,
                            finalResult.SpeakerSegments);
                        await SaveHistoryAsync(finalResult, cancellationToken);
                        _log?.Invoke(
                            $"[App.Worker] recording stopped, session={finalResult.SessionId}, textLen={finalResult.ConfirmedText.Length}, speakers={finalResult.SpeakerSegments.Count}");
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"[App.Worker] recording stop failed: {ex.Message}");
                        _isRecording = false;
                        _isHotkeyPressed = false;
                        UpdateButtonVisual();
                        Form.SetHintText("停止失败，点击设置");
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
            _isRecording = false;
            _isHotkeyPressed = false;
            UpdateButtonVisual();
        }
    }

    private void OnStreamingResult(StreamingAsrResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.PreviewText))
        {
            _lastPreviewText = result.PreviewText;
        }

        if (!string.IsNullOrWhiteSpace(result.ConfirmedText))
        {
            _lastConfirmedText = result.ConfirmedText;
        }

        if (result.SpeakerSegments.Count > 0)
        {
            _lastSpeakerSegments = result.SpeakerSegments;
        }

        var segments = _lastSpeakerSegments.Count > 0 ? _lastSpeakerSegments : null;
        Form.SetLivePreview(_lastPreviewText, _lastConfirmedText, segments);
    }

    private async Task SaveHistoryAsync(StreamingAsrResult result, CancellationToken cancellationToken)
    {
        if (_historyStore is null)
        {
            return;
        }

        var preferredText = string.IsNullOrWhiteSpace(result.PreviewText)
            ? result.ConfirmedText
            : result.PreviewText;
        var text = preferredText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var record = new HistoryRecord(
            SessionId: result.SessionId,
            Timestamp: DateTimeOffset.Now,
            RawText: text,
            NormalizedText: text,
            AppName: "PressTalk.Streaming");

        await _historyStore.AppendAsync(record, cancellationToken);
    }

    private void BindHotkey(uint virtualKey)
    {
        _hotkeyHook?.Dispose();
        _hotkeyHook = null;
        _hotkeyConflictActive = false;

        if (IsGlobalHotkeyOccupied(virtualKey))
        {
            _hotkeyConflictActive = true;
            _log?.Invoke($"[Hotkey.Hook] bind blocked by conflict, vk=0x{virtualKey:X}");
            Form.SetHintText("热键冲突，请改键");
            ShowWarning($"热键 {HoldKeyPresetCatalog.Resolve(virtualKey).DisplayName} 被其他程序占用，请到设置中更换。");
            UpdateButtonVisual();
            return;
        }

        _hotkeyHook = new GlobalHoldKeyHook(virtualKey, _log);
        _hotkeyHook.HoldStarted += OnHotkeyStarted;
        _hotkeyHook.HoldEnded += OnHotkeyEnded;
        _hotkeyHook.StickyModeToggleRequested += OnToggleRequested;
        _hotkeyHook.Start();
    }

    private void OnHotkeyStarted()
    {
        if (!_initialized || _hotkeyConflictActive)
        {
            return;
        }

        _isHotkeyPressed = true;
        UpdateButtonVisual();
        var ok = _signalChannel.Writer.TryWrite(ToggleSignal.Toggle);
        _log?.Invoke($"[App] enqueue toggle from hotkey, ok={ok}");
    }

    private void OnHotkeyEnded()
    {
        _isHotkeyPressed = false;
        UpdateButtonVisual();
    }

    private void OnToggleRequested()
    {
        if (!_initialized || _hotkeyConflictActive)
        {
            return;
        }

        var ok = _signalChannel.Writer.TryWrite(ToggleSignal.Toggle);
        _log?.Invoke($"[App] enqueue toggle from UI/hook, ok={ok}");
    }

    private void UpdateButtonVisual()
    {
        Form.SetVisualState(
            isPressed: _isHotkeyPressed,
            isRecording: _isRecording,
            isSticky: false);
    }

    private async Task<AppUserConfig> EnsureConfigAsync(CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        if (config is not null && HoldKeyPresetCatalog.IsSupported(config.HoldKeyVirtualKey))
        {
            config.HotwordConfig ??= new HotwordConfig();
            config.TranscriptionMode = NormalizeTranscriptionMode(config.TranscriptionMode);
            return config;
        }

        var preset = HoldKeyPresetCatalog.Default;
        var selected = new AppUserConfig
        {
            SchemaVersion = 2,
            HoldKeyName = preset.DisplayName,
            HoldKeyVirtualKey = preset.VirtualKey,
            EnableLiveCaption = true,
            EnableManualSemanticLlm = false,
            EnableStickyDictationSemantic = false,
            EnableSpeakerDiarization = false,
            HotwordConfig = new HotwordConfig(),
            TranscriptionMode = "fast",
            AlwaysOnTop = true
        };

        await _configStore.SaveAsync(selected, cancellationToken);
        return selected;
    }

    private void OpenSettingsDialog()
    {
        if (!_initialized)
        {
            MessageBox.Show(
                Form,
                "PressTalk 仍在启动，请稍后再试。",
                "PressTalk",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_config is null)
        {
            return;
        }

        if (_isRecording)
        {
            MessageBox.Show(
                "请先停止录音，再打开设置。",
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

        ApplyFormConfig(updated);
        _log?.Invoke(
            $"[App] settings updated, holdKey={updated.HoldKeyName}, hotwords={updated.HotwordConfig.GetNormalizedTerms().Count}, speaker={updated.EnableSpeakerDiarization}, mode={updated.TranscriptionMode}, topMost={updated.AlwaysOnTop}");

        BindHotkey(updated.HoldKeyVirtualKey);
        _configStore.SaveAsync(updated, CancellationToken.None).GetAwaiter().GetResult();
    }

    private void ApplyFormConfig(AppUserConfig config)
    {
        Form.SetHotkeyText(HoldKeyPresetCatalog.Resolve(config.HoldKeyVirtualKey).DisplayName);
        Form.SetTopMost(config.AlwaysOnTop);
        Form.Location = ResolveWindowLocation(config);
    }

    private Point ResolveWindowLocation(AppUserConfig config)
    {
        static Point ClampToArea(Point point, Rectangle workArea, Size formSize)
        {
            var maxX = Math.Max(workArea.Left, workArea.Right - formSize.Width);
            var maxY = Math.Max(workArea.Top, workArea.Bottom - formSize.Height);
            var clampedX = Math.Clamp(point.X, workArea.Left, maxX);
            var clampedY = Math.Clamp(point.Y, workArea.Top, maxY);
            return new Point(clampedX, clampedY);
        }

        if (config.FloatingWindowX is int x && config.FloatingWindowY is int y)
        {
            var target = new Point(x, y);
            var targetScreen = Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.Contains(target));
            var savedArea = targetScreen?.WorkingArea ?? (Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080));
            return ClampToArea(target, savedArea, Form.Size);
        }

        var defaultArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var fallback = new Point(defaultArea.Right - Form.Width - 32, Math.Max(defaultArea.Top + 24, defaultArea.Height / 4));
        return ClampToArea(fallback, defaultArea, Form.Size);
    }

    private void SaveWindowPosition()
    {
        if (_config is null)
        {
            return;
        }

        _config.FloatingWindowX = Form.Left;
        _config.FloatingWindowY = Form.Top;

        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _configStore.SaveAsync(_config, CancellationToken.None);
                    _log?.Invoke("[App] window position saved");
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[App] save window position failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[App] save window position dispatch failed: {ex.Message}");
        }
    }

    private void ShowInitFailureAndClose(Exception ex)
    {
        if (Form.IsDisposed)
        {
            return;
        }

        MessageBox.Show(
            Form,
            $"PressTalk 启动失败：\n\n{ex.Message}",
            "PressTalk",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        Form.Close();
    }

    private bool HasFlag(string flag)
    {
        return _args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    }

    private static FunAsrRuntimeOptions BuildFunAsrRuntimeOptions(string[] args, AppUserConfig config, Action<string> log)
    {
        var scriptOverride = ResolveOption(args, "--funasr-script=");
        var pythonOverride = ResolveOption(args, "--funasr-python=");
        var modelOverride = ResolveOption(args, "--funasr-model=");
        var speakerModelOverride = ResolveOption(args, "--funasr-speaker-model=");
        var realtimePuncModelOverride = ResolveOption(args, "--funasr-realtime-punc-model=");
        var finalPuncModelOverride = ResolveOption(args, "--funasr-final-punc-model=");
        var deviceOverride = ResolveOption(args, "--funasr-device=");
        var int8Override = ResolveOption(args, "--funasr-int8=");
        var strideOverride = ResolveOption(args, "--funasr-stride=");
        var endpointSilenceOverride = ResolveOption(args, "--funasr-endpoint-silence-ms=");
        var endpointRmsOverride = ResolveOption(args, "--funasr-endpoint-rms=");
        var modeOverride = ResolveOption(args, "--transcription-mode=");

        var pythonExecutable = pythonOverride
            ?? Environment.GetEnvironmentVariable("PRESSTALK_FUNASR_PYTHON")
            ?? "python";
        var configuredDevice = deviceOverride
            ?? Environment.GetEnvironmentVariable("PRESSTALK_FUNASR_DEVICE");
        var resolvedDevice = string.IsNullOrWhiteSpace(configuredDevice) || string.Equals(configuredDevice, "auto", StringComparison.OrdinalIgnoreCase)
            ? DetectPreferredDevice(pythonExecutable, log)
            : configuredDevice!;

        var int8ValueRaw = int8Override
            ?? Environment.GetEnvironmentVariable("PRESSTALK_FUNASR_INT8")
            ?? "0";
        var enableInt8 = int8ValueRaw is "1" or "true" or "True" or "TRUE";
        var strideRaw = strideOverride
            ?? Environment.GetEnvironmentVariable("PRESSTALK_FUNASR_STRIDE_SAMPLES");
        var strideSamples = int.TryParse(strideRaw, out var strideParsed)
            ? Math.Max(1600, strideParsed)
            : 9600;
        var endpointSilenceRaw = endpointSilenceOverride
            ?? Environment.GetEnvironmentVariable("PRESSTALK_FUNASR_ENDPOINT_SILENCE_MS");
        var endpointSilenceMs = int.TryParse(endpointSilenceRaw, out var endpointSilenceParsed)
            ? Math.Clamp(endpointSilenceParsed, 120, 1500)
            : 420;
        var endpointRmsRaw = endpointRmsOverride
            ?? Environment.GetEnvironmentVariable("PRESSTALK_FUNASR_ENDPOINT_RMS");
        var endpointRms = double.TryParse(
                endpointRmsRaw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var endpointRmsParsed)
            ? Math.Clamp(endpointRmsParsed, 0.001, 0.2)
            : 0.0065;
        var configuredMode = modeOverride
            ?? Environment.GetEnvironmentVariable("PRESSTALK_TRANSCRIPTION_MODE")
            ?? config.TranscriptionMode;
        var transcriptionMode = NormalizeTranscriptionMode(configuredMode);

        return new FunAsrRuntimeOptions
        {
            ScriptPath = scriptOverride
                ?? Environment.GetEnvironmentVariable("PRESSTALK_FUNASR_SCRIPT")
                ?? Path.Combine(AppContext.BaseDirectory, "funasr_runtime.py"),
            PythonExecutable = pythonExecutable,
            StreamingModel = modelOverride
                ?? Environment.GetEnvironmentVariable("PRESSTALK_FUNASR_MODEL")
                ?? "iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-online",
            SpeakerModel = speakerModelOverride
                ?? Environment.GetEnvironmentVariable("PRESSTALK_FUNASR_SPK_MODEL")
                ?? "iic/speech_campplus_sv_zh-cn_16k-common",
            RealtimePunctuationModel = realtimePuncModelOverride
                ?? Environment.GetEnvironmentVariable("PRESSTALK_FUNASR_REALTIME_PUNC_MODEL")
                ?? "iic/punc_ct-transformer_zh-cn-common-vad_realtime-vocab272727",
            FinalPunctuationModel = finalPuncModelOverride
                ?? Environment.GetEnvironmentVariable("PRESSTALK_FUNASR_FINAL_PUNC_MODEL")
                ?? "iic/punc_ct-transformer_zh-cn-common-vocab272727-pytorch",
            TranscriptionMode = transcriptionMode,
            Device = resolvedDevice,
            EnableInt8Quantization = enableInt8,
            StrideSamples = strideSamples,
            EndpointSilenceMs = endpointSilenceMs,
            EndpointRmsThreshold = endpointRms,
            CommandTimeoutMs = 180000
        };
    }

    private static string NormalizeTranscriptionMode(string? raw)
    {
        if (string.Equals(raw, "formatted", StringComparison.OrdinalIgnoreCase))
        {
            return "formatted";
        }

        return "fast";
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

    private bool IsGlobalHotkeyOccupied(uint virtualKey)
    {
        if (!NativeMethods.RegisterHotKey(IntPtr.Zero, NativeMethods.HOTKEY_ID_PROBE, NativeMethods.MOD_NONE, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NativeMethods.HOTKEY_ALREADY_REGISTERED)
            {
                return true;
            }

            _log?.Invoke($"[Hotkey.Hook] probe failed, vk=0x{virtualKey:X}, error={error}");
            return false;
        }

        NativeMethods.UnregisterHotKey(IntPtr.Zero, NativeMethods.HOTKEY_ID_PROBE);
        return false;
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
            EnableSpeakerDiarization = source.EnableSpeakerDiarization,
            HotwordConfig = new HotwordConfig
            {
                Terms = source.HotwordConfig.GetNormalizedTerms().ToList()
            },
            TranscriptionMode = NormalizeTranscriptionMode(source.TranscriptionMode),
            AlwaysOnTop = source.AlwaysOnTop,
            FloatingWindowX = source.FloatingWindowX,
            FloatingWindowY = source.FloatingWindowY
        };
    }

    private void ShowWarning(string message)
    {
        if (Form.IsDisposed)
        {
            return;
        }

        if (Form.InvokeRequired)
        {
            Form.BeginInvoke(new MethodInvoker(() => ShowWarning(message)));
            return;
        }

        MessageBox.Show(
            Form,
            message,
            "PressTalk",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }
}
