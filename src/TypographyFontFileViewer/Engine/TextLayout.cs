using System.Globalization;
using FreeTypeSharp;
using HarfBuzzSharp;
using TypographyFontFileViewer.Core;
using static FreeTypeSharp.FT;
using HbBuffer = HarfBuzzSharp.Buffer;

namespace TypographyFontFileViewer.Engine;

/// <summary>A glyph placed on the canvas. Pen positions are in 26.6 fixed point.</summary>
internal readonly record struct PositionedGlyph(uint GlyphIndex, int PenX26, int PenY26, bool IsMissing);

/// <summary>The result of laying text out: where every glyph goes, and how big the box is.</summary>
internal sealed class LaidOutText
{
    public required IReadOnlyList<PositionedGlyph> Glyphs { get; init; }

    /// <summary>Distance between baselines, in pixels.</summary>
    public required int LineHeight { get; init; }

    /// <summary>Distance from the top of the line box down to the first baseline, in pixels.</summary>
    public required int Ascent { get; init; }

    public required int Descent { get; init; }

    public required int LineCount { get; init; }

    /// <summary>Width of the widest line, in pixels.</summary>
    public required int AdvanceWidth { get; init; }

    /// <summary>True when some character had no glyph and was drawn as .notdef.</summary>
    public required bool HasMissingGlyphs { get; init; }

    public static LaidOutText Empty { get; } = new()
    {
        Glyphs = [],
        LineHeight = 0,
        Ascent = 0,
        Descent = 0,
        LineCount = 0,
        AdvanceWidth = 0,
        HasMissingGlyphs = false,
    };
}

/// <summary>
/// Turns a string into placed glyphs: HarfBuzz for SFNT fonts, FreeType's own cmap and
/// kerning for everything else.
/// </summary>
/// <remarks>
/// <para>
/// HarfBuzz is what makes ligatures, GPOS kerning and complex scripts come out right,
/// but it only understands SFNT. Type 1, BDF, PCF and Windows FNT fonts go down the
/// fallback path, which maps characters one at a time and applies the font's own kern
/// table where it has one.
/// </para>
/// <para>
/// Both paths produce the same intermediate form, so line breaking and placement are
/// written once.
/// </para>
/// </remarks>
internal static unsafe class TextLayout
{
    /// <summary>A shaped glyph before it has been assigned to a line. 26.6 throughout.</summary>
    private readonly record struct ShapedGlyph(
        uint GlyphIndex, int XAdvance26, int XOffset26, int YOffset26, int Cluster, bool IsMissing);

    /// <summary>
    /// Lays <paramref name="text"/> out through <paramref name="face"/>.
    /// </summary>
    /// <param name="maxWidthPx">Wrap width in pixels; 0 or less means never wrap.</param>
    public static LaidOutText Layout(LoadedFace face, string text, int maxWidthPx)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(text);

        (int lineHeight, int ascent, int descent) = ReadLineMetrics(face);

        if (text.Length == 0)
        {
            return new LaidOutText
            {
                Glyphs = [],
                LineHeight = lineHeight,
                Ascent = ascent,
                Descent = descent,
                LineCount = 1,
                AdvanceWidth = 0,
                HasMissingGlyphs = false,
            };
        }

        var placed = new List<PositionedGlyph>(text.Length + 8);
        int lineIndex = 0;
        int widestLine26 = 0;
        bool anyMissing = false;

        foreach (string paragraph in SplitLines(text))
        {
            IReadOnlyList<ShapedGlyph> shaped = Shape(face, paragraph);
            foreach (ShapedGlyph glyph in shaped)
                if (glyph.IsMissing) anyMissing = true;

            foreach (List<ShapedGlyph> line in BreakIntoLines(shaped, paragraph, maxWidthPx))
            {
                int pen26 = 0;
                int baseline26 = lineIndex * lineHeight * 64;

                foreach (ShapedGlyph glyph in line)
                {
                    placed.Add(new PositionedGlyph(
                        glyph.GlyphIndex,
                        pen26 + glyph.XOffset26,
                        baseline26 - glyph.YOffset26,   // screen y runs down, HarfBuzz y runs up
                        glyph.IsMissing));

                    pen26 += glyph.XAdvance26;
                }

                widestLine26 = Math.Max(widestLine26, pen26);
                lineIndex++;
            }
        }

