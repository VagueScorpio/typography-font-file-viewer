using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using TypographyFontFileViewer.ViewModels;

namespace TypographyFontFileViewer;

public partial class MainWindow : Window
{
    private readonly RenderService _renderService = new();
    private readonly SettingsService _settings;
    private readonly FavoritesService _favorites;
    private readonly MainViewModel _model;

    public MainWindow()
    {
        InitializeComponent();

        // The XAML carries a title so the designer has something to show; the real one
        // comes from the assembly version, so a version bump touches only the csproj.
        // The status bar's corner is filled from the same place for the same reason.
        Title = AppInfo.NameAndVersion;
        VersionLabel.Text = $"v{AppInfo.Version}";

        // Everything the app keeps lives beside the exe. Environment.ProcessPath is the
        // one that works in a single-file build; Assembly.Location is empty there.
        string appFolder = Path.GetDirectoryName(Environment.ProcessPath)
                        ?? AppContext.BaseDirectory;

        _settings = new SettingsService(SettingsService.DefaultPathFor(appFolder));
        _settings.Load();

        _favorites = new FavoritesService(
            FavoritesService.DefaultDirectoryFor(appFolder),
            new HashService(),
            new RecycleBinFileRecycler(),
            _settings);

        _model = new MainViewModel(_renderService, _favorites)
        {
            PickFontFile = PickFontFile,
            PickFolder = PickFolder,
        };

        if (_settings.LastProblem is { Length: > 0 } problem) _model.Message = problem;

        DataContext = _model;

        Loaded += OnLoaded;
        DpiChanged += (_, _) => UpdateDpi();

        // Closing the window is the usual way out, but it is not the only one: shutting
        // Windows down with the app open should not lose what the user had set up.
        if (Application.Current is { } application)
            application.SessionEnding += (_, _) => SaveSettings();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateDpi();
        RestoreWindowBounds();
        _model.ApplySettings(_settings.Current);

        // The folder is the truth, so bring the index into line with it before anything
        // asks which fonts are starred.
        await _favorites.ReconcileAsync();

        // A file on the command line is what the user just asked for, so it wins over
        // whatever they were looking at last time.
        if (App.StartupPath is { Length: > 0 } startup)
            await _model.OpenPathAsync(startup);
        else
            await _model.RestoreLastSessionAsync(_settings.Current);
    }

    /// <summary>
    /// Puts the window back where it was, if that is still somewhere visible.
    /// </summary>
    /// <remarks>
    /// A saved position can point at a monitor that has since been unplugged. Rather
    /// than open off-screen, anything that no longer overlaps a screen falls back to
    /// the default position.
    /// </remarks>
    private void RestoreWindowBounds()
    {
        AppSettings settings = _settings.Current;

        if (settings.WindowWidth is not { } width || settings.WindowHeight is not { } height)
            return;
        if (width < MinWidth || height < MinHeight) return;

        if (settings.WindowLeft is { } left && settings.WindowTop is { } top)
        {
            var proposed = new Rect(left, top, width, height);
            if (IsOnScreen(proposed))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        Width = width;
        Height = height;

        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private static bool IsOnScreen(Rect window)
    {
        var desktop = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        Rect overlap = Rect.Intersect(window, desktop);

        // Enough of it visible to grab hold of.
        return overlap != Rect.Empty && overlap.Width >= 120 && overlap.Height >= 80;
    }

    /// <summary>Writes everything worth remembering back to the settings file.</summary>
    private void SaveSettings()
    {
        AppSettings settings = _settings.Current;

        // RestoreBounds is where the window would be if it were not maximized, which is
        // what should come back when it is un-maximized next time.
        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds.Width > 0 && bounds.Height > 0)
        {
            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
        }

        settings.WindowMaximized = WindowState == WindowState.Maximized;

        _model.CaptureSettings(settings);
        _settings.Save();
    }

    /// <summary>Ctrl+F focuses the search box; Esc inside it clears and lets go.</summary>
    private void OnSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;

        _model.ClearSearchCommand.Execute(null);
        System.Windows.Input.Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void OnFocusSearch(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>
    /// Tells the preview which monitor it is on, so a point size is a real point size.
    /// </summary>
    private void UpdateDpi()
        => _model.Preview.Dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;

    // --- Dialogs ---------------------------------------------------------------

    private string? PickFontFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a font",
            Filter = BuildFilter(),
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = _model.CurrentFolder,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string? PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Open a folder of fonts",
            Multiselect = false,
            InitialDirectory = _model.CurrentFolder,
        };

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    /// <summary>Builds the file dialog's filter from the formats the app actually opens.</summary>
    private static string BuildFilter()
    {
        const string All =
            "*.ttf;*.otf;*.ttc;*.otc;*.woff;*.woff2;*.pfb;*.pfa;*.fon;*.fnt;*.bdf;*.pcf;*.dfont";

        return string.Join("|",
        [
            $"All fonts|{All}",
            "TrueType and OpenType|*.ttf;*.otf;*.ttc;*.otc",
            "Web fonts|*.woff;*.woff2",
            "PostScript Type 1|*.pfb;*.pfa",
            "Bitmap fonts|*.fon;*.fnt;*.bdf;*.pcf",
            "All files|*.*",
        ]);
    }

    // --- Drag and drop ---------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedPath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        string? path = GetDroppedPath(e);
        e.Handled = true;

        if (path is not null) await _model.OpenPathAsync(path);
    }

    /// <summary>
    /// The first thing worth opening out of a drop: a folder, or a font file.
    /// </summary>
    /// <remarks>
    /// Dropping several files is treated as dropping the first one, since opening any
    /// font opens its whole folder anyway.
    /// </remarks>
    private static string? GetDroppedPath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return null;

        foreach (string path in paths)
        {
            if (Directory.Exists(path)) return path;
            if (File.Exists(path) && FormatSniffer.IsSupportedFileName(Path.GetFileName(path)))
                return path;
        }

        return null;
    }

    // --- Menu ------------------------------------------------------------------

    private void OnBiggerClick(object sender, RoutedEventArgs e) => _model.AdjustSize(2);

    private void OnSmallerClick(object sender, RoutedEventArgs e) => _model.AdjustSize(-2);

    /// <summary>Double-clicking a gallery row opens that font in the single-font view.</summary>
    private void OnGalleryRowActivated(object? sender, EventArgs e)
        => _model.OpenInSingleView(_model.SelectedFont);

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            $"{AppInfo.NameAndVersion}\n\n" +
            "A portable font viewer. Nothing is installed and nothing is written to the registry.\n\n" +
            $"Rendering by FreeType {_renderService.NativeVersion}, shaping by HarfBuzz.\n\n" +
            "FreeTypeSharp, HarfBuzzSharp and CommunityToolkit.Mvvm are used under the "
            + "MIT License; FreeType itself under the FreeType License (FTL).\n\n" +
            AppInfo.FreeTypeCredit,
            $"About {AppInfo.Name}",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // While the window still knows where it is.
        SaveSettings();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _model.Dispose();
        _renderService.Dispose();
        base.OnClosed(e);
    }
}
