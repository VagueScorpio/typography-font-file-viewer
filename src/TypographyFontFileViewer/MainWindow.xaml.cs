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
    private readonly MainViewModel _model;

    public MainWindow()
    {
        InitializeComponent();

        _model = new MainViewModel(_renderService)
        {
            PickFontFile = PickFontFile,
            PickFolder = PickFolder,
        };

        DataContext = _model;

        Loaded += OnLoaded;
        DpiChanged += (_, _) => UpdateDpi();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateDpi();

        // Double-clicking a font in Explorer starts the app with that file.
        if (App.StartupPath is { Length: > 0 } startup)
            await _model.OpenPathAsync(startup);
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
            "Typography Font File Viewer 1.0\n\n" +
            "A portable font viewer. Nothing is installed and nothing is written to the registry.\n\n" +
            $"Rendering by FreeType {_renderService.NativeVersion}, shaping by HarfBuzz.\n\n" +
            "Portions of this software are copyright © 2026 The FreeType Project\n" +
            "(www.freetype.org). All rights reserved.",
            "About Typography Font File Viewer",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    protected override void OnClosed(EventArgs e)
    {
        _model.Dispose();
        _renderService.Dispose();
        base.OnClosed(e);
    }
}
