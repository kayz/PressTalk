using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PressTalk.App.Hotkey;

public sealed class GlobalHoldKeyHook : IDisposable
{
    private readonly uint _holdKeyVirtualKey;
    private readonly Action<string>? _log;
    private NativeMethods.LowLevelKeyboardProc? _callback;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _isPressed;
    private bool _stickyChordPressed;

    public GlobalHoldKeyHook(uint holdKeyVirtualKey, Action<string>? log = null)
    {
        _holdKeyVirtualKey = holdKeyVirtualKey;
        _log = log;
    }

    public event Action? HoldStarted;

    public event Action? HoldEnded;
    public event Action? StickyModeToggleRequested;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _callback = HookCallback;
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _callback,
            IntPtr.Zero,
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install keyboard hook.");
        }

        _log?.Invoke($"[Hotkey.Hook] started, vk=0x{_holdKeyVirtualKey:X}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
        var message = unchecked((uint)wParam.ToInt64());

        if (data.vkCode == NativeMethods.VK_SPACE && _isPressed)
        {
            if (message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN)
            {
                if (!_stickyChordPressed)
                {
                    _stickyChordPressed = true;
                    _log?.Invoke($"[Hotkey.Hook] sticky combo detected, holdVk=0x{_holdKeyVirtualKey:X}, comboVk=0x{data.vkCode:X}");
                    StickyModeToggleRequested?.Invoke();
                }

                return (IntPtr)1;
            }

            if (message == NativeMethods.WM_KEYUP || message == NativeMethods.WM_SYSKEYUP)
            {
                _stickyChordPressed = false;
                return (IntPtr)1;
            }

            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (data.vkCode != _holdKeyVirtualKey)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN)
        {
            if (!_isPressed)
            {
                _isPressed = true;
                _log?.Invoke($"[Hotkey.Hook] key down, vk=0x{data.vkCode:X}");
                HoldStarted?.Invoke();
            }

            return (IntPtr)1;
        }

        if (message == NativeMethods.WM_KEYUP || message == NativeMethods.WM_SYSKEYUP)
        {
            if (_isPressed)
            {
                _isPressed = false;
                _stickyChordPressed = false;
                _log?.Invoke($"[Hotkey.Hook] key up, vk=0x{data.vkCode:X}");
                HoldEnded?.Invoke();
            }

            return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _log?.Invoke("[Hotkey.Hook] stopped");
        _hookHandle = IntPtr.Zero;
        _callback = null;
    }
}
