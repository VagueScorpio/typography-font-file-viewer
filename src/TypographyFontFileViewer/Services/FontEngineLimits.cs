namespace TypographyFontFileViewer.Services;

/// <summary>
/// How much the app is allowed to remember.
/// </summary>
/// <remarks>
/// <para>
/// The blueprint's target is under 400 MB with a 2,000-font folder open in the gallery.
/// Three caches compete for that: open faces, rendered gallery rows and rendered glyph
/// cells. Their first values were chosen separately and added up to 416 MB between them
/// — over the whole budget before the runtime, WPF and the bitmaps actually on screen
/// had taken anything.
/// </para>
/// <para>
/// They are set together here instead, leaving room for everything else. Each cache is
/// still bounded by a count as well as by bytes, because one 30 MB CJK face or one
/// 2,000-pixel row is worth a great many small ones.
/// </para>
/// </remarks>
public static class FontEngineLimits
{
    /// <summary>Open faces kept loaded. Scrolling back through a folder should stay instant.</summary>
    public const int FaceCacheCount = 48;

    /// <summary>Bytes of font data held open. A large CJK face alone is most of this.</summary>
    public const long FaceCacheBytes = 96L * 1024 * 1024;

    /// <summary>Gallery rows remembered, which is a few screens' worth either way.</summary>
    public const int GalleryRowCount = 250;

    public const long GalleryRowBytes = 40L * 1024 * 1024;

    /// <summary>Glyph cells remembered. Small squares, so the count can be generous.</summary>
    public const int GlyphCellCount = 2500;

    public const long GlyphCellBytes = 24L * 1024 * 1024;

    /// <summary>What the three caches may hold between them.</summary>
    public const long TotalCacheBytes = FaceCacheBytes + GalleryRowBytes + GlyphCellBytes;
}
