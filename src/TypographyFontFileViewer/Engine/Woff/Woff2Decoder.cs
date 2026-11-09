using System.Buffers;
using System.IO;
using System.IO.Compression;
using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Engine.Woff;

/// <summary>
/// Unwraps a WOFF 2.0 file back into the SFNT font inside it.
/// </summary>
/// <remarks>
/// <para>
/// WOFF 2.0 does considerably more than compress. Table tags are replaced by indices
/// into a fixed list of the 63 common ones, lengths use a variable-width integer, all
/// the table data is concatenated into a single Brotli stream, and the outline tables
/// are structurally rewritten — <c>glyf</c> and <c>loca</c> into seven parallel streams,
/// <c>hmtx</c> with its left side bearings dropped where they can be recovered from the
/// glyph bounding boxes.
/// </para>
/// <para>
/// Decoding happens in managed code rather than through FreeType. The blueprint's reason
/// was that a prebuilt FreeType may lack Brotli, and that proved exactly right: the
/// freetype.dll this app ships opens WOFF 1.0 but not WOFF 2.0.
/// </para>
/// </remarks>
internal static class Woff2Decoder
{
    private const int HeaderSize = 48;

    /// <summary>
    /// The 63 tags WOFF 2.0 can refer to by index. Position is the index; 63 in the flag
    /// byte means a four-byte tag follows instead.
    /// </summary>
    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post",
        "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea",
        "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar",
        "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop",
        "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    private static readonly uint[] KnownTagValues = [.. KnownTags.Select(SfntTag.FromString)];

    /// <summary>One entry of the WOFF 2.0 table directory.</summary>
    private sealed class DirectoryEntry
    {
        public required uint Tag { get; init; }
        public required int TransformVersion { get; init; }
        public required uint OriginalLength { get; init; }

        /// <summary>Length in the compressed stream, which differs only when transformed.</summary>
        public required uint StreamLength { get; init; }

        public required bool IsTransformed { get; init; }

        public byte[] Data { get; set; } = [];
    }

    /// <summary>Converts WOFF 2.0 bytes to SFNT bytes.</summary>
    /// <exception cref="FontLoadException">The file is malformed, hostile or unsupported.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> woff2, string? path = null)
    {
        var reader = new ByteReader(woff2, path);

        reader.ExpectTag("wOF2");
        uint flavor = reader.ReadUInt32();
        uint length = reader.ReadUInt32();
        ushort numTables = reader.ReadUInt16();
        reader.Skip(2);                                   // reserved
        uint totalSfntSize = reader.ReadUInt32();
        uint totalCompressedSize = reader.ReadUInt32();
        reader.Skip(2 + 2);                               // major and minor version
        reader.Skip(4 + 4 + 4);                           // extended metadata block
        reader.Skip(4 + 4);                               // private data block

        if (numTables == 0)
            throw new FontLoadException("This WOFF2 file declares no tables.") { Path = path };

        if (length != woff2.Length)
            throw new FontLoadException(
                $"This WOFF2 file says it is {length} bytes but is actually {woff2.Length}.")
            { Path = path };

        if (totalSfntSize > SfntWriter.MaximumFontSize)
            throw new FontLoadException(
                $"This WOFF2 file claims to unpack to {totalSfntSize / (1024 * 1024)} MB.")
            { Path = path };

        if (totalCompressedSize > woff2.Length)
            throw new FontLoadException(
                "This WOFF2 file's compressed stream is larger than the file holding it.")
            { Path = path };

        DirectoryEntry[] directory = ReadDirectory(ref reader, numTables, path);

        if (flavor == SfntTag.FromString("ttcf"))
            ReadCollectionDirectoryAndReject(ref reader, numTables, path);

        // Everything from here is the single Brotli stream, holding every table's data
        // end to end in directory order.
        long expectedStreamLength = directory.Sum(entry => (long)entry.StreamLength);
        if (expectedStreamLength > SfntWriter.MaximumFontSize)
            throw new FontLoadException("This WOFF2 file's tables add up to an implausible size.")
            { Path = path };

        if (reader.Position + totalCompressedSize > woff2.Length)
            throw new FontLoadException("This WOFF2 file's compressed stream runs past its end.")
            { Path = path };

        ReadOnlySpan<byte> compressed = woff2.Slice(reader.Position, (int)totalCompressedSize);
        byte[] decompressed = Decompress(compressed, (int)expectedStreamLength, path);

        // Hand each table its slice of the decompressed stream.
        int cursor = 0;
        foreach (DirectoryEntry entry in directory)
        {
            entry.Data = decompressed.AsSpan(cursor, (int)entry.StreamLength).ToArray();
            cursor += (int)entry.StreamLength;
        }

        return Rebuild(flavor, directory, path);
    }

