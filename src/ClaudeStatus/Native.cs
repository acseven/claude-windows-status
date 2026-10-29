using System.Runtime.InteropServices;

namespace ClaudeStatus;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public enum Edge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

    const uint ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5;
    const int ABS_AUTOHIDE = 1;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    const uint GW_HWNDNEXT = 2;
    const int WS_EX_TOPMOST = 0x8, DWMWA_CLOAKED = 14;
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    public static IntPtr ShellTrayWindow() => FindWindow("Shell_TrayWnd", null);

    public sealed record Taskbar(RECT Bounds, Edge Edge, bool AutoHide, RECT? Tray);

    /// <summary>Primary taskbar position and the notification area (tray) inside it, in physical pixels.</summary>
    public static Taskbar? GetTaskbar()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == IntPtr.Zero) return null;
        var autoHide = ((long)SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0;

        RECT? tray = null;
        var shell = FindWindow("Shell_TrayWnd", null);
        var notify = shell == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(shell, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out var r) &&
            r.Width > 0 && r.Height > 0 && r.Width < data.rc.Width * 3 / 4)
            tray = r;

        return new Taskbar(data.rc, (Edge)data.uEdge, autoHide, tray);
    }

    /// <summary>True when the foreground window covers its whole monitor (games, videos, presentations).</summary>
    public static bool IsForegroundFullscreen()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow()) return false;

        var sb = new System.Text.StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        if (sb.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd") return false;

        if (!GetWindowRect(fg, out var w)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, 2 /* NEAREST */), ref mi)) return false;
        var m = mi.rcMonitor;
        return w.Left <= m.Left && w.Top <= m.Top && w.Right >= m.Right && w.Bottom >= m.Bottom;
    }

    /// <summary>
    /// True when another always-on-top window (a context menu, the hidden-icons tray, a flyout) is above
    /// <paramref name="self"/> and overlaps <paramref name="area"/>. Raising the widget then would draw it
    /// over that window. The taskbar itself does not count: it is re-ordered under the widget on purpose.
    /// </summary>
    public static bool IsCoveredByOtherTopmost(IntPtr self, RECT area)
    {
        var sb = new System.Text.StringBuilder(64);
        var guard = 0;
        for (var w = GetTopWindow(IntPtr.Zero); w != IntPtr.Zero && w != self && guard++ < 400; w = GetWindow(w, GW_HWNDNEXT))
        {
            if ((((long)GetWindowLongPtr(w, GWL_EXSTYLE)) & WS_EX_TOPMOST) == 0) break; // below here nothing is above us
            if (!IsWindowVisible(w)) continue;
            if (DwmGetWindowAttribute(w, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (!GetWindowRect(w, out var r) || r.Width <= 0 || r.Height <= 0) continue;
            if (r.Left >= area.Right || r.Right <= area.Left || r.Top >= area.Bottom || r.Bottom <= area.Top) continue;

            sb.Clear();
            GetClassName(w, sb, sb.Capacity);
            if (sb.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") continue;
            return true;
        }
        return false;
    }

    /// <summary>Hide from Alt-Tab and never take focus.</summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }
}
