namespace TypographyFontFileViewer.Core;

/// <summary>
/// Which queue a request joins. The selected font's preview must never wait behind a
/// thousand gallery rows.
/// </summary>
public enum JobPriority
{
    /// <summary>The thing the user is looking at: the selected font's preview.</summary>
    High = 0,

    /// <summary>Background work: gallery rows, the metadata scan.</summary>
    Low = 1,
}

/// <summary>Colours a render uses, as 0xAARRGGBB.</summary>
public readonly record struct RenderStyle(uint Foreground, uint Background)
{
    /// <summary>Near-black on white.</summary>
    public static RenderStyle Light { get; } = new(0xFF1A1A1Au, 0xFFFFFFFFu);

    /// <summary>Near-white on near-black, for the dark preview toggle.</summary>
    public static RenderStyle Dark { get; } = new(0xFFF2F2F2u, 0xFF141414u);
}

/// <summary>
/// A BGRA image produced by the engine. The UI thread wraps this in a BitmapSource and
/// freezes it; no WPF type and no native handle ever crosses the engine boundary.
/// </summary>
public sealed record RenderedText
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int Stride => Width * 4;

    /// <summary>How many glyphs were drawn, after shaping.</summary>
    public int GlyphCount { get; init; }

    /// <summary>How many lines the text wrapped to.</summary>
    public int LineCount { get; init; }

    /// <summary>True when characters had no glyph and were drawn as .notdef.</summary>
    public bool HasMissingGlyphs { get; init; }

    public static RenderedText Empty { get; } = new()
    {
        Bgra = [],
        Width = 0,
        Height = 0,
    };
}

/// <summary>One entry in the glyph map.</summary>
/// <param name="GlyphIndex">Index into the font's glyph array.</param>
/// <param name="Codepoint">The Unicode value that maps here, or -1 when unmapped.</param>
/// <param name="Name">The glyph's own name, when the font carries names.</param>
public readonly record struct GlyphEntry(uint GlyphIndex, int Codepoint, string? Name)
{
    public bool IsMapped => Codepoint >= 0;

    public string Label => IsMapped ? $"U+{Codepoint:X4}" : $"#{GlyphIndex}";
}

/// <summary>One rendered cell of the glyph map.</summary>
public sealed record GlyphCellBitmap
{
    public required uint GlyphIndex { get; init; }
    public required int Codepoint { get; init; }
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int Stride => Width * 4;

    /// <summary>True when the font has no outline for this glyph at this size.</summary>
    public bool IsBlank { get; init; }
}
