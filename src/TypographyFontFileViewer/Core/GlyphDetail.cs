using System.Globalization;

namespace TypographyFontFileViewer.Core;

/// <summary>
/// Everything known about one glyph, for the overlay the glyph map opens on a
/// double-click.
/// </summary>
/// <remarks>
/// Measurements are in the font's own design units rather than pixels, because that is
/// what they mean: they are the same whatever size the glyph is being shown at, and
/// they are the numbers a type designer would recognise.
/// </remarks>
public sealed record GlyphDetail
{
    public required uint GlyphIndex { get; init; }

    /// <summary>The Unicode value that reaches this glyph, or -1 when none does.</summary>
    public required int Codepoint { get; init; }

    public string? Name { get; init; }

    /// <summary>Design units per em, the scale everything below is measured against.</summary>
    public int UnitsPerEm { get; init; }

    public int AdvanceWidth { get; init; }

    public int LeftSideBearing { get; init; }

    public int MinX { get; init; }
    public int MinY { get; init; }
    public int MaxX { get; init; }
    public int MaxY { get; init; }

    public int Width => MaxX - MinX;

    public int Height => MaxY - MinY;

    /// <summary>True when the glyph has no outline at all, as a space does.</summary>
    public bool IsBlank { get; init; }

    public bool IsMapped => Codepoint >= 0;

    /// <summary>The character itself, when there is one to show.</summary>
    public string? Character => IsMapped && Codepoint is not (>= 0 and <= 0x20)
        ? char.ConvertFromUtf32(Codepoint)
        : null;

    public string CodepointLabel => IsMapped
        ? "U+" + Codepoint.ToString(Codepoint > 0xFFFF ? "X6" : "X4", CultureInfo.InvariantCulture)
        : $"glyph #{GlyphIndex}";
}
