using System.Runtime.InteropServices;

namespace WinIsland.Interop;

/// <summary>
/// Watches global left/right clicks. Used to collapse the island when the user
/// clicks anywhere outside of it without stealing keyboard focus.
/// </summary>
internal sealed class GlobalMouseHook : IDisposable
{
    private readonly Native.LowLevelMouseProc _callback;
    private IntPtr _hook;

    public GlobalMouseHook()
    {
        _callback = HookCallback;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _callback, Native.GetModuleHandle(null), 0);
    }

    public bool IsActive => _hook != IntPtr.Zero;

    public event Action<Native.POINT>? Clicked;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var message = wParam.ToInt32();
                if (message is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN)
                {
                    var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    Clicked?.Invoke(info.pt);
                }
            }
        }
        catch
        {
            // Never let an exception escape a system hook.
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