    // --- Table directory -------------------------------------------------------

    private static DirectoryEntry[] ReadDirectory(ref ByteReader reader, int numTables, string? path)
    {
        var directory = new DirectoryEntry[numTables];

        for (int i = 0; i < numTables; i++)
        {
            byte flags = reader.ReadUInt8();
            int tagIndex = flags & 0x3F;
            int transformVersion = (flags >> 6) & 0x03;

            uint tag = tagIndex == 63 ? reader.ReadUInt32() : KnownTagValues[tagIndex];
            uint originalLength = reader.ReadUIntBase128();

            // The rule reads backwards for the outline tables: glyf and loca treat
            // version 0 as "transformed" and version 3 as "left alone", while every
            // other table treats version 0 as "left alone".
            bool isOutlineTable = tag == SfntTag.Glyf || tag == SfntTag.Loca;
            bool isTransformed = isOutlineTable ? transformVersion == 0 : transformVersion != 0;

            uint transformLength = isTransformed ? reader.ReadUIntBase128() : originalLength;

            if (originalLength > SfntWriter.MaximumFontSize
                || transformLength > SfntWriter.MaximumFontSize)
                throw new FontLoadException(
                    $"Table '{SfntTag.ToDisplayString(tag)}' declares an implausible length.")
                { Path = path };

            directory[i] = new DirectoryEntry
            {
                Tag = tag,
                TransformVersion = transformVersion,
                OriginalLength = originalLength,
                StreamLength = transformLength,
                IsTransformed = isTransformed,
            };
        }

        return directory;
    }

    /// <summary>
    /// Reads past the collection directory and reports that collections are not supported.
    /// </summary>
    /// <remarks>
    /// A WOFF 2.0 file may hold a whole TrueType collection. v1.0 does not rebuild one.
    /// The directory is still parsed so the message can say how many fonts are in there,
    /// and so a collection is never mistaken for a single font and silently mis-decoded.
    /// Nothing available can produce such a file to test against — fontTools cannot write
    /// one — and shipping an unverified reconstruction of a font format seemed worse than
    /// saying plainly that it is not handled.
    /// </remarks>
    private static void ReadCollectionDirectoryAndReject(
        ref ByteReader reader, int numTables, string? path)
    {
        reader.ReadUInt32();                                  // directory format version
        int numFonts = reader.Read255UInt16();

        for (int font = 0; font < numFonts; font++)
        {
            int fontTables = reader.Read255UInt16();
            reader.ReadUInt32();                              // this font's flavour

            for (int i = 0; i < fontTables; i++)
            {
                int index = reader.Read255UInt16();
                if (index >= numTables)
                    throw new FontLoadException(
                        "This WOFF2 collection points at a table that does not exist.")
                    { Path = path };
            }
        }

        throw new FontLoadException(
            $"This is a WOFF2 collection holding {numFonts} fonts, which this version "
            + "cannot unpack. Convert it to a .ttc and open that instead.")
        { Path = path };
    }

    // --- Decompression ---------------------------------------------------------

