using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WinlyStart.Core;

/// <summary>One installed application (desktop shortcut or packaged app).</summary>
public sealed class AppEntry : INotifyPropertyChanged
{
    public const int ListIcon = 24, TileIcon = 48, LargeIcon = 96;

    /// <summary>Stable id: full .lnk/.url path, or "uwp:&lt;AUMID&gt;".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>What the shell needs to render the icon / launch: the shortcut path or shell:AppsFolder\AUMID.</summary>
    public required string ParsingName { get; init; }
    /// <summary>Start-menu sub-folder (first level only, as Windows 10 does), null for root items.</summary>
    public string? Folder { get; init; }
    public bool IsPackaged { get; init; }
    /// <summary>Set for user-added items (an .exe, a file, or a URL); launched with the shell.</summary>
    public string? CustomTarget { get; init; }
    /// <summary>Cached preview image for a website tile's live face, if one has been fetched.</summary>
    public string? ThumbPath { get; set; }
    /// <summary>A user-added item (website / program / file) — these can be edited and removed.</summary>
    public bool IsCustom => CustomTarget is { Length: > 0 };
    public bool IsWebsite => CustomTarget is { } t &&
        (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    public DateTime Created { get; init; }
    public int Launches { get; set; }

    public string SortKey => Name;
    /// <summary>A–Z header this app belongs to ("#" digits, "&amp;" symbols, otherwise the first letter).</summary>
    public string Letter
    {
        get
        {
            if (Name.Length == 0) return "&";
            char c = char.ToUpperInvariant(Name[0]);
            if (char.IsDigit(c)) return "#";
            return char.IsLetter(c) ? c.ToString() : "&";
        }
    }

    private readonly Dictionary<int, BitmapSource> _images = new(3);

    /// <summary>App-list icon. Requested at 32 px (a native shell size) and shown at 24 for a crisp downscale.</summary>
    public BitmapSource? Icon => this[32];

    /// <summary>Icon at any logical size (24/48/96/100/204…). Null until the background loader
    /// delivers it, then <c>PropertyChanged("Item[]")</c> refreshes every binding.</summary>
    public BitmapSource? this[int size]
    {
        get
        {
            if (_images.TryGetValue(size, out var bmp)) return bmp;
            IconLoader.Request(this, size);
            return null;
        }
    }

    internal void SetIcon(int size, BitmapSource bmp)
    {
        _images[size] = bmp;
        // "Item[]" refreshes indexer bindings (tiles); Icon is a plain property and needs its own
        // notification or the app list stays blank forever after the async load.
        Raise("Item[]");
        Raise(nameof(Icon));
    }

    /// <summary>Drop cached bitmaps (DPI change / memory trim); they reload on next bind.</summary>
    internal void DropIcons()
    {
        _images.Clear();
        Raise("Item[]");
        Raise(nameof(Icon));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// Enumerates what the Windows 10 Start menu shows: every *.lnk / *.url in both Start Menu
/// "Programs" folders (sub-folder = Start folder) plus every packaged app from shell:AppsFolder.
/// </summary>
public sealed class AppCatalog
{
    public IReadOnlyList<AppEntry> Apps { get; private set; } = Array.Empty<AppEntry>();
    public UsageData Usage { get; }
    /// <summary>Items the user added by hand (Add program / file / website).</summary>
    public CustomItems Custom { get; }
    public event Action? Changed;

    private readonly List<FileSystemWatcher> _watchers = new();
    private System.Threading.Timer? _debounce;
    private int _scanning;

    public AppCatalog()
    {
        Usage = Store.Load("usage.json", JsonCtx.Default.UsageData);
        Custom = Store.Load("custom.json", JsonCtx.Default.CustomItems);
        foreach (var root in Roots())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                w.Created += (_, _) => ScheduleScan();
                w.Deleted += (_, _) => ScheduleScan();
                w.Renamed += (_, _) => ScheduleScan();
                _watchers.Add(w);
            }
            catch { /* watching is best-effort */ }
        }
    }

    private static IEnumerable<string> Roots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    }

