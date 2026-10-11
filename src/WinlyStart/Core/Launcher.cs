using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace WinlyStart.Core;

/// <summary>
/// Everything that leaves the process: launching apps, opening shell locations, and the
/// power/session actions the Windows 10 Start menu exposes. Only ever called from an
/// explicit user click.
/// </summary>
public static class Launcher
{
    /// <summary>Set by the UI to open built-in items such as "builtin:calendar".</summary>
    public static Action<string>? BuiltinHandler { get; set; }

    public static void Launch(AppEntry app)
    {
        try
        {
            if (app.Id.StartsWith("builtin:", StringComparison.Ordinal)) { BuiltinHandler?.Invoke(app.Id); return; }
            // user-added exe / file / website: let the shell decide how to open it
            if (app.CustomTarget is { Length: > 0 } target) { Start(target); return; }
            if (app.IsPackaged)
            {
                string aumid = app.Id["uwp:".Length..];
                try
                {
                    var mgr = (Native.IApplicationActivationManager)new Native.ApplicationActivationManager();
                    if (mgr.ActivateApplication(aumid, null, 0, out _) == 0) return;
                }
                catch { /* fall through to the explorer route */ }
                Start("explorer.exe", "shell:AppsFolder\\" + aumid);
            }
            else Start(app.Id);
        }
        catch { /* app gone / access denied — Windows shows its own error where applicable */ }
    }

    public static void RunAsAdmin(AppEntry app)
    {
        if (app.IsPackaged) { Launch(app); return; }
        try { Process.Start(new ProcessStartInfo(app.Id) { UseShellExecute = true, Verb = "runas" }); }
        catch { /* UAC cancelled */ }
    }

    public static void OpenFileLocation(AppEntry app)
    {
        if (app.IsPackaged) return;
        string path = app.CustomTarget ?? app.Id;
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        Start("explorer.exe", "/select,\"" + path + "\"");
    }

    public static void Uninstall(AppEntry app) => Start("ms-settings:appsfeatures");

    public static void Start(string fileName, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(fileName, args ?? string.Empty) { UseShellExecute = true }); }
        catch { }
    }

    // ── left rail ──
    public static void OpenDocuments() => Start("explorer.exe", "shell:Personal");
    public static void OpenPictures() => Start("explorer.exe", "shell:My Pictures");
    public static void OpenSettings() => Start("ms-settings:");
    public static void OpenDownloads() => Start("explorer.exe", "shell:Downloads");
    public static void OpenMusic() => Start("explorer.exe", "shell:My Music");
    public static void OpenVideos() => Start("explorer.exe", "shell:My Video");
    public static void OpenNetwork() => Start("explorer.exe", "shell:NetworkPlacesFolder");
    public static void OpenPersonalFolder() => Start("explorer.exe", "shell:UsersFilesFolder");
    public static void OpenAccountSettings() => Start("ms-settings:yourinfo");
    public static void OpenFileExplorer() => Start("explorer.exe");

    // ── power / session (identical to what the Windows 10 Start menu does) ──
    public static void Lock() => Native.LockWorkStation();
    public static void SignOut() { if (!Native.ExitWindowsEx(Native.EWX_LOGOFF, 0)) Shutdown("/l"); }
    public static void Sleep() => Native.SetSuspendState(false, false, false);
    public static void Hibernate() => Native.SetSuspendState(true, false, false);
    public static void Restart() => Shutdown("/r /t 0");
    public static void ShutDown()
    {
        // hybrid = Windows 10 "Shut down" with fast startup; plain shutdown if that is unavailable
        if (Shutdown("/s /hybrid /t 0") != 0) Shutdown("/s /t 0");
    }

    private static int Shutdown(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
            p!.WaitForExit(5000);
            return p.HasExited ? p.ExitCode : 0;
        }
        catch { return -1; }
    }
}

/// <summary>Current user's display name and account picture (for the left rail).</summary>
public static class UserInfo
{
    private static string? _displayName;

    /// <summary>The account's display name. On a domain-joined PC the first lookup can go to the
    /// network and take seconds, so callers resolve it off the UI thread; afterwards it is cached.</summary>
    public static string DisplayName
    {
        get
        {
            if (_displayName != null) return _displayName;
            try
            {
                var sb = new StringBuilder(256);
                uint size = (uint)sb.Capacity;
                if (Native.GetUserNameExW(Native.NameDisplay, sb, ref size) != 0 && sb.Length > 0) return _displayName = sb.ToString();
            }
            catch { }
            return _displayName = Environment.UserName;
        }
    }

    /// <summary>The picture actually shown: a chosen one wins over the Windows account picture.</summary>
    public static string? PicturePath
    {
        get
        {
            string custom = App.Settings.ProfileImagePath;
            return custom.Length > 0 && File.Exists(custom) ? custom : WindowsPicturePath;
        }
    }

    /// <summary>The Windows account picture, ignoring any override.</summary>
    public static string? WindowsPicturePath
    {
        get
        {
            try
            {
                string? sid = WindowsIdentity.GetCurrent().User?.Value;
                if (sid == null) return null;
                string dir = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "AccountPictures", sid);
                if (!Directory.Exists(dir)) return null;
                return Directory.EnumerateFiles(dir, "*Image96*").FirstOrDefault()
                    ?? Directory.EnumerateFiles(dir, "*Image64*").FirstOrDefault()
                    ?? Directory.EnumerateFiles(dir).FirstOrDefault(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }
    }
}
