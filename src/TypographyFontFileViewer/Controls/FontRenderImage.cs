using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.ViewModels;

namespace TypographyFontFileViewer.Controls;

/// <summary>
/// An image that fetches its own rendering of a font, and gives up when it is no longer
/// on screen.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the gallery affordable. The list virtualizes and recycles its
/// containers, so only the rows actually visible exist; each one asks for its bitmap
/// when it is handed a font and cancels that request the moment it is handed a
/// different one. Scrolling quickly through two thousand fonts therefore leaves a
/// couple of dozen live requests behind it, not two thousand.
/// </para>
/// <para>
/// A recycled container can receive a result meant for the font it used to show, so
/// every result is checked against the font the control is displaying now before it is
/// painted.
/// </para>
/// </remarks>
public sealed class FontRenderImage : Image
{
    public static readonly DependencyProperty GalleryProperty = DependencyProperty.Register(
        nameof(Gallery), typeof(GalleryViewModel), typeof(FontRenderImage),
        new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty FaceProperty = DependencyProperty.Register(
        nameof(Face), typeof(FaceKey?), typeof(FontRenderImage),
        new PropertyMetadata(null, OnInputChanged));

    /// <summary>Changes whenever every row needs redrawing — new text, size or colours.</summary>
    public static readonly DependencyProperty EpochProperty = DependencyProperty.Register(
        nameof(Epoch), typeof(int), typeof(FontRenderImage),
        new PropertyMetadata(0, OnInputChanged));

    private CancellationTokenSource? _pending;

    public FontRenderImage()
    {
        Stretch = Stretch.None;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;

        Loaded += (_, _) => QueueRender();
        Unloaded += (_, _) => Cancel();
    }

    public GalleryViewModel? Gallery
    {
        get => (GalleryViewModel?)GetValue(GalleryProperty);
        set => SetValue(GalleryProperty, value);
    }

    public FaceKey? Face
    {
        get => (FaceKey?)GetValue(FaceProperty);
        set => SetValue(FaceProperty, value);
    }

    public int Epoch
    {
        get => (int)GetValue(EpochProperty);
        set => SetValue(EpochProperty, value);
    }

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FontRenderImage)d).QueueRender();

    private void QueueRender()
    {
        Cancel();

        if (Gallery is not { } gallery || Face is not { } face)
        {
            Source = null;
            return;
        }

        var request = new CancellationTokenSource();
        _pending = request;

        _ = RenderAsync(gallery, face, request.Token);
    }

    private async Task RenderAsync(GalleryViewModel gallery, FaceKey face, CancellationToken cancellationToken)
    {
        try
        {
            BitmapSource? bitmap = await gallery.RenderRowAsync(face, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            // The container may have been recycled onto another font while this ran.
            if (Face is { } current && current.Equals(face)) Source = bitmap;
        }
        catch (OperationCanceledException)
        {
            // Recycled or superseded; the newer request owns this control now.
        }
        catch (Exception exception) when (exception is FontLoadException or ObjectDisposedException)
        {
            // A font that will not render shows as a blank row; the list already marks
            // it with a warning and the reason.
            if (Face is { } current && current.Equals(face)) Source = null;
        }
    }

    private void Cancel()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _pending, null);
        previous?.Cancel();
        previous?.Dispose();
    }
}
