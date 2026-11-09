using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Engine.Woff;

/// <summary>One table on its way into a rebuilt font.</summary>
internal sealed class SfntTable(uint tag, byte[] data)
{
    public uint Tag { get; } = tag;

    public byte[] Data { get; set; } = data;

    public override string ToString() => $"{SfntTag.ToDisplayString(Tag)} ({Data.Length} bytes)";
}

/// <summary>
/// Assembles tables back into a plain SFNT file that FreeType and HarfBuzz can open.
/// </summary>
/// <remarks>
/// Both WOFF decoders end here. Whatever a web font did to compress itself, the engine
/// above only ever sees an ordinary TrueType or OpenType file coming out of this class.
/// </remarks>
internal static class SfntWriter
{
    private const int OffsetTableSize = 12;
    private const int TableRecordSize = 16;

    /// <summary>Anything larger than this is treated as hostile rather than merely big.</summary>
    public const int MaximumFontSize = 256 * 1024 * 1024;

    /// <summary>
    /// Writes the offset table, the table records and the padded table data.
    /// </summary>
    /// <param name="sfntVersion">The flavour word: 0x00010000, 'OTTO' or 'true'.</param>
    /// <param name="tables">The tables to write. Sorted by tag here, not by the caller.</param>
    public static byte[] Write(uint sfntVersion, IReadOnlyList<SfntTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        if (tables.Count == 0)
            throw new FontLoadException("The font has no tables.");
        if (tables.Count > ushort.MaxValue)
            throw new FontLoadException($"The font claims {tables.Count} tables.");

        // Records go in tag order; so does the data, which is what most fonts do anyway.
        var ordered = tables.OrderBy(table => table.Tag).ToArray();

        long total = OffsetTableSize + (long)ordered.Length * TableRecordSize;
        foreach (SfntTable table in ordered) total += Align4(table.Data.Length);

        if (total > MaximumFontSize)
            throw new FontLoadException(
                $"The rebuilt font would be {total / (1024 * 1024)} MB, which is not plausible.");

        byte[] output = new byte[total];
        int numTables = ordered.Length;

        // searchRange and friends are the binary-search hints in the offset table.
        int entrySelector = 0;
        while (1 << (entrySelector + 1) <= numTables) entrySelector++;
        int searchRange = (1 << entrySelector) * 16;
        int rangeShift = numTables * 16 - searchRange;

        WriteUInt32(output, 0, sfntVersion);
        WriteUInt16(output, 4, (ushort)numTables);
        WriteUInt16(output, 6, (ushort)searchRange);
        WriteUInt16(output, 8, (ushort)entrySelector);
        WriteUInt16(output, 10, (ushort)rangeShift);

        int recordOffset = OffsetTableSize;
        int dataOffset = OffsetTableSize + numTables * TableRecordSize;

        foreach (SfntTable table in ordered)
        {
            table.Data.CopyTo(output, dataOffset);
            // The gap to the next 4-byte boundary stays zero, as the specification wants.

            WriteUInt32(output, recordOffset, table.Tag);
            WriteUInt32(output, recordOffset + 4, Checksum(output, dataOffset, table.Data.Length));
            WriteUInt32(output, recordOffset + 8, (uint)dataOffset);
            WriteUInt32(output, recordOffset + 12, (uint)table.Data.Length);

            recordOffset += TableRecordSize;
            dataOffset += Align4(table.Data.Length);
        }

        FixHeadChecksumAdjustment(output, ordered);
        return output;
    }

    /// <summary>
    /// Writes <c>head.checkSumAdjustment</c>, which is defined in terms of the finished
    /// file and so can only be filled in once everything else is in place.
    /// </summary>
    private static void FixHeadChecksumAdjustment(byte[] font, IReadOnlyList<SfntTable> ordered)
    {
        const int CheckSumAdjustmentOffset = 8;      // within head
        const uint Magic = 0xB1B0AFBAu;

        int headRecord = -1;
        int recordOffset = OffsetTableSize;
        foreach (SfntTable table in ordered)
        {
            if (table.Tag == SfntTag.Head) { headRecord = recordOffset; break; }
            recordOffset += TableRecordSize;
        }

        if (headRecord < 0) return;      // CFF-only fonts still have head; bitmap ones may not

        uint headOffset = ReadUInt32(font, headRecord + 8);
        uint headLength = ReadUInt32(font, headRecord + 12);
        if (headOffset + headLength > (uint)font.Length || headLength < CheckSumAdjustmentOffset + 4)
            return;

        // The field must read as zero while the whole-file checksum is taken.
        WriteUInt32(font, (int)headOffset + CheckSumAdjustmentOffset, 0);
        uint fileChecksum = Checksum(font, 0, font.Length);
        WriteUInt32(font, (int)headOffset + CheckSumAdjustmentOffset, unchecked(Magic - fileChecksum));
    }

    /// <summary>
    /// The SFNT checksum: the sum of the data as big-endian 32-bit words, with the tail
    /// padded by zeros.
    /// </summary>
    public static uint Checksum(byte[] data, int offset, int length)
    {
        uint sum = 0;
        int i = offset;
        int end = offset + length;

        for (; i + 4 <= end; i += 4)
            sum = unchecked(sum + ReadUInt32(data, i));

        if (i < end)
        {
            uint tail = 0;
            for (int shift = 24; i < end; i++, shift -= 8)
                tail |= (uint)data[i] << shift;
            sum = unchecked(sum + tail);
        }

        return sum;
    }

    public static int Align4(int length) => (length + 3) & ~3;

    public static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    public static void WriteInt16(byte[] buffer, int offset, short value)
        => WriteUInt16(buffer, offset, (ushort)value);

    public static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    public static uint ReadUInt32(byte[] buffer, int offset)
        => ((uint)buffer[offset] << 24)
         | ((uint)buffer[offset + 1] << 16)
         | ((uint)buffer[offset + 2] << 8)
         | buffer[offset + 3];

    public static ushort ReadUInt16(byte[] buffer, int offset)
        => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
}
