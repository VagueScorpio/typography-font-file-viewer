namespace TypographyFontFileViewer.Core;

/// <summary>
/// The font formats v1.0 opens. Detected by magic bytes first, extension second,
/// so a .ttf that is really CFF-based still reports what it actually is.
/// </summary>
public enum FontFormat
{
    Unknown = 0,

    /// <summary>TrueType outlines: <c>00 01 00 00</c> or <c>true</c>.</summary>
    TrueType,

    /// <summary>OpenType with CFF (PostScript) outlines: <c>OTTO</c>.</summary>
    OpenTypeCff,

    /// <summary>TrueType/OpenType Collection: <c>ttcf</c>. Holds several faces.</summary>
    Collection,

    /// <summary>WOFF 1.0: <c>wOFF</c>. A zlib-compressed wrapper around SFNT.</summary>
    Woff,

    /// <summary>WOFF 2.0: <c>wOF2</c>. A Brotli-compressed, table-transformed wrapper.</summary>
    Woff2,

    /// <summary>PostScript Type 1, binary (.pfb) or ASCII (.pfa).</summary>
    Type1,

    /// <summary>Windows bitmap font: .fon (an NE/PE container) or a bare .fnt.</summary>
    WindowsBitmap,

    /// <summary>X11 bitmap font in the text BDF format.</summary>
    Bdf,

    /// <summary>X11 bitmap font in the binary PCF format, optionally gzipped.</summary>
    Pcf,

    /// <summary>Mac data-fork suitcase.</summary>
    DataFork,
}

public static class FontFormats
{
    /// <summary>The short badge shown in the font list.</summary>
    public static string Badge(this FontFormat format) => format switch
    {
        FontFormat.TrueType => "TTF",
        FontFormat.OpenTypeCff => "OTF",
        FontFormat.Collection => "TTC",
        FontFormat.Woff => "WOFF",
        FontFormat.Woff2 => "WOFF2",
        FontFormat.Type1 => "T1",
        FontFormat.WindowsBitmap => "FON",
        FontFormat.Bdf => "BDF",
        FontFormat.Pcf => "PCF",
        FontFormat.DataFork => "DFONT",
        _ => "?",
    };

    /// <summary>
    /// True when the format is, or unwraps to, an SFNT file — the ones HarfBuzz can
    /// shape. Everything else goes through the FreeType fallback path in TextLayout.
    /// </summary>
    public static bool IsSfnt(this FontFormat format) => format
        is FontFormat.TrueType or FontFormat.OpenTypeCff or FontFormat.Collection
        or FontFormat.Woff or FontFormat.Woff2 or FontFormat.DataFork;

    /// <summary>True for formats that only exist at fixed pixel sizes.</summary>
    public static bool IsBitmapOnly(this FontFormat format) => format
        is FontFormat.WindowsBitmap or FontFormat.Bdf or FontFormat.Pcf;

    /// <summary>True for the compressed web wrappers, which must be decoded to SFNT first.</summary>
    public static bool NeedsDecoding(this FontFormat format) => format
        is FontFormat.Woff or FontFormat.Woff2;

    /// <summary>True when one file can hold several faces.</summary>
    public static bool CanHoldManyFaces(this FontFormat format) => format
        is FontFormat.Collection or FontFormat.DataFork or FontFormat.WindowsBitmap;
}
