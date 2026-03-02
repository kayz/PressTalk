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

    public SendInputTextCommitter(Action<string>? log = null)
    {
        _log = log;
    }

    public Task CommitAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            _log?.Invoke("[Commit.SendInput] skip empty text");
            return Task.CompletedTask;
        }

        var foreground = GetForegroundWindow();
        _log?.Invoke($"[Commit.SendInput] start, chars={text.Length}, foreground={DescribeWindow(foreground)}");

        var inputSize = Marshal.SizeOf<Input>();
        uint totalSent = 0;

        foreach (var c in text)
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

        _log?.Invoke($"[Commit.SendInput] done, totalInputsSent={totalSent}, expected={text.Length * 2}");

        return Task.CompletedTask;
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
