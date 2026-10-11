using System.IO;
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
        _hook.Toggle += () => _menu.ToggleMenu();
        _hook.Show += () => _menu.ShowMenu();
        _hook.Dismiss += () => _menu.Dismiss();
        ApplyHookSettings();
        _hook.Install();
        TaskbarInfo.RefreshStartButtonAsync();
        _menu.Prewarm();

        _tray = new TrayIcon("Winly Start");
        _tray.LeftClick += () => _menu.ToggleMenu();
        _tray.RightClick += ShowTrayMenu;

        Autostart.Apply(Settings.StartWithWindows);
        base.OnStartup(e);
    }

    /// <summary>Clamps whatever came from disk (hand-edited, imported, or from an older version).</summary>
    private static void Normalize(Settings s)
    {
        s.TileColumns = Math.Clamp(s.TileColumns == 0 ? 6 : s.TileColumns, 4, 12);
        s.MenuHeight = Math.Clamp(s.MenuHeight, 480, 1200);
        s.ListWidth = Math.Clamp(s.ListWidth == 0 ? 256 : s.ListWidth, Settings.MinListWidth, Settings.MaxListWidth);
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
        Autostart.Apply(Settings.StartWithWindows);
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
        Autostart.Apply(Settings.StartWithWindows);
        app._menu?.ApplySettings();
    }

    private void ApplyHookSettings()
    {
        if (_hook == null) return;
        _hook.WinKeyEnabled = Settings.ReplaceWinKey;
        _hook.StartButtonEnabled = Settings.ReplaceStartButton;
        _hook.FallbackEnabled = Settings.ReplaceWinKey || Settings.ReplaceStartButton;
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

/// <summary>Per-user autostart via HKCU\…\Run — the only registry key Winly Start ever writes.</summary>
internal static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        // A packaged (MSIX) build declares startup through the windows.startupTask extension in its
        // manifest, and Windows — not us — owns the on/off switch. Writing the Run key there is
        // redirected into the package's private registry hive, so it would look like it worked and
        // silently never start the app. Leave it alone.
        if (Packaged.Is) return;

        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (k == null) return;
            if (enabled && Environment.ProcessPath is { } exe) k.SetValue("WinlyStart", $"\"{exe}\"");
            else k.DeleteValue("WinlyStart", throwOnMissingValue: false);
        }
        catch { }
    }
}
