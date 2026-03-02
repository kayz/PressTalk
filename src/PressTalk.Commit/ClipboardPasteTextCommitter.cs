using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using PressTalk.Contracts.Commit;

namespace PressTalk.Commit;

public sealed class ClipboardPasteTextCommitter : ITextCommitter
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int VkControl = 0x11;
    private const int VkV = 0x56;
    private const int InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;

    private readonly Action<string>? _log;

    public ClipboardPasteTextCommitter(Action<string>? log = null)
    {
        _log = log;
    }

    public Task CommitAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            _log?.Invoke("[Commit.Clipboard] skip empty text");
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _log?.Invoke($"[Commit.Clipboard] start, chars={text.Length}");

        SetClipboardText(text);
        _log?.Invoke("[Commit.Clipboard] clipboard updated");

        SendCtrlV();
        _log?.Invoke("[Commit.Clipboard] Ctrl+V sent");

        return Task.CompletedTask;
    }

    private static void SetClipboardText(string text)
    {
        // Retry because clipboard can be temporarily locked by other processes.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (TrySetClipboardText(text))
            {
                return;
            }

            Thread.Sleep(30);
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set clipboard text.");
    }

    private static bool TrySetClipboardText(string text)
    {
        IntPtr hGlobal = IntPtr.Zero;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
            if (hGlobal == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalAlloc failed.");
            }

            var locked = GlobalLock(hGlobal);
            if (locked == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalLock failed.");
            }

            try
            {
                Marshal.Copy(bytes, 0, locked, bytes.Length);
            }
            finally
            {
                _ = GlobalUnlock(hGlobal);
            }

            if (!OpenClipboard(IntPtr.Zero))
            {
                return false;
            }

            try
            {
                if (!EmptyClipboard())
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed.");
                }

                var setResult = SetClipboardData(CfUnicodeText, hGlobal);
                if (setResult == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetClipboardData failed.");
                }

                // Ownership transferred to clipboard.
                hGlobal = IntPtr.Zero;
            }
            finally
            {
                _ = CloseClipboard();
            }

            return true;
        }
        finally
        {
            if (hGlobal != IntPtr.Zero)
            {
                _ = GlobalFree(hGlobal);
            }
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            KeyboardInput((ushort)VkControl, 0),
            KeyboardInput((ushort)VkV, 0),
            KeyboardInput((ushort)VkV, KeyeventfKeyup),
            KeyboardInput((ushort)VkControl, KeyeventfKeyup)
        };

        var inputSize = Marshal.SizeOf<Input>();
        var sent = SendInput((uint)inputs.Length, inputs, inputSize);
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SendInput(Ctrl+V) failed. sent={sent}, expected={inputs.Length}, cbSize={inputSize}");
        }
    }

    private static Input KeyboardInput(ushort vk, uint flags)
    {
        return new Input
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KeybdInput
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, Input[] pInputs, int cbSize);

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