    /// <summary>
    /// Brotli-decompresses the table stream to exactly the length the directory implies.
    /// </summary>
    /// <remarks>
    /// The output size is known before decompression starts, so the buffer is allocated
    /// once and the stream must fill it exactly. A file that expands to more than it
    /// declared is rejected rather than allowed to allocate its way through memory.
    /// </remarks>
    private static byte[] Decompress(ReadOnlySpan<byte> compressed, int expectedLength, string? path)
    {
        if (expectedLength == 0) return [];

        byte[] output = new byte[expectedLength];

        if (BrotliDecoder.TryDecompress(compressed, output, out int written))
        {
            if (written != expectedLength)
                throw new FontLoadException(
                    $"The compressed stream unpacked to {written} bytes but the tables "
                    + $"declare {expectedLength}.")
                { Path = path };

            return output;
        }

        // TryDecompress also returns false when the output buffer is too small, which
        // for a well-formed file means the declared lengths and the real ones disagree.
        // Retry through the streaming decoder to tell a corrupt file from a lying one.
        try
        {
            using var source = new MemoryStream(compressed.ToArray(), writable: false);
            using var brotli = new BrotliStream(source, CompressionMode.Decompress);

            int read = brotli.ReadAtLeast(output, expectedLength, throwOnEndOfStream: false);

            if (read < expectedLength)
                throw new FontLoadException(
                    $"The compressed stream unpacked to {read} bytes but the tables "
                    + $"declare {expectedLength}.")
                { Path = path };

            if (brotli.ReadByte() != -1)
                throw new FontLoadException(
                    "The compressed stream unpacked to more than the tables declare.")
                { Path = path };

            return output;
        }
        catch (InvalidDataException exception)
        {
            throw new FontLoadException(
                "The compressed stream is not valid Brotli data.", exception)
            { Path = path };
        }
    }

    // --- Rebuilding ------------------------------------------------------------

    private static byte[] Rebuild(uint flavor, DirectoryEntry[] directory, string? path)
    {
        var tables = new List<SfntTable>(directory.Length + 1);
        var byTag = new Dictionary<uint, SfntTable>(directory.Length);

        ReconstructedGlyf? reconstructed = null;
        DirectoryEntry? transformedHmtx = null;

        foreach (DirectoryEntry entry in directory)
        {
            if (entry.Tag == SfntTag.Glyf && entry.IsTransformed)
            {
                reconstructed = Woff2GlyfReconstructor.Reconstruct(entry.Data, path);

                var glyf = new SfntTable(SfntTag.Glyf, reconstructed.Glyf);
                tables.Add(glyf);
                byTag[SfntTag.Glyf] = glyf;
                continue;
            }

            if (entry.Tag == SfntTag.Loca && entry.IsTransformed)
                continue;   // loca is rebuilt alongside glyf, not from its own (empty) stream

            if (entry.Tag == SfntTag.Hmtx && entry.IsTransformed)
            {
                transformedHmtx = entry;   // needs glyph bounding boxes, so it waits
                continue;
            }

            if (entry.Data.Length != entry.OriginalLength)
                throw new FontLoadException(
                    $"Table '{SfntTag.ToDisplayString(entry.Tag)}' is {entry.Data.Length} bytes "
                    + $"but declares {entry.OriginalLength}.")
                { Path = path };

            var table = new SfntTable(entry.Tag, entry.Data);
            tables.Add(table);
            byTag[entry.Tag] = table;
        }

        if (reconstructed is not null)
        {
            var loca = new SfntTable(SfntTag.Loca, reconstructed.Loca);
            tables.Add(loca);
            byTag[SfntTag.Loca] = loca;

            PatchIndexToLocFormat(byTag, reconstructed.IndexToLocFormat, path);
        }
        else if (directory.Any(entry => entry.Tag == SfntTag.Loca && entry.IsTransformed))
        {
            throw new FontLoadException(
                "This WOFF2 file transforms loca but not glyf, which the format does not allow.")
            { Path = path };
        }

        if (transformedHmtx is not null)
        {
            if (reconstructed is null)
                throw new FontLoadException(
                    "This WOFF2 file transforms hmtx without a glyf table to rebuild it from.")
                { Path = path };

            var hmtx = new SfntTable(
                SfntTag.Hmtx,
                RebuildHmtx(transformedHmtx.Data, byTag, reconstructed, path));

            tables.Add(hmtx);
            byTag[SfntTag.Hmtx] = hmtx;
        }

        return SfntWriter.Write(flavor, tables);
    }

