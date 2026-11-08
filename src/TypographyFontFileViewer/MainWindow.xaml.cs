using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Engine;

namespace TypographyFontFileViewer;

public partial class MainWindow : Window
{
    private const string SpikeText = "Hello";
    private const double SpikePointSize = 72;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DpiChanged += (_, _) => RunSpike();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RunSpike();

    /// <summary>
    /// Loads a font off disk, shapes a string through HarfBuzz, rasterizes it with
    /// FreeType and shows the result. If this works from a lone exe on a machine with
    /// no .NET and no fonts installed, the rest of the app is ordinary work.
    /// </summary>
    private void RunSpike()
    {
        var timer = Stopwatch.StartNew();
        string fontPath = FindSpikeFont();

        try
        {
            // Per-Monitor V2 is on, so ask the window where it actually is.
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
            int pixelSize = (int)Math.Round(SpikePointSize * dpi / 72.0);

            var summary = FreeTypeSpike.ReadFaceSummary(fontPath);
            RenderedBitmap rendered = FreeTypeSpike.Render(fontPath, SpikeText, pixelSize);

            // The engine hands over plain bytes; the UI thread owns every WPF object.
            var source = BitmapSource.Create(
                rendered.Width, rendered.Height,
                dpi, dpi,
                PixelFormats.Bgra32, null,
                rendered.Bgra, rendered.Stride);
            source.Freeze();

            SpikeImage.Source = source;
            ErrorText.Visibility = Visibility.Collapsed;

            SubtitleText.Text =
                $"{summary.Family} {summary.Style} · {summary.GlyphCount:N0} glyphs · " +
                $"{SpikeText.Length} characters shaped into {rendered.GlyphCount} glyphs by HarfBuzz";

            StatusText.Text =
                $"{Path.GetFileName(fontPath)} · {SpikePointSize:0} pt at {dpi:0} DPI = {pixelSize} px · " +
                $"{rendered.Width}×{rendered.Height} px rendered in {timer.ElapsedMilliseconds} ms · " +
                $"running from {AppContext.BaseDirectory}";
        }
        catch (Exception ex) when (ex is FontLoadException or IOException)
        {
            SpikeImage.Source = null;
            SubtitleText.Text = "The spike could not render.";
            ErrorText.Text = $"{fontPath}\n\n{ex.GetType().Name}: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
            StatusText.Text = "Failed — see the message above.";
        }
    }

    /// <summary>Picks a font that is on every Windows machine, newest install layout first.</summary>
    private static string FindSpikeFont()
    {
        string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string candidate in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf", "verdana.ttf" })
        {
            string path = Path.Combine(fontsFolder, candidate);
            if (File.Exists(path)) return path;
        }

        // Nothing standard found: name the first file we can see so the error is useful.
        string? any = Directory.EnumerateFiles(fontsFolder, "*.ttf").FirstOrDefault();
        return any ?? Path.Combine(fontsFolder, "arial.ttf");
    }
}
