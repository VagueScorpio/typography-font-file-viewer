using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FreeTypeSharp;
using TypographyFontFileViewer.Core;
using static FreeTypeSharp.FT;

namespace TypographyFontFileViewer.Engine;

/// <summary>
/// Reads what a font says about itself: the SFNT name table for OpenType fonts, and the
/// format-specific equivalents for Type 1, Windows FNT, BDF and PCF.
/// </summary>
/// <remarks>
/// Name records are chosen rather than concatenated. A font usually carries the same
/// name several times over for different platforms and languages, so each name ID is
/// scored and the best record wins: Windows/English first, then Unicode, then Mac Roman.
/// </remarks>
internal static unsafe class MetadataReader
{
    // Name IDs, from the OpenType specification.
    private const int NameCopyright = 0;
    private const int NameFamily = 1;
    private const int NameSubfamily = 2;
    private const int NameUniqueId = 3;
    private const int NameFullName = 4;
    private const int NameVersion = 5;
    private const int NamePostScript = 6;
    private const int NameTrademark = 7;
    private const int NameManufacturer = 8;
    private const int NameDesigner = 9;
    private const int NameDescription = 10;
    private const int NameVendorUrl = 11;
    private const int NameDesignerUrl = 12;
    private const int NameLicense = 13;
    private const int NameLicenseUrl = 14;
    private const int NameTypographicFamily = 16;
    private const int NameTypographicSubfamily = 17;

    private static readonly Encoding Utf16BigEndian = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
    private static Encoding? _macRoman;

