using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.ViewModels;

namespace TypographyFontFileViewer.Controls;

/// <summary>
/// One cell of the glyph map, which fetches its own picture and gives up when recycled.
/// </summary>
/// <remarks>
/// The same arrangement as the gallery's rows, and for the same reason: a CJK font puts
/// tens of thousands of cells in the list and only the ones on screen may cost anything.
/// A recycled cell can receive a result meant for the glyph it used to show, so every
/// result is checked against the glyph the cell is displaying now.
/// </remarks>
public sealed class GlyphCellImage : Image
{
    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
        nameof(Map), typeof(GlyphMapViewModel), typeof(GlyphCellImage),
        new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(GlyphEntry?), typeof(GlyphCellImage),
        new PropertyMetadata(null, OnInputChanged));

    /// <summary>Changes when every cell must be drawn again — new size or colours.</summary>
    public static readonly DependencyProperty EpochProperty = DependencyProperty.Register(
        nameof(Epoch), typeof(int), typeof(GlyphCellImage),
        new PropertyMetadata(0, OnInputChanged));

    private CancellationTokenSource? _pending;

    public GlyphCellImage()
    {
        Stretch = Stretch.None;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        Loaded += (_, _) => QueueRender();
        Unloaded += (_, _) => Cancel();
    }

    public GlyphMapViewModel? Map
    {
        get => (GlyphMapViewModel?)GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public GlyphEntry? Glyph
    {
        get => (GlyphEntry?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public int Epoch
    {
        get => (int)GetValue(EpochProperty);
        set => SetValue(EpochProperty, value);
    }

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((GlyphCellImage)d).QueueRender();

    private void QueueRender()
    {
        Cancel();

        if (Map is not { } map || Glyph is not { } glyph)
        {
            Source = null;
            return;
        }

        var request = new CancellationTokenSource();
        _pending = request;

        _ = RenderAsync(map, glyph, request.Token);
    }

    private async Task RenderAsync(GlyphMapViewModel map, GlyphEntry glyph, CancellationToken cancellationToken)
    {
        try
        {
            BitmapSource? bitmap = await map.RenderCellAsync(glyph, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            if (Glyph is { } current && current.Equals(glyph)) Source = bitmap;
        }
        catch (OperationCanceledException)
        {
            // Recycled or superseded.
        }
        catch (Exception exception) when (exception is FontLoadException or ObjectDisposedException)
        {
            if (Glyph is { } current && current.Equals(glyph)) Source = null;
        }
    }

    private void Cancel()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _pending, null);
        // Cancelled but not disposed: another thread may still be holding this token
        // source, and disposing one out from under an in-flight request is how a
        // superseded render turns into an ObjectDisposedException. It has nothing
        // unmanaged to release, so letting the collector have it is enough.
        previous?.Cancel();
    }
}
