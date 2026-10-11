using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinlyStart.Core;

namespace WinlyStart.UI;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Dialog.Apply(this);
        LoadFromSettings();
    }

    /// <summary>Fills every control from <see cref="App.Settings"/> (at open, and again after an import).</summary>
    private void LoadFromSettings()
    {
        var s = App.Settings;
        WinKey.IsChecked = s.ReplaceWinKey;
        StartButton.IsChecked = s.ReplaceStartButton;
        Autostart.IsChecked = s.StartWithWindows;
        if (Packaged.Is)
        {
            // Windows owns the startup switch for a packaged app (Settings › Apps › Startup), so
            // showing a checkbox we cannot honour would be a lie. Point at the real control instead.
            Autostart.IsEnabled = false;
            Autostart.Content = "Start with Windows — set in Settings › Apps › Startup";
            Autostart.ToolTip = "Winly Start was installed from the Microsoft Store. "
                              + "Windows manages startup for Store apps; open "
                              + "Settings › Apps › Startup to turn it on or off.";
        }
        ThemeBox.SelectedIndex = (int)s.Theme;
        // A size set by dragging the menu's grips (any column count, any height) must survive a visit
        // here that only changes something else: remember what the controls show and write back on change.
        while (ColumnsBox.Items.Count > 2) ColumnsBox.Items.RemoveAt(2);
        if (s.TileColumns is not (6 or 8))
            ColumnsBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = $"Custom — {s.TileColumns} units (set by dragging the menu)" });
        ColumnsBox.SelectedIndex = _loadedColsIndex = s.TileColumns == 8 ? 1 : s.TileColumns == 6 ? 0 : 2;
        HeightSlider.Value = s.MenuHeight;
        _loadedHeight = (int)HeightSlider.Value;     // after the slider's tick snapping
        AtCorner.IsChecked = s.OpenAtCorner;
        Recent.IsChecked = s.ShowRecentlyAdded;
        MostUsed.IsChecked = s.ShowMostUsed;
        Trim.IsChecked = s.TrimMemoryWhenHidden;

        var r = s.Rail;
        RailDocuments.IsChecked = r.Documents;
        RailDownloads.IsChecked = r.Downloads;
        RailMusic.IsChecked = r.Music;
        RailPictures.IsChecked = r.Pictures;
        RailVideos.IsChecked = r.Videos;
        RailNetwork.IsChecked = r.Network;
        RailPersonal.IsChecked = r.PersonalFolder;
        RailExplorer.IsChecked = r.FileExplorer;
        RailSettings.IsChecked = r.Settings;
        RailCalendar.IsChecked = r.Calendar;

        _profilePath = s.ProfileImagePath;
        ShowProfile();
    }

    // ───────────────────────── account picture ─────────────────────────

    private string _profilePath = string.Empty;

    private void ShowProfile()
    {
        string? path = _profilePath.Length > 0 && File.Exists(_profilePath) ? _profilePath : UserInfo.WindowsPicturePath;
        ProfileCaption.Text = _profilePath.Length > 0
            ? "Custom picture · shown on the user button in the left rail."
            : "Using your Windows account picture.";
        ProfileBrush.ImageSource = null;
        if (path == null) return;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.DecodePixelWidth = 128;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            ProfileBrush.ImageSource = bi;
        }
        catch { }
    }

    private void ChoosePicture_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an account picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        _profilePath = dlg.FileName;
        ShowProfile();
    }

    private void ResetPicture_Click(object sender, RoutedEventArgs e)
    {
        _profilePath = string.Empty;
        ShowProfile();
    }

    // IsCancel only closes windows opened with ShowDialog(); this window is shown with Show().
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ───────────────────────── export / import ─────────────────────────

    private int _loadedColsIndex, _loadedHeight;

    /// <summary>Everything in one file: settings (menu size, list width, theme, rail…), the tile
    /// layout (groups, sizes, positions), items added by hand, launch counters, calendar notes.</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Winly Start layout and settings",
            Filter = Backup.Filter, FileName = Backup.DefaultName, AddExtension = true, OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Backup.Export(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(App.Settings, JsonCtx.Default.Settings));
            BackupStatus.Text = "Exported to " + Path.GetFileName(dlg.FileName);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, "Could not write the file.\n\n" + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import Winly Start layout and settings", Filter = Backup.Filter, CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        if (MessageBox.Show(this,
                "Replace the current tile layout, added items and settings with the ones in\n" + Path.GetFileName(dlg.FileName) + "?",
                "Import", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var restored = Backup.Import(dlg.FileName);
            App.ReloadFromDisk();
            LoadFromSettings();           // the controls still showed the old values
            BackupStatus.Text = $"Imported {restored.Count} files from {Path.GetFileName(dlg.FileName)}";
            StartHook.Debug("import → " + string.Join(", ", restored));
        }
        catch (Exception ex)
        {
            if (ex is not InvalidDataException) App.Log(ex);
            MessageBox.Show(this, "Nothing was changed.\n\n" + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.ReplaceWinKey = WinKey.IsChecked == true;
        s.ReplaceStartButton = StartButton.IsChecked == true;
        s.StartWithWindows = Autostart.IsChecked == true;
        s.Theme = (ThemeMode)Math.Max(0, ThemeBox.SelectedIndex);
        if (ColumnsBox.SelectedIndex != _loadedColsIndex && ColumnsBox.SelectedIndex is 0 or 1) s.TileColumns = ColumnsBox.SelectedIndex == 1 ? 8 : 6;
        if ((int)HeightSlider.Value != _loadedHeight) s.MenuHeight = (int)HeightSlider.Value;
        s.OpenAtCorner = AtCorner.IsChecked == true;
        s.ShowRecentlyAdded = Recent.IsChecked == true;
        s.ShowMostUsed = MostUsed.IsChecked == true;
        s.TrimMemoryWhenHidden = Trim.IsChecked == true;

        var r = s.Rail;
        r.Documents = RailDocuments.IsChecked == true;
        r.Downloads = RailDownloads.IsChecked == true;
        r.Music = RailMusic.IsChecked == true;
        r.Pictures = RailPictures.IsChecked == true;
        r.Videos = RailVideos.IsChecked == true;
        r.Network = RailNetwork.IsChecked == true;
        r.PersonalFolder = RailPersonal.IsChecked == true;
        r.FileExplorer = RailExplorer.IsChecked == true;
        r.Settings = RailSettings.IsChecked == true;
        r.Calendar = RailCalendar.IsChecked == true;
        s.ProfileImagePath = _profilePath;

        App.ApplySettings();
        Close();
    }
}