    static MetadataReader()
    {
        // Mac Roman is not one of .NET's built-in encodings on modern runtimes.
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _macRoman = Encoding.GetEncoding(10000);
        }
        catch (Exception)
        {
            _macRoman = null;   // fall back to Latin-1 for the rare Mac-only name
        }
    }

    /// <summary>Builds the list-row summary for an open face.</summary>
    public static FaceSummary ReadSummary(LoadedFace face, FontFormat container)
    {
        ArgumentNullException.ThrowIfNull(face);

        Dictionary<int, string> names = ReadNameTable(face.Face);

        string family = FirstNonEmpty(
            Lookup(names, NameTypographicFamily),
            Lookup(names, NameFamily),
            ReadPostScriptFamily(face.Face),
            ReadBdfProperty(face.Face, "FAMILY_NAME"),
            FreeTypeNative.ReadNativeString(face.Face->family_name));

        string style = FirstNonEmpty(
            Lookup(names, NameTypographicSubfamily),
            Lookup(names, NameSubfamily),
            FreeTypeNative.ReadNativeString(face.Face->style_name));

        return new FaceSummary
        {
            Path = face.Key.Path,
            FaceIndex = face.Key.FaceIndex,
            FaceCount = face.FaceCount,
            Format = container,
            Family = family,
            Style = style,
            GlyphCount = (long)face.Face->num_glyphs,
            FixedSizes = face.FixedSizes,
            IsScalable = face.IsScalable,
            IsVariable = face.IsVariable,
        };
    }

    /// <summary>Builds the full Info-tab record for an open face.</summary>
    public static FontInfo ReadInfo(FT_LibraryRec_* library, LoadedFace face, FontFormat container)
    {
        ArgumentNullException.ThrowIfNull(face);

        Dictionary<int, string> names = ReadNameTable(face.Face);
        FT_FaceRec_* ftFace = face.Face;

        var fileInfo = new FileInfo(face.Key.Path);
        (string? psVersion, string? psNotice, string? psFullName, string? psFamily) = ReadPostScriptInfo(ftFace);
        (string? fntCopyright, int fntPointSize) = ReadWinFntHeader(ftFace);

        string? copyright = FirstNonEmptyOrNull(
            Lookup(names, NameCopyright), psNotice, fntCopyright, ReadBdfProperty(ftFace, "COPYRIGHT"));

        string? family = FirstNonEmptyOrNull(
            Lookup(names, NameTypographicFamily), Lookup(names, NameFamily), psFamily,
            ReadBdfProperty(ftFace, "FAMILY_NAME"), FreeTypeNative.ReadNativeString(ftFace->family_name));

        (IReadOnlyList<VariationAxis> axes, IReadOnlyList<string> instances) =
            ReadVariations(library, ftFace, names);

        return new FontInfo
        {
            Path = face.Key.Path,
            FaceIndex = face.Key.FaceIndex,
            Container = container,
            OutlineFormat = ReadFontFormat(ftFace),

            Copyright = copyright,
            Family = family,
            Subfamily = FirstNonEmptyOrNull(
                Lookup(names, NameTypographicSubfamily), Lookup(names, NameSubfamily),
                FreeTypeNative.ReadNativeString(ftFace->style_name)),
            UniqueId = Lookup(names, NameUniqueId),
            FullName = FirstNonEmptyOrNull(Lookup(names, NameFullName), psFullName),
            Version = FirstNonEmptyOrNull(Lookup(names, NameVersion), psVersion),
            PostScriptName = FirstNonEmptyOrNull(
                Lookup(names, NamePostScript), ReadPostScriptName(ftFace)),
            Trademark = Lookup(names, NameTrademark),
            Manufacturer = FirstNonEmptyOrNull(
                Lookup(names, NameManufacturer), ReadBdfProperty(ftFace, "FOUNDRY")),
            Designer = Lookup(names, NameDesigner),
            Description = Lookup(names, NameDescription),
            VendorUrl = Lookup(names, NameVendorUrl),
            DesignerUrl = Lookup(names, NameDesignerUrl),
            License = Lookup(names, NameLicense),
            LicenseUrl = Lookup(names, NameLicenseUrl),

            GlyphCount = (long)ftFace->num_glyphs,
            UnitsPerEm = ftFace->units_per_EM,
            FaceCount = face.FaceCount,
            MappedCharacterCount = CountMappedCharacters(ftFace),
            CharmapCount = ftFace->num_charmaps,
            Ascender = ftFace->ascender,
            Descender = ftFace->descender,
            LineHeight = ftFace->height,

            IsScalable = face.IsScalable,
            HasKerning = face.HasKerning,
            HasGlyphNames = face.HasGlyphNames,
            HasColor = face.HasColor,
            IsFixedWidth = face.IsFixedWidth,

            FixedSizes = fntPointSize > 0 && face.FixedSizes.Count == 0 ? [fntPointSize] : face.FixedSizes,
            Embedding = ReadEmbedding(ftFace),
            VariationAxes = axes,
            NamedInstances = instances,

            FileLength = fileInfo.Exists ? fileInfo.Length : face.Key.Length,
            LastWriteUtc = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : face.Key.LastWriteUtc,
        };
    }

    /// <summary>
    /// Every character the font maps, in code-point order, optionally followed by the
    /// glyphs no code point reaches.
    /// </summary>
    public static IReadOnlyList<GlyphEntry> ListGlyphs(LoadedFace face, bool includeUnmapped)
    {
        ArgumentNullException.ThrowIfNull(face);

        FT_FaceRec_* ftFace = face.Face;
        long glyphCount = (long)ftFace->num_glyphs;
        var entries = new List<GlyphEntry>((int)Math.Min(glyphCount + 16, 200_000));
        var seen = new HashSet<uint>();

        if (ftFace->charmap != null)
        {
            uint glyphIndex;
            nuint codepoint = FT_Get_First_Char(ftFace, &glyphIndex);

            while (glyphIndex != 0)
            {
                int value = (int)Math.Min((ulong)codepoint, int.MaxValue);
                entries.Add(new GlyphEntry(glyphIndex, value, GetGlyphName(face, glyphIndex)));
                seen.Add(glyphIndex);

                codepoint = FT_Get_Next_Char(ftFace, codepoint, &glyphIndex);
            }
        }

        if (includeUnmapped)
        {
            for (uint glyphIndex = 0; glyphIndex < glyphCount; glyphIndex++)
                if (!seen.Contains(glyphIndex))
                    entries.Add(new GlyphEntry(glyphIndex, -1, GetGlyphName(face, glyphIndex)));
        }

        return entries;
    }

    /// <summary>The font's own name for a glyph, when it carries names at all.</summary>
    public static string? GetGlyphName(LoadedFace face, uint glyphIndex)
    {
        if (!face.HasGlyphNames) return null;

        const int BufferSize = 128;
        byte* buffer = stackalloc byte[BufferSize];
        if (FT_Get_Glyph_Name(face.Face, glyphIndex, buffer, BufferSize) != FT_Error.FT_Err_Ok)
            return null;

        string name = Marshal.PtrToStringAnsi((nint)buffer) ?? string.Empty;
        return name.Length == 0 ? null : name;
    }

    // --- SFNT name table -------------------------------------------------------

    /// <summary>
    /// Reads the name table, keeping the best record for each name ID.
    /// </summary>
    private static Dictionary<int, string> ReadNameTable(FT_FaceRec_* face)
    {
        var best = new Dictionary<int, (int Score, string Value)>();

        uint count = FreeTypeNative.FT_Get_Sfnt_Name_Count(face);
        for (uint i = 0; i < count; i++)
        {
            FreeTypeNative.FT_SfntName record;
            if (FreeTypeNative.FT_Get_Sfnt_Name(face, i, &record) != FT_Error.FT_Err_Ok) continue;
            if (record.@string == null || record.string_len == 0) continue;
            if (record.string_len > 64 * 1024) continue;   // nothing legitimate is this long

            int score = ScoreNameRecord(record.platform_id, record.encoding_id, record.language_id);
            int nameId = record.name_id;

            if (best.TryGetValue(nameId, out (int Score, string Value) existing) && existing.Score >= score)
                continue;

            string? decoded = DecodeName(record);
            if (string.IsNullOrWhiteSpace(decoded)) continue;

            best[nameId] = (score, decoded.Trim());
        }

        var result = new Dictionary<int, string>(best.Count);
        foreach ((int nameId, (int _, string value)) in best)
            result[nameId] = value;

        return result;
    }

    /// <summary>
    /// Ranks a name record. Windows/English wins, then any Unicode record, then Mac Roman.
    /// </summary>
    private static int ScoreNameRecord(ushort platformId, ushort encodingId, ushort languageId)
    {
        const int EnglishUnitedStates = 0x0409;

        return platformId switch
        {
            // Windows platform, UCS-2 or UCS-4
            3 when encodingId is 1 or 10 => 1000 + (languageId == EnglishUnitedStates ? 100 : 0),
            3 => 700,

            // Unicode platform
            0 => 800,

            // Macintosh platform, Roman encoding, English
            1 when encodingId == 0 => 600 + (languageId == 0 ? 50 : 0),
            1 => 400,

            _ => 100,
        };
    }

    private static string? DecodeName(FreeTypeNative.FT_SfntName record)
    {
        var bytes = new ReadOnlySpan<byte>(record.@string, (int)record.string_len);

        try
        {
            // Windows and Unicode platform names are UTF-16 big-endian.
            if (record.platform_id is 0 or 3)
                return Utf16BigEndian.GetString(bytes);

            if (record.platform_id == 1 && record.encoding_id == 0)
                return (_macRoman ?? Encoding.Latin1).GetString(bytes);

            // Unknown platform: an even length with interleaved zeros is almost
            // certainly UTF-16BE; otherwise treat it as single-byte text.
            if (bytes.Length >= 2 && bytes.Length % 2 == 0 && bytes[0] == 0)
                return Utf16BigEndian.GetString(bytes);

            return Encoding.Latin1.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? Lookup(Dictionary<int, string> names, int nameId)
        => names.TryGetValue(nameId, out string? value) ? value : null;

    // --- Format-specific metadata ----------------------------------------------

    private static string ReadFontFormat(FT_FaceRec_* face)
        => FreeTypeNative.ReadNativeString(FreeTypeNative.FT_Get_Font_Format(face)) ?? string.Empty;

    private static (string? Version, string? Notice, string? FullName, string? Family)
        ReadPostScriptInfo(FT_FaceRec_* face)
    {
        FreeTypeNative.PS_FontInfoRec info = default;
        if (FreeTypeNative.FT_Get_PS_Font_Info(face, &info) != FT_Error.FT_Err_Ok)
            return (null, null, null, null);

        return (
            FreeTypeNative.ReadNativeString(info.version),
            FreeTypeNative.ReadNativeString(info.notice),
            FreeTypeNative.ReadNativeString(info.full_name),
            FreeTypeNative.ReadNativeString(info.family_name));
    }

    private static string? ReadPostScriptFamily(FT_FaceRec_* face)
        => ReadPostScriptInfo(face).Family;

    private static string? ReadPostScriptName(FT_FaceRec_* face)
        => FreeTypeNative.ReadNativeString(FT_Get_Postscript_Name(face));

    /// <summary>
    /// Reads the Windows FNT header for .fon and .fnt fonts.
    /// </summary>
    /// <remarks>
    /// The record is written by FreeType into a caller-supplied buffer, so the buffer is
    /// deliberately far larger than the record and the two fields this app wants are read
    /// at their known offsets. A declared struct that was ever a byte short would be a
    /// stack overwrite.
    /// </remarks>
    private static (string? Copyright, int NominalPointSize) ReadWinFntHeader(FT_FaceRec_* face)
    {
        byte* buffer = stackalloc byte[FreeTypeNative.WinFntBufferSize];
        new Span<byte>(buffer, FreeTypeNative.WinFntBufferSize).Clear();

        if (FreeTypeNative.FT_Get_WinFNT_Header(face, buffer) != FT_Error.FT_Err_Ok)
            return (null, 0);

        var copyrightBytes = new ReadOnlySpan<byte>(
            buffer + FreeTypeNative.WinFntCopyrightOffset, FreeTypeNative.WinFntCopyrightLength);

        int terminator = copyrightBytes.IndexOf((byte)0);
        if (terminator >= 0) copyrightBytes = copyrightBytes[..terminator];

        string copyright = Encoding.Latin1.GetString(copyrightBytes).Trim();
        int pointSize = *(ushort*)(buffer + FreeTypeNative.WinFntNominalPointSizeOffset);

        return (copyright.Length == 0 ? null : copyright, pointSize);
    }

    /// <summary>Reads one BDF or PCF property, for the X11 bitmap fonts.</summary>
    private static string? ReadBdfProperty(FT_FaceRec_* face, string propertyName)
    {
        byte* buffer = stackalloc byte[FreeTypeNative.BdfPropertyBufferSize];
        new Span<byte>(buffer, FreeTypeNative.BdfPropertyBufferSize).Clear();

        if (FreeTypeNative.FT_Get_BDF_Property(face, propertyName, buffer) != FT_Error.FT_Err_Ok)
            return null;

        var type = (FreeTypeNative.BdfPropertyType)(*(int*)buffer);
        if (type != FreeTypeNative.BdfPropertyType.Atom) return null;

        byte* atom = *(byte**)(buffer + FreeTypeNative.BdfPropertyValueOffset);
        string? value = FreeTypeNative.ReadNativeString(atom);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static EmbeddingPermission ReadEmbedding(FT_FaceRec_* face)
    {
        ushort flags = FT_Get_FSType_Flags(face);

        // The licensing bits are the low nibble and are mutually exclusive in practice.
        return (flags & 0x000F) switch
        {
            0x0000 => EmbeddingPermission.Installable,
            0x0002 => EmbeddingPermission.Restricted,
            0x0004 => EmbeddingPermission.PreviewAndPrint,
            0x0008 => EmbeddingPermission.Editable,
            _ => EmbeddingPermission.Unknown,
        };
    }

    /// <summary>Lists the variation axes and named instances. v1.0 only lists them.</summary>
    private static (IReadOnlyList<VariationAxis> Axes, IReadOnlyList<string> Instances)
        ReadVariations(FT_LibraryRec_* library, FT_FaceRec_* face, Dictionary<int, string> names)
    {
        if (((long)face->face_flags & (long)FT_FACE_FLAG.FT_FACE_FLAG_MULTIPLE_MASTERS) == 0)
            return ([], []);

        FreeTypeNative.FT_MM_Var* master = null;
        if (FreeTypeNative.FT_Get_MM_Var(face, &master) != FT_Error.FT_Err_Ok || master == null)
            return ([], []);

        try
        {
            var axes = new List<VariationAxis>((int)master->num_axis);
            for (uint i = 0; i < master->num_axis; i++)
            {
                FreeTypeNative.FT_Var_Axis axis = master->axis[i];
                string tag = FreeTypeNative.TagToString(axis.tag);
                string name = FreeTypeNative.ReadNativeString(axis.name) ?? tag;

                axes.Add(new VariationAxis(
                    tag,
                    name,
                    FreeTypeNative.FromFixed16(axis.minimum),
                    FreeTypeNative.FromFixed16(axis.def),
                    FreeTypeNative.FromFixed16(axis.maximum)));
            }

            var instances = new List<string>((int)master->num_namedstyles);
            for (uint i = 0; i < master->num_namedstyles; i++)
            {
                uint nameId = master->namedstyle[i].strid;
                string label = Lookup(names, (int)nameId) ?? $"Instance {i + 1}";
                instances.Add(label);
            }

            return (axes, instances);
        }
        finally
        {
            FreeTypeNative.FT_Done_MM_Var(library, master);
        }
    }

    /// <summary>Counts the characters the current charmap actually maps.</summary>
    private static int CountMappedCharacters(FT_FaceRec_* face)
    {
        if (face->charmap == null) return 0;

        int count = 0;
        uint glyphIndex;
        nuint codepoint = FT_Get_First_Char(face, &glyphIndex);

        while (glyphIndex != 0)
        {
            count++;
            if (count > 2_000_000) break;    // a damaged cmap must not spin forever
            codepoint = FT_Get_Next_Char(face, codepoint, &glyphIndex);
        }

        return count;
    }

    // --- Small helpers ---------------------------------------------------------

    private static string FirstNonEmpty(params string?[] candidates)
        => FirstNonEmptyOrNull(candidates) ?? string.Empty;

    private static string? FirstNonEmptyOrNull(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();

        return null;
    }
}
