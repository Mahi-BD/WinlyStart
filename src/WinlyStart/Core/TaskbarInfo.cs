using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WinlyStart.Core;

internal static partial class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);
}

/// <summary>Where the taskbars are, where their Start buttons are, and how big a pixel is.</summary>
internal static class TaskbarInfo
{
    /// <summary>One taskbar — the primary <c>Shell_TrayWnd</c> or a <c>Shell_SecondaryTrayWnd</c> (one per
    /// extra monitor when "show my taskbar on all displays" is on) — with its rectangle and Start button
    /// in pixels. Immutable, and the array is swapped whole, so the hook thread never sees a half-written
    /// snapshot while the pool thread refreshes it.</summary>
    public sealed record Bar(IntPtr Hwnd, bool Primary, Native.RECT Rect, Native.RECT? StartButton);

    private static Bar[] _bars = Array.Empty<Bar>();
    private static long _stamp;
    private static int _refreshing;

    /// <summary>Primary taskbar rectangle (pixels) and edge (ABE_*). Every taskbar sits on the same edge.</summary>
    public static (Native.RECT rect, uint edge) GetTaskbar()
    {
        var abd = new Native.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>() };
        if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != UIntPtr.Zero && abd.rc.Width > 0)
            return (abd.rc, abd.uEdge);

        var tray = Native.FindWindowW("Shell_TrayWnd", null);
        Native.GetWindowRect(tray, out var r);
        var mon = MonitorInfo(Native.MonitorFromWindow(tray, Native.MONITOR_DEFAULTTONEAREST)).rcMonitor;
        uint edge = r.Width >= r.Height
            ? (r.Top <= mon.Top ? Native.ABE_TOP : Native.ABE_BOTTOM)
            : (r.Left <= mon.Left ? Native.ABE_LEFT : Native.ABE_RIGHT);
        return (r, edge);
    }

    public static Native.MONITORINFO MonitorInfo(IntPtr hMonitor)
    {
        var mi = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfoW(hMonitor, ref mi);
        return mi;
    }

    /// <summary>Effective DPI of a monitor (96 = 100 %).</summary>
    public static uint Dpi(IntPtr hMonitor) =>
        Native.GetDpiForMonitor(hMonitor, 0, out uint x, out _) == 0 && x > 0 ? x : 96;

    /// <summary>Cached snapshot of every taskbar. Cheap enough to call from a low-level hook.</summary>
    public static Bar[] Bars
    {
        get
        {
            // the taskbar re-centres whenever an app opens or closes, so keep this fresh
            if (Environment.TickCount64 - _stamp > 2_000) RefreshAsync();
            return _bars;
        }
    }

    /// <summary>The taskbar whose Start button, grown by <paramref name="slack"/> pixels, contains the point.</summary>
    public static Bar? StartButtonAt(int x, int y, int slack = 0)
    {
        foreach (var b in Bars)
            if (b.StartButton is { } r && x >= r.Left - slack && x < r.Right + slack && y >= r.Top - slack && y < r.Bottom + slack) return b;
        return null;
    }

    public static bool OnTaskbar(int x, int y)
    {
        foreach (var b in Bars) if (b.Rect.Contains(x, y)) return true;
        return false;
    }

    /// <summary>The taskbar on the monitor that holds the point, else the primary one: a monitor without a
    /// taskbar ("show my taskbar on all displays" off) opens the menu where the taskbar is, like Windows.</summary>
    public static Bar? BarFor(Native.POINT pt)
    {
        var mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        Bar? primary = null;
        foreach (var b in Bars)
        {
            if (b.Primary) primary = b;
            if (Native.MonitorFromWindow(b.Hwnd, Native.MONITOR_DEFAULTTONEAREST) == mon) return b;
        }
        return primary;
    }

    /// <summary>UI Automation is slow-ish (cross-process), so the lookup runs off the UI/hook thread.</summary>
    public static void RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var bars = FindAll();
                _bars = bars;
                _stamp = Environment.TickCount64;
                if (StartHook.DebugEnabled)
                    StartHook.Debug("taskbars → " + string.Join(" | ", bars.Select(b =>
                        $"{(b.Primary ? "primary" : "secondary")} {b.Rect.Left},{b.Rect.Top} {b.Rect.Width}x{b.Rect.Height} start={(b.StartButton is { } s ? $"{s.Left},{s.Top} {s.Width}x{s.Height}" : "null")}")));
            }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    private static Bar[] FindAll()
    {
        var list = new List<Bar>(2);
        var tray = Native.FindWindowW("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero) list.Add(Describe(tray, primary: true));
        for (var h = Native.FindWindowExW(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); h != IntPtr.Zero;
             h = Native.FindWindowExW(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null))
            list.Add(Describe(h, primary: false));
        return list.ToArray();
    }

    private static Bar Describe(IntPtr tray, bool primary)
    {
        Native.GetWindowRect(tray, out var r);
        return new Bar(tray, primary, r, FindStartButton(tray));
    }

    private static Native.RECT? FindStartButton(IntPtr tray)
    {
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