    /// <summary>
    /// Writes the loca format this rebuild actually used into <c>head</c>.
    /// </summary>
    /// <remarks>
    /// The reconstruction always emits long offsets, so a font that originally used the
    /// short form would be misread unless head is told. This is the one place a
    /// pass-through table is modified.
    /// </remarks>
    private static void PatchIndexToLocFormat(
        Dictionary<uint, SfntTable> byTag, short indexToLocFormat, string? path)
    {
        const int IndexToLocFormatOffset = 50;

        if (!byTag.TryGetValue(SfntTag.Head, out SfntTable? head))
            throw new FontLoadException("This font has outlines but no head table.") { Path = path };

        if (head.Data.Length < IndexToLocFormatOffset + 2)
            throw new FontLoadException("This font's head table is too short to be valid.")
            { Path = path };

        SfntWriter.WriteInt16(head.Data, IndexToLocFormatOffset, indexToLocFormat);
    }

    /// <summary>
    /// Rebuilds <c>hmtx</c> from the transformed form, which drops the left side bearings
    /// that can be read back off the glyph outlines.
    /// </summary>
    /// <remarks>
    /// For most glyphs the left side bearing equals the glyph's xMin, so WOFF 2.0 simply
    /// leaves those arrays out and expects the decoder to put them back from the
    /// reconstructed bounding boxes.
    /// </remarks>
    private static byte[] RebuildHmtx(
        byte[] transformed, Dictionary<uint, SfntTable> byTag,
        ReconstructedGlyf glyf, string? path)
    {
        const int NumberOfHMetricsOffset = 34;   // within hhea

        if (!byTag.TryGetValue(SfntTag.Hhea, out SfntTable? hhea)
            || hhea.Data.Length < NumberOfHMetricsOffset + 2)
            throw new FontLoadException("A transformed hmtx needs an hhea table, which is missing.")
            { Path = path };

        int numberOfHMetrics = SfntWriter.ReadUInt16(hhea.Data, NumberOfHMetricsOffset);
        int numGlyphs = glyf.NumGlyphs;

        if (numberOfHMetrics == 0 || numberOfHMetrics > numGlyphs)
            throw new FontLoadException(
                $"hhea declares {numberOfHMetrics} horizontal metrics for {numGlyphs} glyphs.")
            { Path = path };

        var reader = new ByteReader(transformed, path);
        byte flags = reader.ReadUInt8();
        bool lsbAbsent = (flags & 0x01) != 0;
        bool leftSideBearingAbsent = (flags & 0x02) != 0;

        if ((flags & 0xFC) != 0)
            throw new FontLoadException("The transformed hmtx table sets reserved flags.")
            { Path = path };

        var advanceWidths = new ushort[numberOfHMetrics];
        for (int i = 0; i < numberOfHMetrics; i++)
            advanceWidths[i] = reader.ReadUInt16();

        var lsb = new short[numberOfHMetrics];
        for (int i = 0; i < numberOfHMetrics; i++)
            lsb[i] = lsbAbsent ? glyf.XMin[i] : reader.ReadInt16();

        int remaining = numGlyphs - numberOfHMetrics;
        var trailing = new short[remaining];
        for (int i = 0; i < remaining; i++)
            trailing[i] = leftSideBearingAbsent
                ? glyf.XMin[numberOfHMetrics + i]
                : reader.ReadInt16();

        byte[] hmtx = new byte[numberOfHMetrics * 4 + remaining * 2];
        int offset = 0;

        for (int i = 0; i < numberOfHMetrics; i++)
        {
            SfntWriter.WriteUInt16(hmtx, offset, advanceWidths[i]); offset += 2;
            SfntWriter.WriteInt16(hmtx, offset, lsb[i]); offset += 2;
        }

        for (int i = 0; i < remaining; i++)
        {
            SfntWriter.WriteInt16(hmtx, offset, trailing[i]);
            offset += 2;
        }

        return hmtx;
    }
}
