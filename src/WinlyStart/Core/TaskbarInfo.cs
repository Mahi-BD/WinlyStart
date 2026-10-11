using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WinlyStart.Core;

internal static partial class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);
}

/// <summary>Where the taskbar is, where the Start button is, and how big a pixel is.</summary>
internal static class TaskbarInfo
{
    // boxed so the hook thread never sees a half-written rectangle while the pool thread updates it
    private sealed record Box(Native.RECT R);
    private static Box? _startButton;
    private static long _startButtonStamp;
    private static int _refreshing;

    /// <summary>Primary taskbar rectangle (pixels) and edge (ABE_*).</summary>
    public static (Native.RECT rect, uint edge) GetTaskbar()
    {
        var abd = new Native.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>() };
        if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != UIntPtr.Zero && abd.rc.Width > 0)
            return (abd.rc, abd.uEdge);

        var tray = Native.FindWindowW("Shell_TrayWnd", null);
        Native.GetWindowRect(tray, out var r);
        var mon = MonitorInfo(Native.MonitorFromWindow(tray, Native.MONITOR_DEFAULTTONEAREST));
        uint edge = r.Width >= r.Height
            ? (r.Top <= mon.rcMonitor.Top ? Native.ABE_TOP : Native.ABE_BOTTOM)
            : (r.Left <= mon.rcMonitor.Left ? Native.ABE_LEFT : Native.ABE_RIGHT);
        return (r, edge);
    }

    public static Native.MONITORINFO MonitorInfo(IntPtr hMonitor)
    {
        var mi = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfoW(hMonitor, ref mi);
        return mi;
    }

    /// <summary>Cached Start button rectangle (pixels). Cheap enough to call from a low-level hook.</summary>
    public static Native.RECT? StartButton
    {
        get
        {
            // the taskbar re-centres whenever an app opens or closes, so keep this fresh
            if (Environment.TickCount64 - _startButtonStamp > 2_000) RefreshStartButtonAsync();
            return _startButton?.R;
        }
    }

    /// <summary>UI Automation is slow-ish (cross-process), so the lookup runs off the UI/hook thread.</summary>
    public static void RefreshStartButtonAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var found = FindStartButton();
                _startButton = found is { } r ? new Box(r) : null;
                _startButtonStamp = Environment.TickCount64;
                if (StartHook.DebugEnabled)
                    StartHook.Debug($"StartButton refresh → {(found is { } f ? $"{f.Left},{f.Top} {f.Width}x{f.Height}" : "null")}");
            }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    private static Native.RECT? FindStartButton()
    {
        var tray = Native.FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return null;

        // Windows 10: the Start button is a real window.
        var legacy = Native.FindWindowExW(tray, IntPtr.Zero, "Start", null);
        if (legacy != IntPtr.Zero && Native.GetWindowRect(legacy, out var lr) && lr.Width > 0) return lr;

        // Windows 11: it lives inside the XAML taskbar; UI Automation exposes it as "StartButton".
        try
        {
            var root = AutomationElement.FromHandle(tray);
            var el = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
            if (el == null) return null;
            var b = el.Current.BoundingRectangle;
            if (b.IsEmpty || b.Width <= 0) return null;
            return new Native.RECT { Left = (int)b.Left, Top = (int)b.Top, Right = (int)b.Right, Bottom = (int)b.Bottom };
        }
        catch { return null; }
    }
}
