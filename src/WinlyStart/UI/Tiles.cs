using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media.Imaging;
using WinlyStart.Core;

namespace WinlyStart.UI;

/// <summary>Which live content a tile can show. Windows 11 has no live-tile platform, so these are
/// Winly Start's own faces for the apps where it has real data.</summary>
public enum LiveKind { None, Calendar, Clock, Photos, Website }

/// <summary>One tile on the board. Geometry is in 48 px units with 4 px gutters, like Windows 10.</summary>
public sealed class TileVm : INotifyPropertyChanged
{
    public const int Unit = 48, Gap = 4, Pitch = Unit + Gap;

    public AppEntry App { get; }
    public TileGroupVm Group { get; set; } = null!;

    private TileSize _size;
    private int _col, _row;
    private bool _dragging;

    public TileVm(AppEntry app, TileSize size, int col, int row, bool live = true)
    {
        App = app; _size = size; _col = col; _row = row; _live = live;
        app.PropertyChanged += (_, e) => { if (e.PropertyName == "Item[]") Raise(nameof(Image)); };
    }

    // ── live tile ──
    private bool _live, _showLive;
    private string _liveTitle = string.Empty, _liveBig = string.Empty, _liveSub = string.Empty;
    private BitmapSource? _liveImage;

    public LiveKind Kind =>
        App.Id == "builtin:calendar" ? LiveKind.Calendar
        : App.Id.Contains("Microsoft.WindowsAlarms", StringComparison.OrdinalIgnoreCase) ? LiveKind.Clock
        : App.Id.Contains("Microsoft.Windows.Photos", StringComparison.OrdinalIgnoreCase) ? LiveKind.Photos
        : App.IsWebsite ? LiveKind.Website
        : LiveKind.None;
    /// <summary>Small tiles were never live on Windows 10 either.</summary>
    public bool CanBeLive => Kind != LiveKind.None && _size != TileSize.Small;
    public bool Live { get => _live; set { _live = value; Raise(nameof(Live)); Raise(nameof(LiveMenuText)); if (!value) ShowLive = false; } }
    public string LiveMenuText => _live ? "Turn Live Tile off" : "Turn Live Tile on";
    /// <summary>true while the live face is up; the template slides between faces on change.</summary>
    public bool ShowLive { get => _showLive; set { if (_showLive == value) return; _showLive = value; Raise(nameof(ShowLive)); } }
    public string LiveTitle { get => _liveTitle; set { _liveTitle = value; Raise(nameof(LiveTitle)); } }
    public string LiveBig { get => _liveBig; set { _liveBig = value; Raise(nameof(LiveBig)); } }
    public string LiveSub { get => _liveSub; set { _liveSub = value; Raise(nameof(LiveSub)); } }
    public BitmapSource? LiveImage { get => _liveImage; set { _liveImage = value; Raise(nameof(LiveImage)); Raise(nameof(LiveHasImage)); } }
    public bool LiveHasImage => _liveImage != null;
    public double LiveBigSize => _size switch { TileSize.Large => 60, TileSize.Wide => 38, _ => 22 };
    /// <summary>Stable per-tile phase so live tiles don't all flip in lock-step.</summary>
    public int LivePhase => Math.Abs(App.Id.GetHashCode() % 5);

    public TileSize Size
    {
        get => _size;
        set { _size = value; Raise(nameof(Size)); Raise(nameof(Width)); Raise(nameof(Height)); Raise(nameof(ShowLabel)); Raise(nameof(ImageSize)); Raise(nameof(ImageWidth)); Raise(nameof(ImageHeight)); Raise(nameof(Image)); Raise(nameof(CanBeLive)); Raise(nameof(LiveBigSize)); if (!CanBeLive) ShowLive = false; }
    }
    public int Col { get => _col; set { _col = value; Raise(nameof(X)); } }
    public int Row { get => _row; set { _row = value; Raise(nameof(Y)); } }
    public bool IsDragging { get => _dragging; set { _dragging = value; Raise(nameof(IsDragging)); } }

