using System.Diagnostics;
using System.Runtime.InteropServices;
using PressTalk.Contracts.Normalize;

namespace PressTalk.Normalize;

public sealed class AdaptiveSemanticNormalizer : ITextNormalizer
{
    private const double MaxMemoryLoadPercent = 88;
    private const ulong MinAvailPhysicalBytes = 1024UL * 1024UL * 1024UL;
    private const int SlowNormalizeThresholdMs = 1200;
    private const int MinSemanticInputChars = 16;
    private const int MinStickyDictationSemanticChars = 80;

    private readonly ITextNormalizer _ruleNormalizer;
    private readonly ITextNormalizer _semanticNormalizer;
    private readonly Action<string>? _log;

    public AdaptiveSemanticNormalizer(
        ITextNormalizer ruleNormalizer,
        ITextNormalizer semanticNormalizer,
        Action<string>? log = null)
    {
        _ruleNormalizer = ruleNormalizer;
        _semanticNormalizer = semanticNormalizer;
        _log = log;
    }

    public async Task<string> NormalizeAsync(
        string rawText,
        string languageHint,
        TextNormalizationOptions options,
        CancellationToken cancellationToken)
    {
        var ruleOutput = await _ruleNormalizer.NormalizeAsync(rawText, languageHint, options, cancellationToken);
        if (!options.EnableSemantic)
        {
            _log?.Invoke("[Normalize.Adaptive] semantic skipped by options");
            return ruleOutput;
        }

        var minimumChars = string.Equals(options.Scenario, "sticky-dictation", StringComparison.Ordinal)
            ? MinStickyDictationSemanticChars
            : MinSemanticInputChars;
        if (ruleOutput.Length < minimumChars)
        {
            _log?.Invoke($"[Normalize.Adaptive] semantic skipped, textLen={ruleOutput.Length} < {minimumChars}, scenario={options.Scenario}");
            return ruleOutput;
        }

        if (IsSystemUnderPressure(out var pressureReason))
        {
            _log?.Invoke($"[Normalize.Adaptive] semantic skipped by pressure: {pressureReason}");
            return ruleOutput;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var semanticOutput = await _semanticNormalizer.NormalizeAsync(ruleOutput, languageHint, options, cancellationToken);
            stopwatch.Stop();

            var elapsedMs = stopwatch.ElapsedMilliseconds;
            var charsPerSecond = elapsedMs <= 0
                ? 0d
                : ruleOutput.Length / (elapsedMs / 1000d);
            _log?.Invoke(
                $"[Normalize.Adaptive] semantic latency, scenario={options.Scenario}, textLen={ruleOutput.Length}, elapsedMs={elapsedMs}, charsPerSec={charsPerSecond:F1}");

            if (elapsedMs >= SlowNormalizeThresholdMs)
            {
                _log?.Invoke($"[Normalize.Adaptive] semantic slow, elapsedMs={elapsedMs}");
            }

            return string.IsNullOrWhiteSpace(semanticOutput) ? ruleOutput : semanticOutput.Trim();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Normalize.Adaptive] semantic failed, fallback to rule output, error: {ex.Message}");
            return ruleOutput;
        }
    }

    private static bool IsSystemUnderPressure(out string reason)
    {
        if (!TryReadMemoryStatus(out var memStatus))
        {
            reason = "memory status unavailable";
            return false;
        }

        if (memStatus.dwMemoryLoad >= MaxMemoryLoadPercent)
        {
            reason = $"memory load={memStatus.dwMemoryLoad}%";
            return true;
        }

        if (memStatus.ullAvailPhys <= MinAvailPhysicalBytes)
        {
            reason = $"available physical={memStatus.ullAvailPhys / (1024 * 1024)}MB";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool TryReadMemoryStatus(out MEMORYSTATUSEX status)
    {
        status = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };

        return GlobalMemoryStatusEx(ref status);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