    public AppEntry? Find(string id) => Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    public void RecordLaunch(AppEntry app)
    {
        app.Launches++;
        Usage.Launches[app.Id] = app.Launches;
        Store.SaveAsync("usage.json", Usage, JsonCtx.Default.UsageData);   // called mid-launch; keep the UI thread free
    }

    /// <summary>Adds a user item (exe / file / website) and rescans so it appears everywhere.</summary>
    public AppEntry AddCustom(string name, string target)
    {
        var item = new CustomItem { Id = "custom:" + Guid.NewGuid().ToString("N"), Name = name, Target = target };
        Custom.Items.Add(item);
        Store.Save("custom.json", Custom, JsonCtx.Default.CustomItems);
        var entry = new AppEntry
        {
            Id = item.Id, Name = item.Name, ParsingName = item.Target,
            CustomTarget = item.Target, Created = DateTime.Now,
        };
        Apps = Apps.Append(entry).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Fetches favicons and live-tile previews for website items, then refreshes the UI.
    /// Cheap on repeat: WebAssets serves from its cache until the thumbnail lifetime expires.</summary>
    public void RefreshWebAssetsAsync()
    {
        var sites = Custom.Items.Where(c => c.Target.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        if (sites.Count == 0) return;
        _ = Task.Run(async () =>
        {
            bool changed = false;
            foreach (var c in sites)
            {
                if (c.IconPath.Length == 0 || !File.Exists(c.IconPath))
                {
                    if (await WebAssets.GetIconAsync(c.Target).ConfigureAwait(false) is { } icon) { c.IconPath = icon; changed = true; }
                }
                string? thumb = await WebAssets.GetThumbnailAsync(c.Target).ConfigureAwait(false);
                if (thumb != null && thumb != c.ThumbPath) { c.ThumbPath = thumb; changed = true; }
                else if (thumb != null)
                {
                    // same file, refreshed contents → let the tile know
                    var entry = Find(c.Id);
                    if (entry != null) entry.ThumbPath = thumb;
                }
            }
            if (!changed) return;
            Store.Save("custom.json", Custom, JsonCtx.Default.CustomItems);
            Application.Current?.Dispatcher.BeginInvoke(ScanAsync);
        });
    }

    /// <summary>Renames/retargets a user-added item. Cached web assets are dropped when the target changes.</summary>
    public void UpdateCustom(string id, string name, string target)
    {
        var item = Custom.Items.FirstOrDefault(c => c.Id == id);
        if (item == null) return;
        if (!string.Equals(item.Target, target, StringComparison.OrdinalIgnoreCase))
        {
            item.IconPath = string.Empty;
            item.ThumbPath = string.Empty;
        }
        item.Name = name;
        item.Target = target;
        Store.Save("custom.json", Custom, JsonCtx.Default.CustomItems);
        ScanAsync();
        RefreshWebAssetsAsync();
    }

    public void RemoveCustom(string id)
    {
        if (Custom.Items.RemoveAll(c => c.Id == id) == 0) return;
        Store.Save("custom.json", Custom, JsonCtx.Default.CustomItems);
        ScanAsync();
    }

    /// <summary>Installs/uninstalls fire many file events; coalesce them into one rescan.</summary>
    public void ScheduleScan(int delayMs = 1500)
    {
        _debounce ??= new System.Threading.Timer(_ => ScanAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _debounce.Change(delayMs, Timeout.Infinite);
    }

    public void ScanAsync()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        var t = new Thread(() =>
        {
            try
            {
                var list = Scan();
                Application.Current?.Dispatcher.BeginInvoke(() => { Apps = list; Changed?.Invoke(); });
            }
            finally { Interlocked.Exchange(ref _scanning, 0); }
        }) { IsBackground = true, Name = "WinlyStart.Scan" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private List<AppEntry> Scan()
    {
        var result = new List<AppEntry>(256);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool firstRun = Usage.FirstSeen.Count == 0;
        var now = DateTime.Now;

        foreach (var root in Roots()) ScanFolder(root, result, seen);

        foreach (var (name, aumid) in EnumeratePackagedApps())
        {
            string id = "uwp:" + aumid;
            if (!seen.Add("uwp\0" + name)) continue;
            if (!Usage.FirstSeen.TryGetValue(id, out var created))
                Usage.FirstSeen[id] = created = firstRun ? DateTime.MinValue : now;
            result.Add(new AppEntry
            {
                Id = id, Name = name, ParsingName = "shell:AppsFolder\\" + aumid,
                IsPackaged = true, Created = created,
                Launches = Usage.Launches.GetValueOrDefault(id),
            });
        }

        foreach (var c in Custom.Items)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Target)) continue;
            // a cached favicon feeds the normal icon pipeline; otherwise the default glyph shows
            bool hasIcon = c.IconPath.Length > 0 && File.Exists(c.IconPath);
            result.Add(new AppEntry
            {
                Id = c.Id, Name = c.Name, ParsingName = hasIcon ? "img:" + c.IconPath : c.Target, CustomTarget = c.Target,
                ThumbPath = c.ThumbPath.Length > 0 && File.Exists(c.ThumbPath) ? c.ThumbPath : null,
                Created = DateTime.MinValue, Launches = Usage.Launches.GetValueOrDefault(c.Id),
            });
        }

        // built-in items (open inside Winly Start itself)
        result.Add(new AppEntry
        {
            Id = "builtin:calendar", Name = "Calendar", ParsingName = "res:Assets/calendar.png",
            Created = DateTime.MinValue, Launches = Usage.Launches.GetValueOrDefault("builtin:calendar"),
        });

        // forget usage of apps that no longer exist
        var ids = new HashSet<string>(result.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var k in Usage.Launches.Keys.Where(k => !ids.Contains(k)).ToList()) Usage.Launches.Remove(k);
        foreach (var k in Usage.FirstSeen.Keys.Where(k => !ids.Contains(k)).ToList()) Usage.FirstSeen.Remove(k);
        Store.Save("usage.json", Usage, JsonCtx.Default.UsageData);

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    private void ScanFolder(string root, List<AppEntry> result, HashSet<string> seen)
    {
        if (!Directory.Exists(root)) return;
        string startup1 = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string startup2 = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories); }
        catch { return; }

        foreach (var file in files)
        {
            string ext = Path.GetExtension(file);
            if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) continue;
            string dir = Path.GetDirectoryName(file)!;
            if (dir.StartsWith(startup1, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(startup2, StringComparison.OrdinalIgnoreCase)) continue;
            FileInfo fi;
            try { fi = new FileInfo(file); if ((fi.Attributes & FileAttributes.Hidden) != 0) continue; }
            catch { continue; }

            string? folder = null;
            string rel = Path.GetRelativePath(root, dir);
            if (rel != ".")
            {
                int cut = rel.IndexOfAny(new[] { '\\', '/' });
                folder = cut < 0 ? rel : rel[..cut];        // deeper levels flatten into the first, like Windows 10
            }
            string name = Path.GetFileNameWithoutExtension(file);
            if (!seen.Add((folder ?? string.Empty) + "\0" + name)) continue;   // same shortcut in both Programs roots

            result.Add(new AppEntry
            {
                Id = file, Name = name, ParsingName = file, Folder = folder,
                Created = fi.CreationTime, Launches = Usage.Launches.GetValueOrDefault(file),
            });
        }
    }

    private static IEnumerable<(string name, string aumid)> EnumeratePackagedApps()
    {
        var list = new List<(string, string)>();
        try
        {
            if (Native.CreateShellItem("shell:AppsFolder") is not { } folder) return list;
            var bhid = Native.BHID_EnumItems; var iid = Native.IID_IEnumShellItems;
            if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref iid) is not Native.IEnumShellItems e) return list;
            while (e.Next(1, out var item, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    string pn = item.GetDisplayName(Native.SIGDN_PARENTRELATIVEPARSING);
                    if (pn.IndexOf('!') < 0) continue;              // desktop entries come from the Programs folders instead
                    string name = item.GetDisplayName(Native.SIGDN_NORMALDISPLAY);
                    if (!string.IsNullOrWhiteSpace(name)) list.Add((name, pn));
                }
                catch { /* skip broken item */ }
            }
        }
        catch { /* AppsFolder unavailable — desktop apps still work */ }
        return list;
    }
}