        return new LaidOutText
        {
            Glyphs = placed,
            LineHeight = lineHeight,
            Ascent = ascent,
            Descent = descent,
            LineCount = Math.Max(1, lineIndex),
            AdvanceWidth = Round26(widestLine26),
            HasMissingGlyphs = anyMissing,
        };
    }

    /// <summary>Measures a single line without placing it, for the gallery and waterfall.</summary>
    public static int MeasureWidth(LoadedFace face, string text)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (string.IsNullOrEmpty(text)) return 0;

        int total26 = 0;
        foreach (ShapedGlyph glyph in Shape(face, text))
            total26 += glyph.XAdvance26;

        return Round26(total26);
    }

    // --- Shaping ---------------------------------------------------------------

    private static IReadOnlyList<ShapedGlyph> Shape(LoadedFace face, string text)
    {
        if (text.Length == 0) return [];

        return face.HbFont is not null
            ? ShapeWithHarfBuzz(face, text)
            : ShapeWithFreeType(face, text);
    }

    private static IReadOnlyList<ShapedGlyph> ShapeWithHarfBuzz(LoadedFace face, string text)
    {
        using var buffer = new HbBuffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        face.HbFont!.Shape(buffer);

        ReadOnlySpan<GlyphInfo> infos = buffer.GetGlyphInfoSpan();
        ReadOnlySpan<GlyphPosition> positions = buffer.GetGlyphPositionSpan();

        var shaped = new List<ShapedGlyph>(infos.Length);
        for (int i = 0; i < infos.Length; i++)
        {
            GlyphPosition position = positions[i];
            uint glyphIndex = infos[i].Codepoint;   // a glyph id after shaping, not a character

            shaped.Add(new ShapedGlyph(
                glyphIndex,
                position.XAdvance,
                position.XOffset,
                position.YOffset,
                (int)infos[i].Cluster,
                IsMissing: glyphIndex == 0));
        }

        return shaped;
    }

    /// <summary>
    /// The path for fonts HarfBuzz cannot take: Type 1, BDF, PCF and Windows FNT.
    /// </summary>
    private static IReadOnlyList<ShapedGlyph> ShapeWithFreeType(LoadedFace face, string text)
    {
        var shaped = new List<ShapedGlyph>(text.Length);
        FT_FaceRec_* ftFace = face.Face;
        uint previousGlyph = 0;

        for (int i = 0; i < text.Length; i++)
        {
            // Step by whole code points so characters outside the BMP are not split.
            int codepoint = char.ConvertToUtf32(text, i);
            int cluster = i;
            if (char.IsHighSurrogate(text[i])) i++;

            // Not a plain Unicode lookup: Windows FNT and friends index by byte value,
            // and symbol fonts hide their glyphs in the private use area.
            uint glyphIndex = face.GetGlyphIndex(codepoint);
            bool missing = glyphIndex == 0;

            int advance26 = 0;
            if (FT_Load_Glyph(ftFace, glyphIndex, FT_LOAD.FT_LOAD_DEFAULT) == FT_Error.FT_Err_Ok)
                advance26 = (int)(long)ftFace->glyph->advance.x;

            // The fallback path has no GPOS, so the font's own kern table is all there is.
            if (face.HasKerning && previousGlyph != 0 && glyphIndex != 0)
            {
                FT_Vector_ kerning;
                if (FT_Get_Kerning(ftFace, previousGlyph, glyphIndex,
                        FT_Kerning_Mode_.FT_KERNING_DEFAULT, &kerning) == FT_Error.FT_Err_Ok)
                {
                    advance26 += (int)(long)kerning.x;
                }
            }

            shaped.Add(new ShapedGlyph(glyphIndex, advance26, 0, 0, cluster, missing));
            previousGlyph = glyphIndex;
        }

        return shaped;
    }

    // --- Line breaking ---------------------------------------------------------

    /// <summary>
    /// Splits a shaped paragraph into lines at <paramref name="maxWidthPx"/>, preferring
    /// spaces and falling back to breaking inside a word that is too long on its own.
    /// </summary>
    private static List<List<ShapedGlyph>> BreakIntoLines(
        IReadOnlyList<ShapedGlyph> shaped, string paragraph, int maxWidthPx)
    {
        var lines = new List<List<ShapedGlyph>>();

        if (shaped.Count == 0)
        {
            lines.Add([]);
            return lines;
        }

        if (maxWidthPx <= 0)
        {
            lines.Add([.. shaped]);
            return lines;
        }

        int limit26 = maxWidthPx * 64;
        var current = new List<ShapedGlyph>();
        int width26 = 0;
        int lastBreak = -1;          // index in `current` just after the last space
        int widthAtBreak26 = 0;

        foreach (ShapedGlyph glyph in shaped)
        {
            bool isSpace = IsSpaceCluster(paragraph, glyph.Cluster);

            if (width26 + glyph.XAdvance26 > limit26 && current.Count > 0)
            {
                if (lastBreak > 0)
                {
                    // Break at the space: the tail moves down to the next line.
                    var tail = current.GetRange(lastBreak, current.Count - lastBreak);
                    current.RemoveRange(lastBreak, current.Count - lastBreak);
                    lines.Add(current);

                    current = tail;
                    width26 -= widthAtBreak26;
                }
                else
                {
                    // One word wider than the whole panel: break it rather than drop it.
                    lines.Add(current);
                    current = [];
                    width26 = 0;
                }

                lastBreak = -1;
                widthAtBreak26 = 0;
            }

            current.Add(glyph);
            width26 += glyph.XAdvance26;

            if (isSpace)
            {
                lastBreak = current.Count;
                widthAtBreak26 = width26;
            }
        }

        lines.Add(current);
        return lines;
    }

    private static bool IsSpaceCluster(string paragraph, int cluster)
        => cluster >= 0 && cluster < paragraph.Length && char.IsWhiteSpace(paragraph[cluster]);

    /// <summary>Splits on explicit line breaks, keeping empty lines.</summary>
    private static IEnumerable<string> SplitLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int end = i;
                if (end > start && text[end - 1] == '\r') end--;
                yield return text[start..end];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    // --- Metrics ---------------------------------------------------------------

    /// <summary>
    /// Line height, ascent and descent in pixels for the size currently selected.
    /// </summary>
    /// <remarks>
    /// Bitmap fonts sometimes report a zero height, so a sane fallback derived from the
    /// pixel size keeps lines from piling on top of each other.
    /// </remarks>
    private static (int LineHeight, int Ascent, int Descent) ReadLineMetrics(LoadedFace face)
    {
        int pixelSize = Math.Max(1, face.CurrentPixelSize);

        if (face.Face->size != null)
        {
            FT_Size_Metrics_ metrics = face.Face->size->metrics;
            int height = Round26((int)(long)metrics.height);
            int ascent = Round26((int)(long)metrics.ascender);
            int descent = Round26(-(int)(long)metrics.descender);

            if (height > 0 && ascent > 0)
                return (height, ascent, Math.Max(0, descent));
        }

        int fallbackAscent = (int)Math.Round(pixelSize * 0.8);
        return ((int)Math.Round(pixelSize * 1.2), fallbackAscent, pixelSize - fallbackAscent);
    }

    /// <summary>26.6 fixed point to whole pixels, rounded rather than truncated.</summary>
    internal static int Round26(int value) => (value + 32) >> 6;

    /// <summary>Formats a code point the way the glyph map labels it.</summary>
    internal static string FormatCodepoint(int codepoint)
        => "U+" + codepoint.ToString(codepoint > 0xFFFF ? "X6" : "X4", CultureInfo.InvariantCulture);
}
