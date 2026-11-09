using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Engine.Woff;

namespace TypographyFontFileViewer.Engine;

/// <summary>
/// The translator at the engine's border: whatever arrives, what comes out is SFNT.
/// </summary>
/// <remarks>
/// WOFF and WOFF 2.0 are compressed wrappers around an ordinary TrueType or OpenType
/// file. Unwrapping them here means every layer past this point — the face cache, text
/// layout, the rasterizer, the metadata reader — deals with exactly one kind of font and
/// never has to ask how it arrived.
/// </remarks>
internal static class FormatNormalizer
{
    /// <summary>
    /// Returns SFNT bytes for <paramref name="data"/>, decoding web fonts and returning
    /// everything else untouched.
    /// </summary>
    /// <remarks>
    /// Type 1, BDF, PCF and Windows FNT fonts are not SFNT and never will be; they are
    /// passed straight through for FreeType to open in its own right.
    /// </remarks>
    /// <exception cref="FontLoadException">A web font is malformed or unsupported.</exception>
    public static byte[] ToSfnt(byte[] data, FontFormat format, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        return format switch
        {
            FontFormat.Woff => WoffDecoder.Decode(data, path),
            FontFormat.Woff2 => Woff2Decoder.Decode(data, path),
            _ => data,
        };
    }
}
