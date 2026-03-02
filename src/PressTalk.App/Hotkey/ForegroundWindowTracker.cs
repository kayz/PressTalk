namespace PressTalk.App.Hotkey;

public sealed class ForegroundWindowTracker
{
    private readonly IntPtr _selfWindow = NativeMethods.GetConsoleWindow();
    private readonly Action<string>? _log;
    private IntPtr _lastExternalWindow = IntPtr.Zero;

    public ForegroundWindowTracker(Action<string>? log = null)
    {
        _log = log;
    }

    public void CaptureAtPress()
    {
        var current = NativeMethods.GetForegroundWindow();
        if (current == IntPtr.Zero)
        {
            _log?.Invoke("[WindowTracker] capture skipped: foreground is null");
            return;
        }

        if (current == _selfWindow)
        {
            _log?.Invoke("[WindowTracker] capture skipped: foreground is console window");
            return;
        }

        if (!NativeMethods.IsWindow(current))
        {
            _log?.Invoke("[WindowTracker] capture skipped: foreground is invalid");
            return;
        }

        _lastExternalWindow = current;
        _log?.Invoke($"[WindowTracker] captured={DescribeWindow(_lastExternalWindow)}");
    }

    public bool TryActivateCapturedWindow()
    {
        if (_lastExternalWindow == IntPtr.Zero)
        {
            _log?.Invoke("[WindowTracker] activate skipped: no captured window");
            return false;
        }

        if (!NativeMethods.IsWindow(_lastExternalWindow))
        {
            _log?.Invoke("[WindowTracker] activate failed: captured window no longer exists");
            _lastExternalWindow = IntPtr.Zero;
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == _lastExternalWindow)
        {
            _log?.Invoke($"[WindowTracker] activate not needed: already foreground={DescribeWindow(_lastExternalWindow)}");
            return true;
        }

        var ok = NativeMethods.SetForegroundWindow(_lastExternalWindow);
        _log?.Invoke($"[WindowTracker] activate {(ok ? "ok" : "failed")}: target={DescribeWindow(_lastExternalWindow)} current={DescribeWindow(NativeMethods.GetForegroundWindow())}");
        return ok;
    }

    private static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "0x0";
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        var title = NativeMethods.ReadWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title))
        {
            return $"0x{hwnd.ToInt64():X}, pid={pid}";
        }

        return $"0x{hwnd.ToInt64():X}, pid={pid}, title='{title}'";
    }
}
