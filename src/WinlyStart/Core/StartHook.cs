using System.Runtime.InteropServices;
using System.Windows;

namespace WinlyStart.Core;

/// <summary>
/// The "replacement" — three user-mode, fully revertible hooks:
///  1. keyboard: a bare Windows-key tap (or Ctrl+Esc) toggles Winly Start instead of the Windows 11 menu,
///  2. mouse: a left-click on the taskbar's Start button toggles Winly Start; any other click outside
///     the menu light-dismisses it (like the real Start menu — independent of window activation),
///  3. foreground WinEvent: if the Windows 11 Start menu still appears (touch, elevated window focused),
///     Winly Start takes over and the Windows menu light-dismisses.
/// The hooks live on their own thread with its own message loop. Windows delivers low-level hook
/// callbacks to the thread that installed them and gives it only a few hundred milliseconds to answer;
/// on the UI thread a busy layout pass or a slow app launch used to miss that deadline, the key fell
/// through to the Windows 11 menu, and after a few misses Windows silently removed the hook for good.
/// A watchdog re-installs the hooks if input keeps flowing while they stay silent.
/// Nothing is injected anywhere; unhooking (or closing the app) restores Windows completely.
/// </summary>
public sealed class StartHook : IDisposable
{
    private static readonly UIntPtr Magic = new(0x52535452); // "RSTR" tags our own injected key
    // Masking key used to turn a lone Win tap into a "chord" so the shell does not open its own menu.
    // Must be a key the shell actually counts: the unassigned 0xE8 is ignored on Windows 11 build 26200,
    // whereas VK_CONTROL is always counted and, since only lone taps reach this path, injecting it has no
    // visible effect and cannot corrupt a real Win+X combo.
    private const ushort MaskVk = Native.VK_CONTROL;
    private const uint MsgWatchdog = Native.WM_APP + 10;

    // Full-screen remote-control clients forward the Windows key to the far side (mstsc: "Apply Windows
    // key combinations: only when using the full screen"). While one of these owns the screen we stay
    // out of the way, otherwise the remote Start menu and ours would open together.
    private static readonly string[] RemoteClients =
    {
        "mstsc", "msrdc", "rdclient", "vmconnect", "windowssandbox", "virtualbox", "vmware", "vmplayer",
        "wfica", "cdviewer", "anydesk", "teamviewer", "rustdesk", "rdesk", "parsec", "moonlight",
    };

    private readonly Native.HookProc _kbProc;
    private readonly Native.HookProc _mouseProc;
    private readonly Native.WinEventProc _winEventProc;
    private readonly Native.EnumWindowsProc _enumProc;
    private readonly uint _uiThread;
    private IntPtr _kbHook, _mouseHook, _winEventHook;
    private Thread? _thread;
    private uint _threadId;
    private Timer? _watchdog;
    private int _lastHookTick, _lastReinstall;

    private bool _winDown, _otherKey, _passThrough, _swallowUp;
    private long _lastWinDown;
    private readonly Dictionary<uint, bool> _isStartHost = new();
    private readonly Dictionary<uint, bool> _isRemoteClient = new();
    private Native.POINT _probe;
    private bool _probeHit;

    public bool WinKeyEnabled { get; set; } = true;
    public bool StartButtonEnabled { get; set; } = true;
    public bool FallbackEnabled { get; set; } = true;

    /// <summary>Set by the menu window: true from the moment it opens until a hide starts.</summary>
    public static volatile bool MenuVisible;

    /// <summary>Raised on the UI thread. Toggle = open/close, Show = make sure it is open,
    /// Dismiss = the user clicked outside the menu.</summary>
    public event Action? Toggle;
    public event Action? Show;
    public event Action? Dismiss;

    public StartHook()
    {
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
        _winEventProc = WinEventProc;
        _enumProc = EnumWindow;
        _uiThread = Native.GetCurrentThreadId();   // constructed on the UI thread; its windows are "ours"
    }

