using System.IO;
using System.IO.Compression;
using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Engine.Woff;

/// <summary>
/// Unwraps a WOFF 1.0 file back into the SFNT font inside it.
/// </summary>
/// <remarks>
/// WOFF 1.0 is the simple one: a 44-byte header, a directory of tables, and each table
/// stored either raw or deflated. There is no restructuring of the font's contents —
/// unlike WOFF 2.0, which rewrites the outlines themselves.
/// </remarks>
internal static class WoffDecoder
{
    private const int HeaderSize = 44;
    private const int DirectoryEntrySize = 20;

    /// <summary>Converts WOFF 1.0 bytes to SFNT bytes.</summary>
    /// <exception cref="FontLoadException">The file is malformed or implausible.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> woff, string? path = null)
    {
        var reader = new ByteReader(woff, path);

        reader.ExpectTag("wOFF");
        uint flavor = reader.ReadUInt32();
        uint length = reader.ReadUInt32();
        ushort numTables = reader.ReadUInt16();
        reader.Skip(2);                               // reserved, must be zero
        uint totalSfntSize = reader.ReadUInt32();
        reader.Skip(2 + 2);                           // major and minor version
        reader.Skip(4 + 4 + 4);                       // extended metadata block
        reader.Skip(4 + 4);                           // private data block

        if (numTables == 0)
            throw new FontLoadException("This WOFF file declares no tables.") { Path = path };

        if (length != woff.Length)
            throw new FontLoadException(
                $"This WOFF file says it is {length} bytes but is actually {woff.Length}.")
            { Path = path };

        if (totalSfntSize > SfntWriter.MaximumFontSize)
            throw new FontLoadException(
                $"This WOFF file claims to unpack to {totalSfntSize / (1024 * 1024)} MB.")
            { Path = path };

        if (HeaderSize + (long)numTables * DirectoryEntrySize > woff.Length)
            throw new FontLoadException(
                $"This WOFF file declares {numTables} tables but is too small to hold the directory.")
            { Path = path };

        var tables = new List<SfntTable>(numTables);
        long uncompressedTotal = 0;

        for (int i = 0; i < numTables; i++)
        {
            uint tag = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();
            uint compressedLength = reader.ReadUInt32();
            uint originalLength = reader.ReadUInt32();
            reader.ReadUInt32();                      // original checksum, recomputed on write

            if (originalLength > SfntWriter.MaximumFontSize || compressedLength > woff.Length)
                throw new FontLoadException(
                    $"Table '{SfntTag.ToDisplayString(tag)}' declares an implausible length.")
                { Path = path };

            if (offset > woff.Length || compressedLength > woff.Length - offset)
                throw new FontLoadException(
                    $"Table '{SfntTag.ToDisplayString(tag)}' lies outside the file.")
                { Path = path };

            uncompressedTotal += originalLength;
            if (uncompressedTotal > SfntWriter.MaximumFontSize)
                throw new FontLoadException("This WOFF file unpacks to an implausible size.")
                { Path = path };

            ReadOnlySpan<byte> stored = woff.Slice((int)offset, (int)compressedLength);

            byte[] data = compressedLength < originalLength
                ? Inflate(stored, (int)originalLength, tag, path)
                : stored[..(int)originalLength].ToArray();

            tables.Add(new SfntTable(tag, data));
        }

        return SfntWriter.Write(flavor, tables);
    }

    /// <summary>
    /// Inflates one zlib-compressed table to exactly the length the directory promised.
    /// </summary>
    /// <remarks>
    /// The expected length is known up front, so the output buffer is allocated once and
    /// the stream is required to fill it exactly. A table that inflates to more or less
    /// than it declared is malformed, and a decompression bomb cannot grow past the
    /// buffer it was given.
    /// </remarks>
    private static byte[] Inflate(ReadOnlySpan<byte> compressed, int expectedLength, uint tag, string? path)
    {
        byte[] output = new byte[expectedLength];

        try
        {
            using var source = new MemoryStream(compressed.ToArray(), writable: false);
            using var inflater = new ZLibStream(source, CompressionMode.Decompress);

            int written = inflater.ReadAtLeast(output, expectedLength, throwOnEndOfStream: false);

            if (written != expectedLength)
                throw new FontLoadException(
                    $"Table '{SfntTag.ToDisplayString(tag)}' unpacked to {written} bytes " +
                    $"but declared {expectedLength}.")
                { Path = path };

            // Anything still in the stream means the table lied about its length.
            if (inflater.ReadByte() != -1)
                throw new FontLoadException(
                    $"Table '{SfntTag.ToDisplayString(tag)}' unpacked to more than it declared.")
                { Path = path };

            return output;
        }
        catch (InvalidDataException exception)
        {
            throw new FontLoadException(
                $"Table '{SfntTag.ToDisplayString(tag)}' is not valid zlib data.", exception)
            { Path = path };
        }
    }
}
