using System.IO;
using System.Text.Json;

namespace WinlyStart.Core;

/// <summary>
/// Export / import of everything Winly Start keeps for the user: settings (menu height, tile columns,
/// list width, theme, rail…), the tile layout (groups, tile sizes and positions), items added by
/// hand (programs, files, websites), launch counters and calendar notes. One JSON document embeds the
/// data files verbatim under "files", so a backup stays readable and survives model changes: every
/// file is round-tripped through its own model before anything is written.
/// </summary>
public static class Backup
{
    public static readonly string[] Files = { "settings.json", "tiles.json", "custom.json", "usage.json", "calendar.json" };
    public const string Filter = "Winly Start backup (*.winlystart.json)|*.winlystart.json|JSON (*.json)|*.json";
    public static string DefaultName => $"WinlyStart-{DateTime.Now:yyyy-MM-dd}.winlystart.json";

    /// <param name="settingsJson">The live settings, serialised by the caller (the file on disk may lag).</param>
    public static void Export(string path, string settingsJson)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("app", "Winly Start");
            w.WriteString("version", typeof(Backup).Assembly.GetName().Version?.ToString(3));
            w.WriteString("exported", DateTime.Now.ToString("o"));
            w.WritePropertyName("files");
            w.WriteStartObject();
            foreach (var f in Files)
            {
                string? json = f == "settings.json" ? settingsJson : ReadValid(Path.Combine(Store.Dir, f));
                if (json == null) continue;
                w.WritePropertyName(f);
                w.WriteRawValue(json);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        File.WriteAllBytes(path, ms.ToArray());
    }

    /// <summary>Checks the whole file first, then writes every embedded data file into the store.
    /// Returns the names restored. Throws <see cref="InvalidDataException"/> for anything that is not
    /// a Winly Start backup; the store is untouched in that case.</summary>
    public static List<string> Import(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("This is not a Winly Start backup file.");

        var pending = new List<(string Name, string Json)>();
        foreach (var p in files.EnumerateObject())
        {
            if (Array.IndexOf(Files, p.Name) < 0 || p.Value.ValueKind != JsonValueKind.Object) continue;
            string json = p.Value.GetRawText();
            try
            {
                switch (p.Name)
                {
                    case "settings.json": JsonSerializer.Deserialize(json, JsonCtx.Default.Settings); break;
                    case "tiles.json": JsonSerializer.Deserialize(json, JsonCtx.Default.TileLayout); break;
                    case "custom.json": JsonSerializer.Deserialize(json, JsonCtx.Default.CustomItems); break;
                    case "usage.json": JsonSerializer.Deserialize(json, JsonCtx.Default.UsageData); break;
                    case "calendar.json": JsonSerializer.Deserialize(json, JsonCtx.Default.CalendarData); break;
                }
            }
            catch (JsonException ex) { throw new InvalidDataException($"{p.Name} in this backup is damaged: {ex.Message}"); }
            pending.Add((p.Name, json));
        }
        if (pending.Count == 0) throw new InvalidDataException("The backup contains nothing to restore.");

        var restored = new List<string>();
        foreach (var (name, json) in pending) { Store.WriteRaw(name, json); restored.Add(name); }
        return restored;
    }

    private static string? ReadValid(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            using var _ = JsonDocument.Parse(text);
            return text;
        }
        catch { return null; }   // a damaged file is simply left out of the backup
    }
}