    public int Cols => _size switch { TileSize.Small => 1, TileSize.Medium => 2, _ => 4 };
    public int Rows => _size switch { TileSize.Small => 1, TileSize.Medium or TileSize.Wide => 2, _ => 4 };
    public double Width => Cols * Pitch - Gap;
    public double Height => Rows * Pitch - Gap;
    public double X => _col * Pitch;
    public double Y => _row * Pitch;
    public bool ShowLabel => _size != TileSize.Small;
    public string Name => App.Name;
    public bool CanRunAsAdmin => !App.IsPackaged;

    /// <summary>Displayed icon size (logical px): centred on the accent plate, Windows 10 proportions.
    /// Packaged (Store) apps ship a plated square logo, so it sits a little larger than a desktop icon.</summary>
    public int ImageSize => App.IsPackaged
        ? _size switch { TileSize.Small => 32, TileSize.Large => 128, _ => 64 }
        : _size switch { TileSize.Small => 24, TileSize.Large => 96, _ => 48 };
    public double ImageWidth => ImageSize;
    public double ImageHeight => ImageSize;

    /// <summary>Resolution actually requested from the shell — always a generous native icon size so
    /// WPF downscales (which is crisp) instead of upscaling (which looks jagged).</summary>
    private int SourceSize => _size switch { TileSize.Large => 256, TileSize.Small => 64, _ => 96 };
    public BitmapSource? Image => App[SourceSize];

    public Tile ToModel() => new() { AppId = App.Id, Size = _size, Col = _col, Row = _row, Live = _live };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>A named group of tiles; packs its tiles first-fit into a grid <see cref="Columns"/> units wide.</summary>
public sealed class TileGroupVm : INotifyPropertyChanged
{
    private string _name;
    private int _rows;

    public TileGroupVm(string? name, int columns) { _name = name ?? string.Empty; _columns = columns; }

    public string Name { get => _name; set { _name = value ?? string.Empty; Raise(nameof(Name)); } }

    private bool _editing;
    /// <summary>The header is plain text until the user clicks it, then it becomes a real text box.</summary>
    public bool IsEditing { get => _editing; set { _editing = value; Raise(nameof(IsEditing)); } }
    private int _columns;
    public int Columns { get => _columns; set { _columns = value; Raise(nameof(PixelWidth)); } }
    public ObservableCollection<TileVm> Tiles { get; } = new();
    public double PixelWidth => Columns * TileVm.Pitch - TileVm.Gap;
    /// <summary>Exactly the rows in use (at least one medium row), so the gap to the next group is
    /// always the same — the group template's bottom margin — whatever row the last tile ends on.</summary>
    public double PixelHeight => Math.Max(_rows, 2) * TileVm.Pitch - TileVm.Gap;

    /// <summary>Set when the user made this group by hand; it then survives while empty.</summary>
    public bool KeepEmpty { get; set; }

    public void Add(TileVm t) { t.Group = this; Tiles.Add(t); KeepEmpty = false; Pack(); }
    public void Remove(TileVm t) { Tiles.Remove(t); Pack(); }

