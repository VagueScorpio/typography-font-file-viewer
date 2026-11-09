using System.IO;

namespace TypographyFontFileViewer.Core;

/// <summary>
/// Decides what a file is. Magic bytes win; the extension is only a fallback, so a
/// CFF-based font named .ttf, or a WOFF2 named .woff, still opens as what it is.
/// </summary>
public static class FormatSniffer
{
    /// <summary>How many bytes <see cref="Sniff"/> wants. Reading more is harmless.</summary>
    public const int HeaderBytes = 64;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".otf", ".ttc", ".otc", ".woff", ".woff2",
        ".pfb", ".pfa", ".fon", ".fnt", ".bdf", ".pcf", ".dfont",
    };

    private static readonly HashSet<string> CompanionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".afm", ".pfm", ".inf",
    };

    /// <summary>Extensions the scanner skips without complaining (blueprint section 3).</summary>
    private static readonly HashSet<string> QuietlyIgnored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".eot", ".svg", ".svgz", ".ttx", ".ufo", ".otb",
    };

    /// <summary>True for a font file the app lists. Handles the two-part <c>.pcf.gz</c>.</summary>
    public static bool IsSupportedFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        if (fileName.EndsWith(".pcf.gz", StringComparison.OrdinalIgnoreCase)) return true;
        return IsSupportedExtension(Path.GetExtension(fileName));
    }

    /// <summary>True for a font extension the app lists.</summary>
    public static bool IsSupportedExtension(string extension)
        => !string.IsNullOrEmpty(extension) && SupportedExtensions.Contains(extension);

    /// <summary>
    /// True for metrics and info files that belong to a Type 1 font. They are not
    /// listed as fonts, but they travel with their font when it is favourited.
    /// </summary>
    public static bool IsCompanionExtension(string extension)
        => !string.IsNullOrEmpty(extension) && CompanionExtensions.Contains(extension);

    /// <summary>True for formats v1.0 does not support and the scanner skips silently.</summary>
    public static bool IsQuietlyIgnoredExtension(string extension)
        => !string.IsNullOrEmpty(extension) && QuietlyIgnored.Contains(extension);

    /// <summary>
    /// Identifies a font from its first bytes, falling back to <paramref name="fileName"/>
    /// when the bytes say nothing conclusive.
    /// </summary>
    /// <param name="header">The first <see cref="HeaderBytes"/> bytes, or fewer.</param>
    /// <param name="fileName">File name or extension; used only as a fallback.</param>
    public static FontFormat Sniff(ReadOnlySpan<byte> header, string? fileName)
    {
        string extension = string.IsNullOrEmpty(fileName)
            ? string.Empty
            : (fileName.EndsWith(".pcf.gz", StringComparison.OrdinalIgnoreCase)
                ? ".pcf"
                : Path.GetExtension(fileName));

        FontFormat byMagic = SniffMagic(header, extension);
        if (byMagic != FontFormat.Unknown) return byMagic;

        return FromExtension(extension);
    }

    private static FontFormat SniffMagic(ReadOnlySpan<byte> header, string extension)
    {
        if (header.Length >= 4)
        {
            if (Matches(header, "OTTO")) return FontFormat.OpenTypeCff;
            if (Matches(header, "ttcf")) return FontFormat.Collection;
            if (Matches(header, "wOFF")) return FontFormat.Woff;
            if (Matches(header, "wOF2")) return FontFormat.Woff2;
            if (Matches(header, "true") || Matches(header, "typ1")) return FontFormat.TrueType;

            // 00 01 00 00 is the TrueType version tag. The outlines inside may still be
            // CFF, but FreeType reports the real format once the face is open.
            if (header[0] == 0x00 && header[1] == 0x01 && header[2] == 0x00 && header[3] == 0x00)
                return FontFormat.TrueType;

            // PCF: the ASCII tag is 0x01 'f' 'c' 'p'.
            if (header[0] == 0x01 && header[1] == 0x66 && header[2] == 0x63 && header[3] == 0x70)
                return FontFormat.Pcf;
        }

        if (header.Length >= 2)
        {
            // PFB segment header: 0x80 followed by a segment type of 1, 2 or 3.
            if (header[0] == 0x80 && header[1] is 0x01 or 0x02 or 0x03) return FontFormat.Type1;

            // Gzip. Only meaningful for the .pcf.gz the caller already resolved to .pcf.
            if (header[0] == 0x1F && header[1] == 0x8B)
                return extension.Equals(".pcf", StringComparison.OrdinalIgnoreCase)
                    ? FontFormat.Pcf
                    : FontFormat.Unknown;

            // MZ is every DOS/Windows executable, so it only means a font when the
            // name agrees. Otherwise this is some unrelated binary.
            if (header[0] == (byte)'M' && header[1] == (byte)'Z')
                return extension is ".fon" or ".fnt" ? FontFormat.WindowsBitmap : FontFormat.Unknown;
        }

        if (StartsWithText(header, "STARTFONT")) return FontFormat.Bdf;

        // PFA is an ASCII PostScript program; the first line names the font type.
        if (StartsWithText(header, "%!PS-AdobeFont")
            || StartsWithText(header, "%!FontType1")
            || StartsWithText(header, "%!PS-Adobe-3.0 Resource-Font"))
            return FontFormat.Type1;

        return FontFormat.Unknown;
    }

    private static FontFormat FromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".ttf" => FontFormat.TrueType,
        ".otf" => FontFormat.OpenTypeCff,
        ".ttc" or ".otc" => FontFormat.Collection,
        ".woff" => FontFormat.Woff,
        ".woff2" => FontFormat.Woff2,
        ".pfb" or ".pfa" => FontFormat.Type1,
        ".fon" or ".fnt" => FontFormat.WindowsBitmap,
        ".bdf" => FontFormat.Bdf,
        ".pcf" => FontFormat.Pcf,
        ".dfont" => FontFormat.DataFork,
        _ => FontFormat.Unknown,
    };

    private static bool Matches(ReadOnlySpan<byte> header, string tag)
    {
        if (header.Length < tag.Length) return false;
        for (int i = 0; i < tag.Length; i++)
            if (header[i] != (byte)tag[i]) return false;
        return true;
    }

    private static bool StartsWithText(ReadOnlySpan<byte> header, string text)
        => Matches(header, text);
}
