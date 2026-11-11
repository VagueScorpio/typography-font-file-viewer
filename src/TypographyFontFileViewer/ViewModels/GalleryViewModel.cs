using System.ComponentModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;

namespace TypographyFontFileViewer.ViewModels;

/// <summary>
/// The All Fonts tab: every font in the folder, each drawn in its own face.
/// </summary>
/// <remarks>
/// <para>
/// The rows are the same objects the sidebar shows, so selection and the metadata pass
/// are shared rather than duplicated. What this class adds is the rendering: a size of
/// its own, a bitmap cache, and the settings each row needs.
/// </para>
/// <para>
/// Rows do not render themselves from here. Each visible row's control asks for its own
/// bitmap when it is realised and cancels when it is recycled, which is what keeps a
/// folder of two thousand fonts to a few dozen live requests.
/// </para>
/// </remarks>
public sealed partial class GalleryViewModel : ObservableObject, IDisposable
{
    public const double MinimumSize = 8;
    public const double MaximumSize = 96;

    /// <summary>
    /// The four named sizes, as points.
    /// </summary>
    /// <remarks>
    /// These are positions on the same scale the slider moves along rather than a
    /// separate setting, so picking Large and dragging the slider to 44 do the same
    /// thing, and a preset lights up whenever the slider lands on it. Small is chosen to
    /// fit as many fonts on screen as stay legible; Extra Large is for judging a single
    /// face's detail without leaving the gallery.
    /// </remarks>
    public const double SmallSize = 16;
    public const double MediumSize = 28;
    public const double LargeSize = 44;
    public const double ExtraLargeSize = 72;

    public static IReadOnlyList<(string Name, double Size)> SizePresets { get; } =
    [
        ("Small", SmallSize),
        ("Medium", MediumSize),
        ("Large", LargeSize),
        ("Extra large", ExtraLargeSize),
    ];

    private readonly RenderService _renderService;
    private readonly PreviewViewModel _preview;
    private bool _disposed;

    public GalleryViewModel(RenderService renderService, PreviewViewModel preview)
    {
        _renderService = renderService;
        _preview = preview;

        // The gallery draws the same words as the preview, at its own size.
        _preview.PropertyChanged += OnPreviewChanged;
    }

    public RenderedBitmapCache Cache { get; } = new();

    /// <summary>
    /// Bumped whenever something changes that every visible row must redraw for.
    /// </summary>
    /// <remarks>
    /// Rows watch this one number rather than the half-dozen settings behind it, so a
    /// change to the text and the size at once asks each row to redraw once, not twice.
    /// </remarks>
    [ObservableProperty]
    public partial int Epoch { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowHeight))]
    [NotifyPropertyChangedFor(nameof(SizeName))]
    public partial double GallerySize { get; set; } = MediumSize;

    /// <summary>The name of the current size, or the number when it is between presets.</summary>
    public string SizeName
    {
        get
        {
            foreach ((string name, double size) in SizePresets)
                if (Math.Abs(GallerySize - size) < 0.5) return name;

            return $"{GallerySize:0} pt";
        }
    }

    /// <summary>A uniform row height keeps the virtualized scrollbar honest.</summary>
    public double RowHeight => Math.Ceiling(GallerySize * 1.7) + 18;

    public string Text => _preview.PreviewText;

    public bool DarkBackground => _preview.DarkBackground;

    public double Dpi => _preview.Dpi;

    public System.Windows.Media.Brush Background => _preview.PreviewBackground;

    partial void OnGallerySizeChanged(double value) => Redraw();

    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PreviewViewModel.PreviewText):
                OnPropertyChanged(nameof(Text));
                Redraw();
                break;

            case nameof(PreviewViewModel.DarkBackground):
                OnPropertyChanged(nameof(DarkBackground));
                OnPropertyChanged(nameof(Background));
                Redraw();
                break;

            case nameof(PreviewViewModel.Dpi):
                OnPropertyChanged(nameof(Dpi));
                Redraw();
                break;
        }
    }

    /// <summary>Asks every realised row to draw itself again.</summary>
    public void Redraw() => Epoch++;

    public void AdjustSize(double delta)
        => GallerySize = Math.Clamp(Math.Round(GallerySize + delta), MinimumSize, MaximumSize);

    /// <summary>
    /// Renders one row, from the cache when possible.
    /// </summary>
    /// <remarks>
    /// Rows are rendered at Low priority, so a thousand of them queued by a fast scroll
    /// still cannot delay the selected font's preview.
    /// </remarks>
    public async Task<BitmapSource?> RenderRowAsync(FaceKey face, CancellationToken cancellationToken)
    {
        if (_disposed) return null;

        double dpi = Dpi <= 0 ? 96 : Dpi;
        int pixelSize = RenderService.ToPixelSize(GallerySize, dpi);

        var key = new GalleryRenderKey(face, Text, pixelSize, DarkBackground);

        BitmapSource? cached = Cache.TryGet(key);
        if (cached is not null) return cached;

        RenderedText rendered = await _renderService.RenderTextAsync(
            face, Text, GallerySize, dpi,
            maxWidthPx: 0,                       // one line, clipped by the row
            DarkBackground ? RenderStyle.Dark : RenderStyle.Light,
            JobPriority.Low, cancellationToken);

        if (cancellationToken.IsCancellationRequested) return null;
        if (rendered.Width <= 0 || rendered.Height <= 0) return null;

        var bitmap = BitmapSource.Create(
            rendered.Width, rendered.Height, dpi, dpi,
            System.Windows.Media.PixelFormats.Bgra32, null, rendered.Bgra, rendered.Stride);
        bitmap.Freeze();

        Cache.Add(key, bitmap);
        return bitmap;
    }

    /// <summary>Forgets cached rows for a file that has changed on disk.</summary>
    public void Invalidate(string path)
    {
        Cache.InvalidatePath(path);
        Redraw();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _preview.PropertyChanged -= OnPreviewChanged;
        Cache.Clear();
    }
}
