using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Services;

/// <summary>
/// Identifies one rendered gallery row. Everything that changes the pixels is in here,
/// so a hit is always the right image.
/// </summary>
public readonly record struct GalleryRenderKey(
    FaceKey Face, string Text, int PixelSize, bool DarkBackground);

/// <summary>Identifies one rendered cell of the glyph map.</summary>
public readonly record struct GlyphCellKey(
    FaceKey Face, uint GlyphIndex, int CellPixels, bool DarkBackground);

/// <summary>The gallery's rendered rows.</summary>
public sealed class RenderedBitmapCache(int maximumEntries = 300, long maximumBytes = 96L * 1024 * 1024)
    : BitmapLruCache<GalleryRenderKey>(maximumEntries, maximumBytes, key => key.Face.Path);

/// <summary>
/// The glyph map's rendered cells.
/// </summary>
/// <remarks>
/// Allowed many more entries than the gallery, and far fewer bytes: a cell is a small
/// square, and a CJK font fills the screen with hundreds of them at once.
/// </remarks>
public sealed class GlyphCellCache(int maximumEntries = 4000, long maximumBytes = 64L * 1024 * 1024)
    : BitmapLruCache<GlyphCellKey>(maximumEntries, maximumBytes, key => key.Face.Path);
