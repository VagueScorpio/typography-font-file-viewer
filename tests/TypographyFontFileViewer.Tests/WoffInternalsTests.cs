using System.IO;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Engine.Woff;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// Direct tests of the pieces the WOFF decoders are built from. Going through the
/// rendering pipeline proves the whole chain works; these pin down the individual rules
/// so a failure says which one broke.
/// </summary>
public class WoffInternalsTests
{
    // --- Variable-length integers ----------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0 }, 0)]
    [InlineData(new byte[] { 100 }, 100)]
    [InlineData(new byte[] { 252 }, 252)]
    [InlineData(new byte[] { 253, 0x12, 0x34 }, 0x1234)]   // two-byte form
    [InlineData(new byte[] { 255, 10 }, 263)]              // one more byte, biased by 253
    [InlineData(new byte[] { 254, 10 }, 516)]              // one more byte, biased by 506
    public void Read255UInt16_HandlesEveryEncoding(byte[] data, int expected)
    {
        var reader = new ByteReader(data);
        Assert.Equal(expected, reader.Read255UInt16());
    }

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0u)]
    [InlineData(new byte[] { 0x7F }, 127u)]
    [InlineData(new byte[] { 0x81, 0x00 }, 128u)]
    [InlineData(new byte[] { 0x8F, 0x7F }, 2047u)]
    [InlineData(new byte[] { 0x8F, 0xFF, 0x7F }, 262143u)]
    public void ReadUIntBase128_DecodesValidValues(byte[] data, uint expected)
    {
        var reader = new ByteReader(data);
        Assert.Equal(expected, reader.ReadUIntBase128());
    }

    [Fact]
    public void ReadUIntBase128_RejectsALeadingZero()
    {
        // 0x80 0x00 encodes zero the long way round, which the specification forbids
        // because it would give one value two encodings.
        var data = new byte[] { 0x80, 0x00 };

        Assert.Throws<FontLoadException>(() =>
        {
            var reader = new ByteReader(data);
            reader.ReadUIntBase128();
        });
    }

    [Fact]
    public void ReadUIntBase128_RejectsOverflow()
    {
        // Six continuation bytes, and a value past 32 bits.
        var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };

        Assert.Throws<FontLoadException>(() =>
        {
            var reader = new ByteReader(data);
            reader.ReadUIntBase128();
        });
    }

    [Fact]
    public void ReadingPastTheEnd_ThrowsFontLoadException()
    {
        var data = new byte[] { 1, 2, 3 };

        FontLoadException thrown = Assert.Throws<FontLoadException>(() =>
        {
            var reader = new ByteReader(data);
            reader.ReadUInt32();
        });

        Assert.Contains("ends unexpectedly", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnexpectedTag_IsNamedInTheError()
    {
        byte[] data = [(byte)'O', (byte)'T', (byte)'T', (byte)'O'];

        FontLoadException thrown = Assert.Throws<FontLoadException>(() =>
        {
            var reader = new ByteReader(data);
            reader.ExpectTag("wOF2");
        });

        Assert.Contains("OTTO", thrown.Message, StringComparison.Ordinal);
    }

    // --- SFNT assembly ----------------------------------------------------------

    [Fact]
    public void SfntWriter_LaysOutTheOffsetTableAndPadsEveryTable()
    {
        var tables = new List<SfntTable>
        {
            new(SfntTag.FromString("test"), [1, 2, 3]),          // 3 bytes: needs padding
            new(SfntTag.FromString("aaaa"), [9, 9, 9, 9]),       // 4 bytes: already aligned
        };

        byte[] font = SfntWriter.Write(0x00010000, tables);

        Assert.Equal(0x00010000u, SfntWriter.ReadUInt32(font, 0));
        Assert.Equal(2, SfntWriter.ReadUInt16(font, 4));

        // Records are written in tag order, so "aaaa" comes before "test".
        Assert.Equal(SfntTag.FromString("aaaa"), SfntWriter.ReadUInt32(font, 12));
        Assert.Equal(SfntTag.FromString("test"), SfntWriter.ReadUInt32(font, 12 + 16));

        uint firstOffset = SfntWriter.ReadUInt32(font, 12 + 8);
        uint secondOffset = SfntWriter.ReadUInt32(font, 12 + 16 + 8);

        Assert.Equal(0u, firstOffset % 4);
        Assert.Equal(0u, secondOffset % 4);

        // The declared length is the real length, not the padded one.
        Assert.Equal(4u, SfntWriter.ReadUInt32(font, 12 + 12));
        Assert.Equal(3u, SfntWriter.ReadUInt32(font, 12 + 16 + 12));
    }

    [Fact]
    public void SfntWriter_RefusesAFontWithNoTables()
        => Assert.Throws<FontLoadException>(() => SfntWriter.Write(0x00010000, []));

    [Fact]
    public void SfntWriter_WritesTheWholeFileChecksumIntoHead()
    {
        const uint Magic = 0xB1B0AFBAu;

        // head is 54 bytes; checkSumAdjustment sits at offset 8.
        byte[] head = new byte[54];
        var tables = new List<SfntTable> { new(SfntTag.Head, head) };

        byte[] font = SfntWriter.Write(0x00010000, tables);

        uint headOffset = SfntWriter.ReadUInt32(font, 12 + 8);
        uint stored = SfntWriter.ReadUInt32(font, (int)headOffset + 8);

        // Zero the field again and the whole-file checksum plus what was stored must
        // come back to the magic number. That is the definition of the field.
        SfntWriter.WriteUInt32(font, (int)headOffset + 8, 0);
        uint recomputed = SfntWriter.Checksum(font, 0, font.Length);

        Assert.Equal(Magic, unchecked(recomputed + stored));
    }

    // --- The decoders, called directly ------------------------------------------

    [Fact]
    public void WoffDecoder_ProducesTheSameTablesAsTheOriginal()
    {
        string sfntPath = TestFonts.Require("Inter-Regular.ttf");
        string woffPath = TestFonts.Require("Inter-Regular.woff");

        byte[] decoded = WoffDecoder.Decode(File.ReadAllBytes(woffPath), woffPath);
        byte[] original = File.ReadAllBytes(sfntPath);

        Assert.Equal(TableTags(original), TableTags(decoded));

        // WOFF 1.0 only compresses, so every table comes back byte for byte — except
        // head.checkSumAdjustment, which is defined in terms of the whole assembled
        // file and so has to be recomputed for the file we actually wrote.
        foreach (uint tag in TableTags(original).Where(tag => tag != SfntTag.Head))
            Assert.True(
                TableBytes(original, tag).SequenceEqual(TableBytes(decoded, tag)),
                $"Table '{SfntTag.ToDisplayString(tag)}' changed, and should not have.");

        byte[] originalHead = TableBytes(original, SfntTag.Head);
        byte[] decodedHead = TableBytes(decoded, SfntTag.Head);

        Assert.Equal(originalHead.Length, decodedHead.Length);
        Assert.True(
            originalHead.AsSpan(0, 8).SequenceEqual(decodedHead.AsSpan(0, 8)),
            "head changed before checkSumAdjustment.");
        Assert.True(
            originalHead.AsSpan(12).SequenceEqual(decodedHead.AsSpan(12)),
            "head changed after checkSumAdjustment.");
    }

    [Fact]
    public void Woff2Decoder_ProducesTheSameTablesAsTheOriginal()
    {
        string sfntPath = TestFonts.Require("Inter-Regular.ttf");
        string woff2Path = TestFonts.Require("Inter-Regular.woff2");

        byte[] decoded = Woff2Decoder.Decode(File.ReadAllBytes(woff2Path), woff2Path);
        byte[] original = File.ReadAllBytes(sfntPath);

        Assert.Equal(TableTags(original), TableTags(decoded));

        // glyf is re-encoded, loca is rewritten as long offsets and head records that
        // choice, so those three legitimately differ. Everything else must not.
        uint[] rewritten = [SfntTag.Glyf, SfntTag.Loca, SfntTag.Head];

        foreach (uint tag in TableTags(original).Except(rewritten))
            Assert.True(
                TableBytes(original, tag).SequenceEqual(TableBytes(decoded, tag)),
                $"Table '{SfntTag.ToDisplayString(tag)}' changed, and should not have.");
    }

    [Fact]
    public void Woff2Decoder_WritesTheLongLocaFormatIntoHead()
    {
        string woff2Path = TestFonts.Require("Inter-Regular.woff2");
        byte[] decoded = Woff2Decoder.Decode(File.ReadAllBytes(woff2Path), woff2Path);

        byte[] head = TableBytes(decoded, SfntTag.Head);
        Assert.Equal(1, (short)SfntWriter.ReadUInt16(head, 50));

        // And loca is the length long offsets imply: one entry per glyph, plus the end.
        byte[] maxp = TableBytes(decoded, SfntTag.Maxp);
        int numGlyphs = SfntWriter.ReadUInt16(maxp, 4);
        Assert.Equal((numGlyphs + 1) * 4, TableBytes(decoded, SfntTag.Loca).Length);
    }

    [Fact]
    public void Woff2Decoder_RejectsACollection()
    {
        // A minimal WOFF2 header claiming a collection: enough to reach the collection
        // directory and be turned away there.
        byte[] header = new byte[48];
        "wOF2"u8.CopyTo(header);
        SfntWriter.WriteUInt32(header, 4, SfntTag.FromString("ttcf"));
        SfntWriter.WriteUInt32(header, 8, (uint)header.Length);
        SfntWriter.WriteUInt16(header, 12, 1);      // one table, so the directory parses

        FontLoadException thrown = Assert.Throws<FontLoadException>(
            () => Woff2Decoder.Decode(header, "collection.woff2"));

        // It fails somewhere in the header or the collection directory; what matters is
        // that it is a clean refusal rather than a mis-decode.
        Assert.False(string.IsNullOrWhiteSpace(thrown.Message));
    }

    /// <summary>
    /// The bounding-box bitmap in a transformed glyf table is padded to four bytes, not
    /// to one.
    /// </summary>
    /// <remarks>
    /// Getting this wrong shifts every bounding box that follows by a single byte, which
    /// leaves simple glyphs untouched — their boxes are computed from their points — and
    /// quietly displaces every composite glyph instead. Accented capitals are composites,
    /// so they are what catches it. Fonts whose glyph count happens to be a multiple of
    /// 32 look fine either way, which is exactly why this needs its own test.
    /// </remarks>
    [Fact]
    public async Task CompositeGlyphsFromAWoff2_LandWhereTheOriginalPutsThem()
    {
        string sfnt = TestFonts.Require("Inter-Regular.ttf");
        string woff2 = TestFonts.Require("Inter-Regular.woff2");

        using var service = new TypographyFontFileViewer.Services.RenderService();

        const string AccentedCapitals = "ÄÃÀÁÂÅÇÑ";

        RenderedText expected = await service.RenderTextAsync(
            FaceKey.For(sfnt, 0), AccentedCapitals, 48, 96, 0, RenderStyle.Light,
            cancellationToken: TestContext.Current.CancellationToken);

        RenderedText actual = await service.RenderTextAsync(
            FaceKey.For(woff2, 0), AccentedCapitals, 48, 96, 0, RenderStyle.Light,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.True(expected.Bgra.AsSpan().SequenceEqual(actual.Bgra),
            "Composite glyphs decoded from WOFF2 do not match the original font.");
    }

    // --- Helpers ----------------------------------------------------------------

    private static uint[] TableTags(byte[] font)
    {
        int numTables = SfntWriter.ReadUInt16(font, 4);
        var tags = new uint[numTables];

        for (int i = 0; i < numTables; i++)
            tags[i] = SfntWriter.ReadUInt32(font, 12 + i * 16);

        Array.Sort(tags);
        return tags;
    }

    private static byte[] TableBytes(byte[] font, uint tag)
    {
        int numTables = SfntWriter.ReadUInt16(font, 4);

        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + i * 16;
            if (SfntWriter.ReadUInt32(font, record) != tag) continue;

            int offset = (int)SfntWriter.ReadUInt32(font, record + 8);
            int length = (int)SfntWriter.ReadUInt32(font, record + 12);
            return font.AsSpan(offset, length).ToArray();
        }

        throw new InvalidOperationException($"No '{SfntTag.ToDisplayString(tag)}' table.");
    }
}