    /// <summary>
    /// Lays the tiles out. <paramref name="priority"/> (a dropped tile) is placed first at its requested
    /// cell; every other tile keeps its cell when still free, otherwise slides to the first free slot.
    /// </summary>
    public void Pack(TileVm? priority = null)
    {
        var grid = new List<bool[]>();
        bool Free(int c, int r, int w, int h)
        {
            if (c < 0 || c + w > Columns || r < 0) return false;
            for (int y = r; y < r + h; y++)
            {
                if (y >= grid.Count) return true;
                for (int x = c; x < c + w; x++) if (grid[y][x]) return false;
            }
            return true;
        }
        void Occupy(int c, int r, int w, int h)
        {
            while (grid.Count < r + h) grid.Add(new bool[Columns]);
            for (int y = r; y < r + h; y++) for (int x = c; x < c + w; x++) grid[y][x] = true;
        }
        void Place(TileVm t, bool keep)
        {
            if (keep && t.Row <= 64 && Free(t.Col, t.Row, t.Cols, t.Rows)) { Occupy(t.Col, t.Row, t.Cols, t.Rows); return; }
            for (int r = 0; ; r++)
                for (int c = 0; c + t.Cols <= Columns; c++)
                    if (Free(c, r, t.Cols, t.Rows)) { t.Col = c; t.Row = r; Occupy(c, r, t.Cols, t.Rows); return; }
        }

        var order = Tiles.OrderBy(t => t.Row).ThenBy(t => t.Col).ToList();
        if (priority != null)
        {
            order.Remove(priority);
            priority.Col = Math.Clamp(priority.Col, 0, Math.Max(0, Columns - priority.Cols));
            priority.Row = Math.Max(0, priority.Row);
            Place(priority, keep: true);
        }
        foreach (var t in order) Place(t, keep: true);

        // Keep the layout the user made: tiles stay in the cells they were dropped in, gaps and all.
        // Only a gap at the very TOP is closed, so a group never floats away from its header.
        int first = 0;
        while (first < grid.Count && Array.IndexOf(grid[first], true) < 0) first++;
        if (first > 0 && first < grid.Count)
            foreach (var t in Tiles) t.Row = Math.Max(0, t.Row - first);

        _rows = Math.Max(0, grid.Count - first);
        Raise(nameof(PixelHeight));
    }

    public TileGroup ToModel() => new() { Name = _name ?? string.Empty, KeepEmpty = KeepEmpty, Tiles = Tiles.Select(t => t.ToModel()).ToList() };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public static class TileBoard
{
    public static ObservableCollection<TileGroupVm> Load(TileLayout layout, AppCatalog catalog, int columns)
    {
        var groups = new ObservableCollection<TileGroupVm>();
        foreach (var g in layout.Groups)
        {
            var vm = new TileGroupVm(g.Name, columns) { KeepEmpty = g.KeepEmpty };
            foreach (var t in g.Tiles)
            {
                var app = catalog.Find(t.AppId);
                if (app == null) continue;                       // uninstalled since last run
                vm.Tiles.Add(new TileVm(app, t.Size, t.Col, t.Row, t.Live) { Group = vm });
            }
            vm.Pack();
            groups.Add(vm);
        }
        return groups;
    }

    public static TileLayout Save(IEnumerable<TileGroupVm> groups) => new() { Groups = groups.Select(g => g.ToModel()).ToList() };

    /// <summary>First-run board, mirroring the spirit of the Windows 10 default groups with whatever is installed.</summary>
    public static TileLayout Default(AppCatalog catalog)
    {
        TileGroup Group(string name, params (string app, TileSize size)[] wants)
        {
            var g = new TileGroup { Name = name };
            foreach (var (want, size) in wants)
            {
                var app = catalog.Apps.FirstOrDefault(a => a.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
                       ?? catalog.Apps.FirstOrDefault(a => a.Name.StartsWith(want, StringComparison.OrdinalIgnoreCase));
                if (app != null && g.Tiles.All(t => t.AppId != app.Id)) g.Tiles.Add(new Tile { AppId = app.Id, Size = size });
            }
            return g;
        }
        var layout = new TileLayout();
        var productivity = Group("Productivity",
            ("Microsoft Edge", TileSize.Wide), ("Mail", TileSize.Medium), ("Calendar", TileSize.Medium),
            ("Calculator", TileSize.Medium), ("Microsoft Store", TileSize.Medium), ("Photos", TileSize.Medium),
            ("Settings", TileSize.Medium), ("Notepad", TileSize.Medium), ("Paint", TileSize.Medium));
        productivity.Tiles.Insert(0, new Tile { AppId = "builtin:calendar", Size = TileSize.Medium });   // live calendar tile
        layout.Groups.Add(productivity);
        layout.Groups.Add(Group("Explore",
            ("Weather", TileSize.Wide), ("News", TileSize.Medium), ("Clock", TileSize.Medium),
            ("Camera", TileSize.Medium), ("Snipping Tool", TileSize.Medium), ("Microsoft To Do", TileSize.Medium)));
        layout.Groups.RemoveAll(g => g.Tiles.Count == 0);
        return layout;
    }
}
