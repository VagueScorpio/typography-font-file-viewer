using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;

namespace TypographyFontFileViewer.ViewModels;

/// <summary>A named block of sample text in the preset dropdown.</summary>
/// <param name="Name">What the dropdown shows.</param>
/// <param name="Text">What gets rendered, or null for the user's own text.</param>
public sealed record PreviewPreset(string Name, string? Text)
{
    public override string ToString() => Name;
}

/// <summary>One line of the waterfall: the same text at one particular size.</summary>
public sealed partial class WaterfallRow(double pointSize) : ObservableObject
{
    public double PointSize { get; } = pointSize;

    public string Label => $"{PointSize:0}";

    [ObservableProperty]
    public partial BitmapSource? Image { get; set; }
}

/// <summary>
/// The Preview tab: the text, the size, and the rendered result.
/// </summary>
/// <remarks>
/// Every change — a keystroke, a drag of the slider, a resize of the window — asks for
/// a new render. Requests are debounced by 30 ms and each one cancels the last, so
/// holding down a key costs one render rather than thirty.
/// </remarks>
public sealed partial class PreviewViewModel : ObservableObject, IDisposable
{
    /// <summary>The sizes the waterfall shows, from the blueprint.</summary>
    private static readonly double[] WaterfallSizes = [12, 18, 24, 36, 48, 72];

    private const int DebounceMilliseconds = 30;

    public const double MinimumPointSize = 6;
    public const double MaximumPointSize = 288;

    private readonly RenderService _renderService;
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public PreviewViewModel(RenderService renderService)
    {
        _renderService = renderService;

        foreach (double size in WaterfallSizes)
            Waterfall.Add(new WaterfallRow(size));
    }

    public static IReadOnlyList<PreviewPreset> Presets { get; } =
    [
        new("Pangram", "The quick brown fox jumps over the lazy dog"),
        new("Pangram (2)", "Pack my box with five dozen liquor jugs"),
        new("Uppercase", "ABCDEFGHIJKLMNOPQRSTUVWXYZ"),
        new("Lowercase", "abcdefghijklmnopqrstuvwxyz"),
        new("Numerals", "0123456789"),
        new("Punctuation", "!@#$%^&*()[]{}<>?/\\|-_=+~`'\";:.,"),
        new("Accents", "ÁÉÍÓÚ áéíóú ÄÖÜ äöü Ññ Çç"),
        new("Custom", null),
    ];

    public ObservableCollection<WaterfallRow> Waterfall { get; } = [];

    /// <summary>The face being previewed, or null when nothing is selected.</summary>
    public FaceKey? Face
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            QueueRender();
        }
    }

    /// <summary>Sizes a bitmap-only font actually has; empty for scalable fonts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FixedSizeNotice))]
    [NotifyPropertyChangedFor(nameof(HasFixedSizes))]
    public partial IReadOnlyList<int> FixedSizes { get; set; } = [];

    public bool HasFixedSizes => FixedSizes.Count > 0;

    /// <summary>Tells the user why the size slider is not doing what they asked.</summary>
    public string FixedSizeNotice => FixedSizes.Count == 0
        ? string.Empty
        : $"Bitmap font — sizes: {string.Join(", ", FixedSizes)}";

    [ObservableProperty]
    public partial string PreviewText { get; set; } = Presets[0].Text!;

    [ObservableProperty]
    public partial double PointSize { get; set; } = 48;

    [ObservableProperty]
    public partial bool DarkBackground { get; set; }

    [ObservableProperty]
    public partial BitmapSource? MainImage { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>The monitor's DPI, so a point size means the same thing on every screen.</summary>
    [ObservableProperty]
    public partial double Dpi { get; set; } = 96;

    /// <summary>Width available for the preview, in device pixels. Drives word wrap.</summary>
    [ObservableProperty]
    public partial int AvailableWidthPx { get; set; } = 600;

    /// <summary>The background the preview panel itself should paint.</summary>
    public Brush PreviewBackground => DarkBackground
        ? new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14))
        : Brushes.White;

    private RenderStyle Style => DarkBackground ? RenderStyle.Dark : RenderStyle.Light;

    partial void OnPreviewTextChanged(string value) => QueueRender();

    partial void OnPointSizeChanged(double value) => QueueRender();

    partial void OnDpiChanged(double value) => QueueRender();

    partial void OnAvailableWidthPxChanged(int value) => QueueRender();

    partial void OnDarkBackgroundChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewBackground));
        QueueRender();
    }

    /// <summary>Picks a preset, leaving the user's own text alone for "Custom".</summary>
    public void ApplyPreset(PreviewPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Text is not null) PreviewText = preset.Text;
    }

    /// <summary>Nudges the size the way Ctrl+= and Ctrl+wheel do.</summary>
    public void AdjustSize(double delta)
        => PointSize = Math.Clamp(Math.Round(PointSize + delta), MinimumPointSize, MaximumPointSize);

    /// <summary>
    /// Asks for a new render, replacing any request still in flight.
    /// </summary>
    public void QueueRender()
    {
        if (_disposed) return;

        var request = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _pending, request);
        previous?.Cancel();
        previous?.Dispose();

        _ = RenderAsync(request.Token);
    }

    private async Task RenderAsync(CancellationToken cancellationToken)
    {
        FaceKey? face = Face;
        if (face is null)
        {
            MainImage = null;
            ErrorMessage = null;
            foreach (WaterfallRow row in Waterfall) row.Image = null;
            return;
        }

        try
        {
            // Let a burst of keystrokes collapse into one render.
            await Task.Delay(DebounceMilliseconds, cancellationToken);

            string text = PreviewText;
            double points = PointSize;
            double dpi = Dpi;
            int width = Math.Max(80, AvailableWidthPx);
            RenderStyle style = Style;

            RenderedText main = await _renderService.RenderTextAsync(
                face.Value, text, points, dpi, width, style, JobPriority.High, cancellationToken);

            if (cancellationToken.IsCancellationRequested) return;

            MainImage = ToBitmap(main, dpi);
            ErrorMessage = null;

            // The waterfall is background work: the big preview must never wait for it.
            foreach (WaterfallRow row in Waterfall)
            {
                if (cancellationToken.IsCancellationRequested) return;

                // Waterfall lines are clipped at the panel edge rather than wrapped, so
                // each size stays one line and the ladder of sizes reads down the page.
                RenderedText line = await _renderService.RenderTextAsync(
                    face.Value, text, row.PointSize, dpi, maxWidthPx: 0, style,
                    JobPriority.Low, cancellationToken);

                if (cancellationToken.IsCancellationRequested) return;
                row.Image = ToBitmap(line, dpi);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request, which now owns the UI.
        }
        catch (FontLoadException exception)
        {
            MainImage = null;
            foreach (WaterfallRow row in Waterfall) row.Image = null;
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            MainImage = null;
            foreach (WaterfallRow row in Waterfall) row.Image = null;
            ErrorMessage = $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    /// <summary>
    /// Wraps the engine's bytes in a frozen bitmap.
    /// </summary>
    /// <remarks>
    /// Freezing is what makes the result safe to hand to WPF from anywhere, and lets the
    /// renderer skip its own locking. The DPI is baked in so the image occupies the
    /// right number of device-independent units without any scaling.
    /// </remarks>
    private static BitmapSource? ToBitmap(RenderedText rendered, double dpi)
    {
        if (rendered.Width <= 0 || rendered.Height <= 0) return null;

        var bitmap = BitmapSource.Create(
            rendered.Width, rendered.Height, dpi, dpi,
            PixelFormats.Bgra32, null, rendered.Bgra, rendered.Stride);

        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}
