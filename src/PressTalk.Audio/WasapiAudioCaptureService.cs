using NAudio.CoreAudioApi;
using NAudio.Wave;
using PressTalk.Contracts.Audio;

namespace PressTalk.Audio;

public sealed class WasapiAudioCaptureService : IAudioCaptureService
{
    private const double GainBoostDb = 3.0;

    private readonly object _sync = new();
    private readonly Action<string>? _log;
    private readonly string _debugAudioRoot;
    private readonly float _gainFactor;

    private string? _activeSessionId;
    private DateTimeOffset _startedAt;
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private TaskCompletionSource<Exception?>? _stopTcs;
    private List<byte>? _buffer;
    private WaveFormat? _waveFormat;

    public WasapiAudioCaptureService(Action<string>? log = null)
    {
        _log = log;
        _debugAudioRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PressTalk",
            "debug-audio");

        _gainFactor = (float)Math.Pow(10.0, GainBoostDb / 20.0);
        _log?.Invoke($"[Audio.Wasapi] initialized, gainFactor={_gainFactor:F3}, debugAudioRoot='{_debugAudioRoot}'");
    }

    public Task StartCaptureAsync(string sessionId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_activeSessionId is not null)
            {
                throw new InvalidOperationException("Capture already started.");
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _device = SelectPreferredCaptureDevice(enumerator);
                _capture = new WasapiCapture(_device);
                _waveFormat = _capture.WaveFormat;
                _buffer = [];
                _stopTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _activeSessionId = sessionId;
                _startedAt = DateTimeOffset.UtcNow;

                _capture.DataAvailable += CaptureOnDataAvailable;
                _capture.RecordingStopped += CaptureOnRecordingStopped;

                _log?.Invoke(
                    $"[Audio.Wasapi] start capture, session={sessionId}, device='{_device.FriendlyName}', format={_waveFormat.Encoding}, rate={_waveFormat.SampleRate}, channels={_waveFormat.Channels}, bits={_waveFormat.BitsPerSample}, gainDb={GainBoostDb:F1}");

                _capture.StartRecording();
                _log?.Invoke($"[Audio.Wasapi] recording started, session={sessionId}, deviceId='{_device.ID}'");
            }
            catch (Exception ex)
            {
                CleanupLocked();
                _log?.Invoke($"[Audio.Wasapi] start failed, session={sessionId}, error={ex.Message}");
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public async Task<AudioCaptureResult> StopCaptureAsync(string sessionId, CancellationToken cancellationToken)
    {
        WasapiCapture? capture;
        TaskCompletionSource<Exception?>? stopTcs;
        List<byte>? bytes;
        WaveFormat? format;
        DateTimeOffset startedAt;

        lock (_sync)
        {
            if (_activeSessionId is null || _activeSessionId != sessionId)
            {
                throw new InvalidOperationException("Capture session mismatch.");
            }

            capture = _capture;
            stopTcs = _stopTcs;
            bytes = _buffer;
            format = _waveFormat;
            startedAt = _startedAt;

            if (capture is null || stopTcs is null || bytes is null || format is null)
            {
                throw new InvalidOperationException("Capture state is invalid.");
            }

            _log?.Invoke($"[Audio.Wasapi] stop requested, session={sessionId}, bufferedBytes={bytes.Count}");
            capture.StopRecording();
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            var error = await stopTcs!.Task.WaitAsync(timeoutCts.Token);
            if (error is not null)
            {
                throw new InvalidOperationException("Recording stopped with error.", error);
            }
            _log?.Invoke($"[Audio.Wasapi] stop signal received, session={sessionId}");

            byte[] rawBytes;
            WaveFormat waveFormat;
            TimeSpan duration;

            lock (_sync)
            {
                var localBytes = bytes!;
                var localFormat = format!;
                rawBytes = localBytes.ToArray();
                waveFormat = localFormat;
                duration = DateTimeOffset.UtcNow - startedAt;
                CleanupLocked();
            }

            var monoSamples = ConvertToMonoFloatSamples(rawBytes, waveFormat, _gainFactor, out var peak);
            var sampleRate = waveFormat.SampleRate;
            var byteCount = rawBytes.Length;

            PersistWav(sessionId, rawBytes, waveFormat);

            _log?.Invoke(
                $"[Audio.Wasapi] stop complete, session={sessionId}, bytes={byteCount}, monoSamples={monoSamples.Length}, sampleRate={sampleRate}, durationMs={duration.TotalMilliseconds:F1}, peak={peak:F3}");

            return new AudioCaptureResult(monoSamples, sampleRate, duration);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                CleanupLocked();
            }

            _log?.Invoke($"[Audio.Wasapi] stop failed, session={sessionId}, error={ex.Message}");
            throw;
        }
    }

    public void ForceStop()
    {
        lock (_sync)
        {
            if (_capture is not null)
            {
                try
                {
                    _capture.StopRecording();
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[Audio.Wasapi] force stop failed: {ex.Message}");
                }
            }

            CleanupLocked();
            _log?.Invoke("[Audio.Wasapi] force cleanup completed");
        }
    }

    public bool TryGetLiveSnapshot(TimeSpan tailWindow, out AudioCaptureResult snapshot)
    {
        lock (_sync)
        {
            if (_activeSessionId is null || _buffer is null || _waveFormat is null)
            {
                snapshot = new AudioCaptureResult(ReadOnlyMemory<float>.Empty, 0, TimeSpan.Zero);
                return false;
            }

            var format = _waveFormat;
            var bytes = _buffer;

            var frameCount = bytes.Count / format.BlockAlign;
            var maxFrames = Math.Max(1, (int)Math.Round(format.SampleRate * tailWindow.TotalSeconds));
            var startFrame = Math.Max(0, frameCount - maxFrames);
            var startOffset = startFrame * format.BlockAlign;

            var chunkLength = bytes.Count - startOffset;
            if (chunkLength <= 0)
            {
                snapshot = new AudioCaptureResult(ReadOnlyMemory<float>.Empty, format.SampleRate, TimeSpan.Zero);
                return false;
            }

            var chunk = new byte[chunkLength];
            bytes.CopyTo(startOffset, chunk, 0, chunkLength);

            var samples = ConvertToMonoFloatSamples(chunk, format, _gainFactor, out _);
            snapshot = new AudioCaptureResult(samples, format.SampleRate, DateTimeOffset.UtcNow - _startedAt);
            return samples.Length > 0;
        }
    }

    private void CaptureOnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_sync)
        {
            if (_buffer is null || e.BytesRecorded <= 0)
            {
                return;
            }

            _buffer.AddRange(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
        }
    }

    private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            _log?.Invoke($"[Audio.Wasapi] recording stopped event, hasError={e.Exception is not null}");
            _stopTcs?.TrySetResult(e.Exception);
        }
    }

    private void PersistWav(string sessionId, IReadOnlyList<byte> bytes, WaveFormat format)
    {
        try
        {
            Directory.CreateDirectory(_debugAudioRoot);
            var path = Path.Combine(_debugAudioRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{sessionId}.wav");
            using var writer = new WaveFileWriter(path, format);
            writer.Write(bytes.ToArray(), 0, bytes.Count);
            writer.Flush();
            _log?.Invoke($"[Audio.Wasapi] wav saved, path={path}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Audio.Wasapi] wav save failed: {ex.Message}");
        }
    }

    private MMDevice SelectPreferredCaptureDevice(MMDeviceEnumerator enumerator)
    {
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .ToArray();

        if (devices.Length == 0)
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }

        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

        var selected = devices
            .Select(d => new { Device = d, Score = ScoreDevice(d, defaultDevice.ID) })
            .OrderByDescending(x => x.Score)
            .First();

        foreach (var candidate in devices.Select(d => new { Device = d, Score = ScoreDevice(d, defaultDevice.ID) }).OrderByDescending(x => x.Score))
        {
            _log?.Invoke($"[Audio.Wasapi] device candidate='{candidate.Device.FriendlyName}', id='{candidate.Device.ID}', score={candidate.Score}");
        }

        _log?.Invoke($"[Audio.Wasapi] device selected='{selected.Device.FriendlyName}', score={selected.Score}, default='{defaultDevice.FriendlyName}'");

        return selected.Device;
    }

    private static int ScoreDevice(MMDevice device, string defaultDeviceId)
    {
        var name = device.FriendlyName.ToLowerInvariant();
        var score = device.ID == defaultDeviceId ? 30 : 0;

        if (name.Contains("headset") || name.Contains("headphone") || name.Contains("boom") || name.Contains("lavalier") || name.Contains("earbud") || name.Contains("\u8033\u673a"))
        {
            score += 85;
        }

        if (name.Contains("microphone") || name.Contains("mic") || name.Contains("\u9ea6\u514b\u98ce"))
        {
            score += 40;
        }

        if (name.Contains("usb") || name.Contains("bluetooth") || name.Contains("\u84dd\u7259"))
        {
            score += 20;
        }

        if (name.Contains("array") || name.Contains("\u9635\u5217"))
        {
            score -= 12;
        }

        if (name.Contains("stereo mix") || name.Contains("\u6df7\u97f3"))
        {
            score -= 200;
        }

        return score;
    }

    private static float[] ConvertToMonoFloatSamples(
        IReadOnlyList<byte> bytes,
        WaveFormat format,
        float gainFactor,
        out double peak)
    {
        if (bytes.Count == 0)
        {
            peak = 0;
            return [];
        }

        var channels = Math.Max(format.Channels, 1);
        var blockAlign = format.BlockAlign;
        if (blockAlign <= 0)
        {
            throw new NotSupportedException($"Unsupported block align: {blockAlign}");
        }

        var frameCount = bytes.Count / blockAlign;
        var mono = new float[frameCount];
        peak = 0;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var frameOffset = frame * blockAlign;
            float sum = 0;

            for (var ch = 0; ch < channels; ch++)
            {
                var sampleOffset = frameOffset + ch * (format.BitsPerSample / 8);
                var value = ReadSample(bytes, sampleOffset, format);
                sum += value;
            }

            var avg = sum / channels;
            var boosted = Math.Clamp(avg * gainFactor, -1f, 1f);

            mono[frame] = boosted;
            peak = Math.Max(peak, Math.Abs(boosted));
        }

        return mono;
    }

    private static float ReadSample(IReadOnlyList<byte> bytes, int offset, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var sample = bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24);
            return BitConverter.Int32BitsToSingle(sample);
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var sample = (short)(bytes[offset] | (bytes[offset + 1] << 8));
            return sample / 32768f;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 24)
        {
            var sample = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
            if ((sample & 0x800000) != 0)
            {
                sample |= unchecked((int)0xFF000000);
            }

            return sample / 8388608f;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32)
        {
            var sample = bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24);
            return sample / 2147483648f;
        }

        throw new NotSupportedException(
            $"Unsupported wave format: encoding={format.Encoding}, bits={format.BitsPerSample}");
    }

    private void CleanupLocked()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= CaptureOnDataAvailable;
            _capture.RecordingStopped -= CaptureOnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;

        _activeSessionId = null;
        _waveFormat = null;
        _buffer = null;
        _stopTcs = null;
    }
}
