using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;

namespace TypographyFontFileViewer;

public partial class MainWindow : Window
{
    private const string SampleText =
        "The quick brown fox jumps over the lazy dog. " +
        "Pack my box with five dozen liquor jugs.";

    private const double SamplePointSize = 36;

    private readonly RenderService _renderService = new();
    private CancellationTokenSource? _pendingRender;
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DpiChanged += (_, _) => _ = ShowFontAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        await ShowFontAsync();
    }

    /// <summary>Re-wraps the preview when the window is resized.</summary>
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_loaded && e.WidthChanged) _ = ShowFontAsync();
    }

    /// <summary>
    /// Drives the whole engine once: open a font, read its metadata, lay text out, and
    /// show the pixels that come back.
    /// </summary>
    private async Task ShowFontAsync()
    {
        // A new request supersedes whatever was in flight.
        CancellationTokenSource request = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _pendingRender, request);
        previous?.Cancel();
        previous?.Dispose();

        CancellationToken cancellationToken = request.Token;
        var timer = Stopwatch.StartNew();
        string fontPath = FindSampleFont();

        try
        {
            await _renderService.Ready;

            FaceSummary[] summaries = await _renderService.ReadSummariesAsync(
                fontPath, JobPriority.High, cancellationToken);

            FaceSummary summary = summaries[0];
            if (summary.IsBroken)
            {
                ShowFailure(fontPath, summary.Error ?? "Unknown error.");
                return;
            }

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
            int wrapWidth = Math.Max(120, (int)((ActualWidth - 80) * dpi / 96.0));

            var key = FaceKey.For(fontPath, summary.FaceIndex);

            RenderedText rendered = await _renderService.RenderTextAsync(
                key, SampleText, SamplePointSize, dpi, wrapWidth,
                RenderStyle.Light, JobPriority.High, cancellationToken);

            FontInfo info = await _renderService.ReadInfoAsync(
                key, JobPriority.High, cancellationToken);

            if (cancellationToken.IsCancellationRequested) return;

            // The engine hands over bytes; the UI thread owns every WPF object.
            var source = BitmapSource.Create(
                rendered.Width, rendered.Height, dpi, dpi,
                PixelFormats.Bgra32, null, rendered.Bgra, rendered.Stride);
            source.Freeze();

            PreviewImage.Source = source;
            ErrorText.Visibility = Visibility.Collapsed;

            SubtitleText.Text =
                $"{summary.DisplayName} · {summary.Format.Badge()} · {summary.GlyphCount:N0} glyphs · " +
                $"{rendered.GlyphCount} glyphs shaped onto {rendered.LineCount} line" +
                (rendered.LineCount == 1 ? "" : "s");

            DetailsText.Text = Describe(info, rendered);

            (int count, long bytes, int hits, int misses, _) = _renderService.CacheStatistics;
            StatusText.Text =
                $"{Path.GetFileName(fontPath)} · FreeType {_renderService.NativeVersion} · " +
                $"{SamplePointSize:0} pt at {dpi:0} DPI = {RenderService.ToPixelSize(SamplePointSize, dpi)} px · " +
                $"{timer.ElapsedMilliseconds} ms · cache {count} faces, {bytes / 1024 / 1024} MB, " +
                $"{hits} hits / {misses} misses";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request; the newer one owns the UI now.
        }
        catch (Exception exception)
        {
            ShowFailure(fontPath, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string Describe(FontInfo info, RenderedText rendered)
    {
        var text = new StringBuilder();

        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                text.AppendLine($"{label,-16}{value}");
        }

        Line("Outline format", info.OutlineFormat);
        Line("Full name", info.FullName);
        Line("Version", info.Version);
        Line("PostScript", info.PostScriptName);
        Line("Designer", info.Designer);
        Line("Manufacturer", info.Manufacturer);
        Line("Embedding", info.Embedding.ToString());
        Line("Units per em", info.UnitsPerEm.ToString());
        Line("Mapped chars", $"{info.MappedCharacterCount:N0} of {info.GlyphCount:N0} glyphs");
        Line("Charmaps", info.CharmapCount.ToString());
        Line("Kerning", info.HasKerning ? "yes" : "no");
        Line("Glyph names", info.HasGlyphNames ? "yes" : "no");
        Line("Colour", info.HasColor ? "yes" : "no");

        if (info.FixedSizes.Count > 0)
            Line("Fixed sizes", string.Join(", ", info.FixedSizes));

        if (info.VariationAxes.Count > 0)
        {
            Line("Variable", $"{info.VariationAxes.Count} axes, {info.NamedInstances.Count} named instances");
            foreach (VariationAxis axis in info.VariationAxes)
                Line("  axis", axis.ToString());
        }

        Line("Copyright", Shorten(info.Copyright, 120));
        Line("Licence", Shorten(info.License, 120));

        if (rendered.HasMissingGlyphs)
            Line("Note", "some characters had no glyph and were drawn as .notdef");

        return text.ToString().TrimEnd();
    }

    private static string? Shorten(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string collapsed = value.ReplaceLineEndings(" ").Trim();
        return collapsed.Length <= limit ? collapsed : collapsed[..limit] + "…";
    }

    private void ShowFailure(string fontPath, string message)
    {
        PreviewImage.Source = null;
        DetailsText.Text = string.Empty;
        SubtitleText.Text = "The engine could not open this font.";
        ErrorText.Text = $"{fontPath}\n\n{message}";
        ErrorText.Visibility = Visibility.Visible;
        StatusText.Text = "Failed — see the message above.";
    }

    /// <summary>Picks a font that exists on every Windows machine.</summary>
    private static string FindSampleFont()
    {
        string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string candidate in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf", "verdana.ttf" })
        {
            string path = Path.Combine(fontsFolder, candidate);
            if (File.Exists(path)) return path;
        }

        string? any = Directory.EnumerateFiles(fontsFolder, "*.ttf").FirstOrDefault();
        return any ?? Path.Combine(fontsFolder, "arial.ttf");
    }

    protected override void OnClosed(EventArgs e)
    {
        _pendingRender?.Cancel();
        _pendingRender?.Dispose();
        _renderService.Dispose();
        base.OnClosed(e);
    }
}
