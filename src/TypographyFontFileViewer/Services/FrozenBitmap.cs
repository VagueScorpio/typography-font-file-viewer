using System.Windows.Media;
using System.Windows.Media.Imaging;
using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Services;

/// <summary>
/// Wraps the engine's bytes in a frozen bitmap.
/// </summary>
/// <remarks>
/// Freezing is what makes the result safe to hand to WPF from anywhere, and lets the
/// renderer skip its own locking. The DPI is baked in so the image occupies the right
/// number of device-independent units without any scaling.
/// </remarks>
public static class FrozenBitmap
{
    /// <summary>A rendered line or block of text, or null when there is nothing in it.</summary>
    public static BitmapSource? From(RenderedText rendered, double dpi)
        => From(rendered.Bgra, rendered.Width, rendered.Height, rendered.Stride, dpi);

    /// <summary>One glyph-map cell, or null when there is nothing in it.</summary>
    public static BitmapSource? From(GlyphCellBitmap cell, double dpi)
        => From(cell.Bgra, cell.Width, cell.Height, cell.Stride, dpi);

    private static BitmapSource? From(byte[] bgra, int width, int height, int stride, double dpi)
    {
        if (width <= 0 || height <= 0) return null;

        var bitmap = BitmapSource.Create(
            width, height, dpi, dpi, PixelFormats.Bgra32, null, bgra, stride);

        bitmap.Freeze();
        return bitmap;
    }
}