    public void Install()
    {
        if (_thread is { IsAlive: true }) return;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => HookThread(ready)) { IsBackground = true, Name = "WinlyStart.Hooks" };
        _thread.Start();
        ready.Wait(2000);
    }

    private void HookThread(ManualResetEventSlim ready)
    {
        _threadId = Native.GetCurrentThreadId();
        Hook();
        _watchdog = new Timer(_ => Native.PostThreadMessageW(_threadId, MsgWatchdog, IntPtr.Zero, IntPtr.Zero), null, 3000, 3000);
        ready.Set();
        // Low-level hooks and out-of-context WinEvents are delivered through this queue.
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == MsgWatchdog) { CheckAlive(); continue; }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        _watchdog?.Dispose();
        Unhook();
    }

    private void Hook()
    {
        var module = Native.GetModuleHandleW(null);
        if (_kbHook == IntPtr.Zero) _kbHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _kbProc, module, 0);
        if (_mouseHook == IntPtr.Zero) _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_winEventHook == IntPtr.Zero)
            _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _lastHookTick = Environment.TickCount;
        _winDown = _otherKey = _passThrough = _swallowUp = false;
    }

    private void Unhook()
    {
        if (_kbHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_winEventHook != IntPtr.Zero) { Native.UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
    }

    /// <summary>Windows drops a low-level hook that misses its deadline a few times — silently, and the
    /// app has no way to ask. If the user has produced input since our hooks last spoke, they are dead.
    /// Input aimed at an elevated window is invisible to us too (UIPI), so re-installs are rate-limited
    /// rather than repeated every tick while the user works in an admin window.</summary>
    private void CheckAlive()
    {
        var lii = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref lii)) return;
        int lastInput = unchecked((int)lii.dwTime), now = Environment.TickCount;
        if (lastInput - Volatile.Read(ref _lastHookTick) <= 2000 || now - _lastReinstall < 30_000) return;
        _lastReinstall = now;
        Debug("hooks silent while input flowed → re-installing");
        Unhook();
        Hook();
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        _lastHookTick = Environment.TickCount;
        if (code < 0 || !WinKeyEnabled) return Native.CallNextHookEx(_kbHook, code, wParam, lParam);

        var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        if ((k.flags & Native.LLKHF_INJECTED) != 0 && k.dwExtraInfo == Magic)
            return Native.CallNextHookEx(_kbHook, code, wParam, lParam);   // our own dummy key

        int msg = (int)wParam;
        bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        bool isWin = k.vkCode is Native.VK_LWIN or Native.VK_RWIN;

        if (isWin)
        {
            long now = Environment.TickCount64;
            if (down)
            {
                // Auto-repeat keeps sending key-downs while Win is held (every ~30 ms after a delay of up
                // to 1 s). Only the first one starts a press; a Win-down after a long silence means we
                // missed the key-up (an elevated window had focus) and the state is stale.
                if (!_winDown || now - _lastWinDown > 1200)
                {
                    _winDown = true;
                    _otherKey = false;
                    _passThrough = ForegroundOwnsWinKey();
                    if (_passThrough) Debug("win-down → remote session owns the key, passing through");
                }
                _lastWinDown = now;
            }
            else
            {
                bool tap = _winDown && !_otherKey && !_passThrough;
                _winDown = false;
                if (tap)
                {
                    // A lone Win tap. Swallow this real key-up (return 1) and re-emit the release as a
                    // chord: a harmless dummy key, then a tagged Win-up we let pass. Windows then sees
                    // Win-down · dummy · Win-up — a "Win + something" combo — and does NOT open its own
                    // Start menu. Injecting during the key-up hook and *also* passing the key-up through
                    // does not work: the injected events queue AFTER the real key-up, too late to mask.
                    // Real Win+X chords never reach here (they set _otherKey and pass through untouched),
                    // so swallowing is safe.
                    Native.SendKeys(
                        Native.Key(MaskVk, up: false, Magic),
                        Native.Key(MaskVk, up: true, Magic),
                        Native.Key((ushort)k.vkCode, up: true, Magic));
                    Debug($"winkey tap → toggle (vk={k.vkCode})");
                    Post(Toggle);
                    return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(_kbHook, code, wParam, lParam);
        }

        if (_winDown && down) _otherKey = true;      // Win+X style chord → let Windows handle it

        if (down && k.vkCode == Native.VK_ESCAPE && (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0
            && !ForegroundOwnsWinKey())
        {
            Post(Toggle);                               // Ctrl+Esc
            return 1;
        }
        return Native.CallNextHookEx(_kbHook, code, wParam, lParam);
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        _lastHookTick = Environment.TickCount;
        if (code < 0) return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
        int msg = (int)wParam;
        if (msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN)
        {
            var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if (msg == Native.WM_LBUTTONDOWN && StartButtonEnabled)
            {
                var r = TaskbarInfo.StartButton;
                // the button re-centres constantly: a near miss refreshes the cache for the next click
                if (r is { } probe && !probe.Contains(m.pt.X, m.pt.Y) && Inflate(probe, 48).Contains(m.pt.X, m.pt.Y))
                    TaskbarInfo.RefreshStartButtonAsync();
                if (DebugEnabled)
                    Debug($"click ({m.pt.X},{m.pt.Y}) startBtn={(r is { } rr ? $"{rr.Left},{rr.Top} {rr.Width}x{rr.Height}" : "null")} hit={(r is { } h && h.Contains(m.pt.X, m.pt.Y))}");
                // a couple of pixels of slack: the button re-centres constantly and a near-miss used to
                // fall through to the Windows 11 menu. A full-screen window (remote desktop, a game, a
                // video) covers the taskbar, so a click there belongs to it, not to the Start button.
                if (r is { } rect && Inflate(rect, 3).Contains(m.pt.X, m.pt.Y) && !FullscreenForeignForeground())
                {
                    _swallowUp = true;
                    Post(Toggle);
                    return 1;
                }
            }
            // Light dismiss: any button pressed outside every window of ours closes the menu. This does
            // not depend on the other window taking activation (the taskbar and the desktop often do
            // not), which is why the Deactivated event alone left the menu open. The click goes through.
            if (MenuVisible && !PointInOurWindows(m.pt))
            {
                Debug($"click outside ({m.pt.X},{m.pt.Y}) → dismiss");
                Post(Dismiss);
            }
        }
        else if (msg == Native.WM_LBUTTONUP && _swallowUp)
        {
            _swallowUp = false;
            return 1;
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static Native.RECT Inflate(Native.RECT r, int by) =>
        new() { Left = r.Left - by, Top = r.Top - by, Right = r.Right + by, Bottom = r.Bottom + by };

    /// <summary>Is this screen point inside any visible top-level window of the UI thread (the menu,
    /// its context menus, flyouts, tooltips, dialogs)? Message-free, so safe inside a hook.</summary>
    private bool PointInOurWindows(Native.POINT pt)
    {
        _probe = pt;
        _probeHit = false;
        Native.EnumThreadWindows(_uiThread, _enumProc, IntPtr.Zero);
        return _probeHit;
    }

    private bool EnumWindow(IntPtr hwnd, IntPtr lParam)
    {
        if (Native.IsWindowVisible(hwnd) && Native.GetWindowRect(hwnd, out var r) && r.Contains(_probe.X, _probe.Y))
        {
            _probeHit = true;
            return false;
        }
        return true;
    }

    /// <summary>A window that covers its whole monitor and is not merely maximised (a maximised window
    /// also spans the monitor when the taskbar auto-hides, and its Start button must keep working).</summary>
    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || Native.IsZoomed(hwnd) || !Native.GetWindowRect(hwnd, out var r) || r.Width <= 0) return false;
        var m = TaskbarInfo.MonitorInfo(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST)).rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>The foreground window is a full-screen window of another process (not the desktop or
    /// the taskbar) — it is covering the Start button, so clicks there are not Start-button clicks.</summary>
    private static bool FullscreenForeignForeground()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == Environment.ProcessId) return false;
        if (Native.GetClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        return IsFullscreen(fg);
    }

    /// <summary>A full-screen remote desktop / virtual machine client has the foreground: it forwards
    /// the Windows key to the remote machine, whose own Start menu should open — not ours.</summary>
    private bool ForegroundOwnsWinKey()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsFullscreen(fg)) return false;
        if (Native.GetClassName(fg) == "TscShellContainerClass") return true;     // mstsc
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == Environment.ProcessId) return false;
        if (!_isRemoteClient.TryGetValue(pid, out bool remote))
        {
            string exe = System.IO.Path.GetFileNameWithoutExtension(Native.GetProcessImageName(pid));
            remote = RemoteClients.Any(n => exe.StartsWith(n, StringComparison.OrdinalIgnoreCase));
            if (_isRemoteClient.Count > 64) _isRemoteClient.Clear();
            _isRemoteClient[pid] = remote;
        }
        return remote;
    }

    private void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // apps coming and going re-centre the taskbar, which moves the Start button
        TaskbarInfo.RefreshStartButtonAsync();
        if (!FallbackEnabled || idObject != 0 || hwnd == IntPtr.Zero) return;
        if (Native.GetClassName(hwnd) != "Windows.UI.Core.CoreWindow") return;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (!_isStartHost.TryGetValue(pid, out bool isStart))
        {
            isStart = Native.GetProcessImageName(pid).EndsWith("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase);
            if (_isStartHost.Count > 64) _isStartHost.Clear();
            _isStartHost[pid] = isStart;
        }
        if (isStart) Post(Show);
    }

    private static void Post(Action? a)
    {
        if (a != null) Application.Current?.Dispatcher.BeginInvoke(a);
    }

    /// <summary>Opt-in trace to %LocalAppData%\WinlyStart\debug.log (set env WINLYSTART_DEBUG=1). Off by default.</summary>
    internal static readonly bool DebugEnabled =
        Environment.GetEnvironmentVariable("WINLYSTART_DEBUG") is { Length: > 0 } v && v != "0";

    internal static void Debug(string msg)
    {
        if (!DebugEnabled) return;
        try
        {
            System.IO.Directory.CreateDirectory(Store.Dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(Store.Dir, "debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        // Windows is back to normal the moment the hook thread unhooks and exits.
        if (_thread is { IsAlive: true })
        {
            Native.PostThreadMessageW(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
        }
        Unhook();
    }
}
