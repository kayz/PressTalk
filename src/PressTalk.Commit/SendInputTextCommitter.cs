using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PressTalk.Contracts.Commit;

namespace PressTalk.Commit;

public sealed class SendInputTextCommitter : ITextCommitter
{
    private const int InputKeyboard = 1;
    private const uint KeyeventfUnicode = 0x0004;
    private const uint KeyeventfKeyup = 0x0002;

    private readonly Action<string>? _log;
    private readonly object _sync = new();
    private string _lastCommittedText = string.Empty;

    public SendInputTextCommitter(Action<string>? log = null)
    {
        _log = log;
    }

    public Task CommitAsync(string text, CancellationToken cancellationToken)
    {
        ResetIncrementalState();
        return CommitIncrementalAsync(text, isFinal: true, cancellationToken);
    }

    public Task CommitIncrementalAsync(
        string confirmedText,
        bool isFinal,
        CancellationToken cancellationToken)
    {
        var delta = ResolveDeltaAndAdvance(confirmedText, isFinal);
        if (string.IsNullOrEmpty(delta))
        {
            _log?.Invoke("[Commit.SendInput] skip empty incremental delta");
            return Task.CompletedTask;
        }

        var foreground = GetForegroundWindow();
        _log?.Invoke($"[Commit.SendInput] start incremental commit, deltaChars={delta.Length}, isFinal={isFinal}, foreground={DescribeWindow(foreground)}");

        var inputSize = Marshal.SizeOf<Input>();
        uint totalSent = 0;

        foreach (var c in delta)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inputs = new[]
            {
                new Input
                {
                    type = InputKeyboard,
                    U = new InputUnion
                    {
                        ki = new KeybdInput
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = KeyeventfUnicode,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                },
                new Input
                {
                    type = InputKeyboard,
                    U = new InputUnion
                    {
                        ki = new KeybdInput
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = KeyeventfUnicode | KeyeventfKeyup,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                }
            };

            var sent = SendInput((uint)inputs.Length, inputs, inputSize);
            if (sent != inputs.Length)
            {
                var errorCode = Marshal.GetLastWin32Error();
                throw new Win32Exception(errorCode, $"SendInput failed. sent={sent}, expected={inputs.Length}, cbSize={inputSize}");
            }

            totalSent += sent;
        }

        _log?.Invoke($"[Commit.SendInput] done, totalInputsSent={totalSent}, expected={delta.Length * 2}");

        return Task.CompletedTask;
    }

    public void ResetIncrementalState()
    {
        lock (_sync)
        {
            _lastCommittedText = string.Empty;
        }
    }

    private string ResolveDeltaAndAdvance(string confirmedText, bool isFinal)
    {
        var current = confirmedText ?? string.Empty;
        lock (_sync)
        {
            if (!current.StartsWith(_lastCommittedText, StringComparison.Ordinal))
            {
                _lastCommittedText = string.Empty;
            }

            var delta = current.Length > _lastCommittedText.Length
                ? current[_lastCommittedText.Length..]
                : string.Empty;

            _lastCommittedText = isFinal ? string.Empty : current;
            return delta;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "0x0";
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            var process = Process.GetProcessById((int)pid);
            return $"0x{hwnd.ToInt64():X}, pid={pid}, proc={process.ProcessName}";
        }
        catch
        {
            return $"0x{hwnd.ToInt64():X}, pid={pid}";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeybdInput ki;

        [FieldOffset(0)]
        public MouseInput mi;

        [FieldOffset(0)]
        public HardwareInput hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
