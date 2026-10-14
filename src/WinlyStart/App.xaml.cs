using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Microsoft.Win32;
using WinlyStart.Core;
using WinlyStart.UI;

namespace WinlyStart;

public partial class App : Application
{
    public static Settings Settings { get; private set; } = new();
    public static AppCatalog Catalog { get; private set; } = null!;

    private Mutex? _mutex;
    private TrayIcon? _tray;
    private StartHook? _hook;
    private StartMenuWindow? _menu;
    private SettingsWindow? _settingsWindow;
    private CalendarWindow? _calendar;

    protected override void OnStartup(StartupEventArgs e)
    {
        // "WinlyStart.exe --autostart on|off": the installer's "Start when I sign in" switch. Runs before
        // the single-instance check so it also works while the app is open.
        if (e.Args is ["--autostart", var mode]) { Autostart.Apply(mode == "on"); Shutdown(); return; }

        _mutex = new Mutex(true, "WinlyStart.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log(ex.ExceptionObject as Exception);

        Settings = Store.Load("settings.json", JsonCtx.Default.Settings);
        Normalize(Settings);

        Theme.Current.Start(Settings.Theme);
        Catalog = new AppCatalog();
        _menu = new StartMenuWindow();
        Catalog.ScanAsync();

        Launcher.BuiltinHandler = id => { if (id == "builtin:calendar") ShowCalendar(); };

        _hook = new StartHook();
        _hook.Toggle += at => _menu.ToggleMenu(at);
        _hook.Show += at => _menu.ShowMenu(at);
        _hook.Dismiss += () => _menu.Dismiss();
        ApplyHookSettings();
        _hook.Install();
        TaskbarInfo.RefreshAsync();
        _menu.Prewarm();

        _tray = new TrayIcon("Winly Start");
        _tray.LeftClick += () => _menu.ToggleMenu();
        _tray.RightClick += ShowTrayMenu;

        // Any registration already present — the installer's "Start when I sign in", or an older
        // build's Run value — counts as on, shows in the Settings checkbox, and becomes the logon task.
        // Off the UI thread: it runs schtasks.exe.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool on = Settings.StartWithWindows || Autostart.IsRegistered();
            if (on && !Settings.StartWithWindows)
                Dispatcher.BeginInvoke(() => { Settings.StartWithWindows = true; Store.SaveAsync("settings.json", Settings, JsonCtx.Default.Settings); });
            Autostart.Apply(on);
        });
        base.OnStartup(e);
    }

    /// <summary>Clamps whatever came from disk (hand-edited, imported, or from an older version).</summary>
    private static void Normalize(Settings s)
    {
        s.TileColumns = Math.Clamp(s.TileColumns == 0 ? 6 : s.TileColumns, 4, 12);
        s.MenuHeight = Math.Clamp(s.MenuHeight, 480, 1200);
        s.ListWidth = Math.Clamp(s.ListWidth == 0 ? 256 : s.ListWidth, Settings.MinListWidth, Settings.MaxListWidth);
        s.GroupGap = Math.Clamp(s.GroupGap, Settings.MinGroupGap, Settings.MaxGroupGap);
    }

    /// <summary>After <see cref="Backup.Import"/>: re-read every data file and push it through the
    /// running app — settings, theme, hooks, user items, and (via the rescan) the tile layout.</summary>
    public static void ReloadFromDisk()
    {
        var app = (App)Current;
        Settings = Store.Load("settings.json", JsonCtx.Default.Settings);
        Normalize(Settings);
        Theme.Current.SetMode(Settings.Theme);
        app.ApplyHookSettings();
        Autostart.ApplyAsync(Settings.StartWithWindows);
        app._menu?.ReloadLayout();      // flags tiles.json for reload before the rescan below completes
        Catalog.ReloadUserData();
        app._calendar?.Close();         // reopens from calendar.json next time
    }

    /// <summary>Persist + push the current <see cref="Settings"/> into every subsystem.</summary>
    public static void ApplySettings()
    {
        var app = (App)Current;
        Store.Save("settings.json", Settings, JsonCtx.Default.Settings);
        Theme.Current.SetMode(Settings.Theme);
        app.ApplyHookSettings();
        Autostart.ApplyAsync(Settings.StartWithWindows);
        app._menu?.ApplySettings();
    }

    private void ApplyHookSettings()
    {
        if (_hook == null) return;
        _hook.WinKeyEnabled = Settings.ReplaceWinKey;
        _hook.StartButtonEnabled = Settings.ReplaceStartButton;
        _hook.FallbackEnabled = Settings.SuppressWindowsMenu && (Settings.ReplaceWinKey || Settings.ReplaceStartButton);
    }

    public void ShowCalendar()
    {
        if (_calendar is { IsLoaded: true }) { _calendar.Activate(); return; }
        _calendar = new CalendarWindow();
        _calendar.Show();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Show();
    }

    private void ShowTrayMenu()
    {
        var cm = new ContextMenu { Placement = PlacementMode.MousePoint, Style = (Style)Resources["Win10ContextMenu"] };
        cm.Items.Add(Item("Open Start menu", () => _menu?.ToggleMenu()));
        cm.Items.Add(Item("Settings…", ShowSettings));
        cm.Items.Add(Item("Refresh app list", () => Catalog.ScanAsync()));
        cm.Items.Add(new Separator { Style = (Style)Resources["Win10Separator"] });
        cm.Items.Add(Item("Exit Winly Start", Shutdown));
        cm.IsOpen = true;
        // A popup with no owning window would not dismiss on an outside click; give it the foreground.
        if (PresentationSource.FromVisual(cm) is HwndSource src) Native.SetForegroundWindow(src.Handle);

        MenuItem Item(string text, Action a)
        {
            var m = new MenuItem { Header = text, Style = (Style)Resources["Win10MenuItem"] };
            m.Click += (_, _) => a();
            return m;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();      // Windows is back to normal the moment this runs
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Store.Dir);
            File.AppendAllText(Path.Combine(Store.Dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}

/// <summary>Per-user "Start with Windows". An unpackaged build registers a Task Scheduler logon task
/// for the current user: the scheduler starts it the moment the user signs in, ahead of Explorer's
/// delayed Run/Startup items, so the Start button is ours from the first click. Builds before 1.5.6
/// wrote an HKCU Run value instead; it is migrated away. A packaged (MSIX) build declares startup
/// through the manifest's windows.startupTask extension and Windows owns the switch (a Run value
/// written there lands in the package's private hive and never starts anything).</summary>
internal static class Autostart
{
    public const string TaskName = "Winly Start";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Any registration present: the logon task or the legacy Run value.</summary>
    public static bool IsRegistered() => !Packaged.Is && (LegacyRunValue() || Schtasks($"/Query /TN \"{TaskName}\"") == 0);

    public static void ApplyAsync(bool enabled) => ThreadPool.QueueUserWorkItem(_ => Apply(enabled));

    public static void Apply(bool enabled)
    {
        if (Packaged.Is) return;
        try
        {
            if (enabled) Register(); else Schtasks($"/Delete /TN \"{TaskName}\" /F");
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            k?.DeleteValue("WinlyStart", throwOnMissingValue: false);
        }
        catch { }
    }

    private static bool LegacyRunValue()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("WinlyStart") != null; }
        catch { return false; }
    }

    /// <summary>Registers (or re-registers, so the path follows an update) the logon task: this user
    /// only, interactive token, least privilege, normal priority, no battery or time limit — the
    /// scheduler's defaults would stop the app after 72 h and skip it on battery.</summary>
    private static void Register()
    {
        if (Environment.ProcessPath is not { } exe) return;
        using var id = WindowsIdentity.GetCurrent();
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Winly Start when you sign in. Turn it off in Winly Start's Settings.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><UserId>{Esc(id.Name)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="user"><UserId>{Esc(id.User?.Value ?? id.Name)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="user"><Exec><Command>"{Esc(exe)}"</Command><WorkingDirectory>{Esc(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), "WinlyStart-logon-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try { Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F"); }
        finally { try { File.Delete(file); } catch { } }
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    private static int Schtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return p != null && p.WaitForExit(15_000) ? p.ExitCode : -1;
    }
}
