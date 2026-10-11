using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WinlyStart.Core;

public enum ThemeMode { Auto, Light, Dark }
public enum TileSize { Small, Medium, Wide, Large }

/// <summary>User options. Persisted to %LocalAppData%\WinlyStart\settings.json.</summary>
public sealed class Settings
{
    public bool ReplaceWinKey { get; set; } = true;
    public bool ReplaceStartButton { get; set; } = true;
    /// <summary>Close the Windows 11 Start menu whenever it still appears (touch, elevated window focused,
    /// a hook Windows dropped) and open Winly Start instead. The "fallback" in StartHook.</summary>
    public bool SuppressWindowsMenu { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public ThemeMode Theme { get; set; } = ThemeMode.Auto;
    /// <summary>Tile units per group row (each medium tile is 2 units). 6 = three medium tiles wide.
    /// Adjustable 4–12 by dragging the resize grip.</summary>
    public int TileColumns { get; set; } = 6;
    public int MenuHeight { get; set; } = 640;
    /// <summary>Width of the app-list column in DIPs; set by dragging the divider between the list and
    /// the tiles (shows on hover). 256 = Windows 10's 248 px list + scrollbar.</summary>
    public int ListWidth { get; set; } = 256;
    public const int MinListWidth = 176, MaxListWidth = 480;
    public bool ShowRecentlyAdded { get; set; } = true;
    public bool ShowMostUsed { get; set; } = true;
    /// <summary>Force the menu to the screen corner instead of following the Start button.</summary>
    public bool OpenAtCorner { get; set; }
    public bool TrimMemoryWhenHidden { get; set; } = true;
    /// <summary>Which shortcuts show in the left rail.</summary>
    public RailSettings Rail { get; set; } = new();
    /// <summary>Custom account picture for the left rail; empty = use the Windows account picture.</summary>
    public string ProfileImagePath { get; set; } = string.Empty;
}

/// <summary>Windows 10's "Choose which folders appear on Start", plus a calendar button.</summary>
public sealed class RailSettings
{
    public bool Documents { get; set; } = true;
    public bool Downloads { get; set; }
    public bool Music { get; set; }
    public bool Pictures { get; set; } = true;
    public bool Videos { get; set; }
    public bool Network { get; set; }
    public bool PersonalFolder { get; set; }
    public bool FileExplorer { get; set; }
    public bool Settings { get; set; } = true;
    /// <summary>Shows a calendar button in the rail that opens a calendar window.</summary>
    public bool Calendar { get; set; }
    /// <summary>Sleep / Shut down / Hibernate buttons at the top of the rail, under the hamburger.</summary>
    public bool QuickPower { get; set; } = true;
}

/// <summary>A user-added target: an .exe, a shortcut, any file, or a website.</summary>
public sealed class CustomItem
{
    public string Id { get; set; } = string.Empty;      // "custom:&lt;guid&gt;"
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;  // full path or URL
    /// <summary>Cached favicon for a website item (used as its icon).</summary>
    public string IconPath { get; set; } = string.Empty;
    /// <summary>Cached preview image for a website item's live tile.</summary>
    public string ThumbPath { get; set; } = string.Empty;
}

public sealed class CustomItems
{
    public List<CustomItem> Items { get; set; } = new();
}

/// <summary>A colour-coded note category.</summary>
public sealed class NoteCategory
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>"#RRGGBB".</summary>
    public string Color { get; set; } = "#0078D4";
}

/// <summary>Calendar notes ("yyyy-MM-dd" → text), their categories, and the remembered window size.</summary>
public sealed class CalendarData
{
    public Dictionary<string, string> Notes { get; set; } = new();
    /// <summary>"yyyy-MM-dd" → category id. Absent = the first category.</summary>
    public Dictionary<string, string> NoteCategories { get; set; } = new();
    public List<NoteCategory> Categories { get; set; } = new();
    public double Width { get; set; }
    public double Height { get; set; }

    public static List<NoteCategory> DefaultCategories() => new()
    {
        new() { Id = "general",   Name = "General",   Color = "#0078D4" },
        new() { Id = "work",      Name = "Work",      Color = "#8764B8" },
        new() { Id = "personal",  Name = "Personal",  Color = "#107C10" },
        new() { Id = "birthday",  Name = "Birthday",  Color = "#E3008C" },
        new() { Id = "holiday",   Name = "Holiday",   Color = "#F7B500" },
        new() { Id = "important", Name = "Important", Color = "#D13438" },
    };
}

public sealed class Tile
{
    public string AppId { get; set; } = string.Empty;
    public TileSize Size { get; set; } = TileSize.Medium;
    public int Col { get; set; }
    public int Row { get; set; }
    /// <summary>Live tile on/off (only meaningful for apps that have live content).</summary>
    public bool Live { get; set; } = true;
}

public sealed class TileGroup
{
    public string Name { get; set; } = string.Empty;
    public List<Tile> Tiles { get; set; } = new();
    /// <summary>A group the user created by hand: keep it even while it is still empty.</summary>
    public bool KeepEmpty { get; set; }
}

public sealed class TileLayout
{
    public List<TileGroup> Groups { get; set; } = new();
}

/// <summary>Launch counters ("Most used") and first-seen dates ("Recently added" for packaged apps).</summary>
public sealed class UsageData
{
    public Dictionary<string, int> Launches { get; set; } = new();
    public Dictionary<string, DateTime> FirstSeen { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(TileLayout))]
[JsonSerializable(typeof(UsageData))]
[JsonSerializable(typeof(CustomItems))]
[JsonSerializable(typeof(CalendarData))]
internal partial class JsonCtx : JsonSerializerContext { }

/// <summary>Tiny JSON file store. Reflection-free (source generated) to keep the working set small.</summary>
public static class Store
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinlyStart");

    public static T Load<T>(string file, JsonTypeInfo<T> type) where T : new()
    {
        try
        {
            var path = Path.Combine(Dir, file);
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), type) ?? new T();
        }
        catch { /* corrupt or unreadable → start fresh, never crash the shell replacement */ }
        return new T();
    }

    public static void Save<T>(string file, T value, JsonTypeInfo<T> type)
    {
        try { Write(file, JsonSerializer.Serialize(value, type)); }
        catch { /* disk full / locked — keep running with in-memory state */ }
    }

    /// <summary>Serialises now (the object is only safe to read on the calling thread) and writes the
    /// file on the pool, so a save in a click handler never stalls the UI thread on the disk.</summary>
    public static void SaveAsync<T>(string file, T value, JsonTypeInfo<T> type)
    {
        string json;
        try { json = JsonSerializer.Serialize(value, type); }
        catch { return; }
        _ = Task.Run(() => { try { Write(file, json); } catch { } });
    }

    /// <summary>Writes a data file verbatim (used by <see cref="Backup"/>). Atomic like every other save.</summary>
    public static void WriteRaw(string file, string json) => Write(file, json);

    private static void Write(string file, string json)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, file);
        // a unique temp name: two threads may save the same file at once (launch counter vs. rescan)
        var tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
