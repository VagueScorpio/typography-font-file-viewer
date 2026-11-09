using System.IO;
using System.Text;
using TypographyFontFileViewer.Core;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// The sniffer decides what a file is from its first bytes, and only falls back to the
/// extension. These tests pin both halves of that rule down.
/// </summary>
public class FormatSnifferTests
{
    private static byte[] Header(params byte[] bytes)
    {
        byte[] header = new byte[FormatSniffer.HeaderBytes];
        bytes.CopyTo(header, 0);
        return header;
    }

    private static byte[] Text(string text)
    {
        byte[] header = new byte[FormatSniffer.HeaderBytes];
        Encoding.ASCII.GetBytes(text).CopyTo(header, 0);
        return header;
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x00 }, FontFormat.TrueType)]
    [InlineData(new byte[] { 0x74, 0x72, 0x75, 0x65 }, FontFormat.TrueType)]   // "true"
    [InlineData(new byte[] { 0x4F, 0x54, 0x54, 0x4F }, FontFormat.OpenTypeCff)] // "OTTO"
    [InlineData(new byte[] { 0x74, 0x74, 0x63, 0x66 }, FontFormat.Collection)]  // "ttcf"
    [InlineData(new byte[] { 0x77, 0x4F, 0x46, 0x46 }, FontFormat.Woff)]        // "wOFF"
    [InlineData(new byte[] { 0x77, 0x4F, 0x46, 0x32 }, FontFormat.Woff2)]       // "wOF2"
    [InlineData(new byte[] { 0x01, 0x66, 0x63, 0x70 }, FontFormat.Pcf)]
    [InlineData(new byte[] { 0x80, 0x01 }, FontFormat.Type1)]
    public void Sniff_RecognisesMagicBytes(byte[] magic, FontFormat expected)
        => Assert.Equal(expected, FormatSniffer.Sniff(Header(magic), "whatever.bin"));

    [Fact]
    public void Sniff_RecognisesTextFormats()
    {
        Assert.Equal(FontFormat.Bdf, FormatSniffer.Sniff(Text("STARTFONT 2.1\n"), "x.bdf"));
        Assert.Equal(FontFormat.Type1, FormatSniffer.Sniff(Text("%!PS-AdobeFont-1.0"), "x.pfa"));
        Assert.Equal(FontFormat.Type1, FormatSniffer.Sniff(Text("%!FontType1-1.0"), "x.pfa"));
    }

    [Fact]
    public void Sniff_PrefersMagicOverExtension()
    {
        // A CFF-based font that someone named .ttf still opens as what it is.
        Assert.Equal(
            FontFormat.OpenTypeCff,
            FormatSniffer.Sniff(Header(0x4F, 0x54, 0x54, 0x4F), "MislabelledFont.ttf"));

        // And a collection named .otf is still a collection.
        Assert.Equal(
            FontFormat.Collection,
            FormatSniffer.Sniff(Header(0x74, 0x74, 0x63, 0x66), "Family.otf"));
    }

    [Fact]
    public void Sniff_FallsBackToExtensionWhenBytesSayNothing()
    {
        byte[] nothing = Header(0x12, 0x34, 0x56, 0x78);

        Assert.Equal(FontFormat.TrueType, FormatSniffer.Sniff(nothing, "font.ttf"));
        Assert.Equal(FontFormat.OpenTypeCff, FormatSniffer.Sniff(nothing, "font.otf"));
        Assert.Equal(FontFormat.DataFork, FormatSniffer.Sniff(nothing, "font.dfont"));
        Assert.Equal(FontFormat.Unknown, FormatSniffer.Sniff(nothing, "notes.txt"));
    }

    [Fact]
    public void Sniff_TreatsMzAsAFontOnlyWhenTheNameAgrees()
    {
        byte[] executable = Header((byte)'M', (byte)'Z');

        Assert.Equal(FontFormat.WindowsBitmap, FormatSniffer.Sniff(executable, "vgasys.fon"));
        Assert.Equal(FontFormat.WindowsBitmap, FormatSniffer.Sniff(executable, "small.fnt"));

        // Every Windows executable starts this way; none of them is a font.
        Assert.Equal(FontFormat.Unknown, FormatSniffer.Sniff(executable, "setup.exe"));
    }

    [Fact]
    public void Sniff_TreatsGzipAsPcfOnlyForPcfGz()
    {
        byte[] gzip = Header(0x1F, 0x8B, 0x08);

        Assert.Equal(FontFormat.Pcf, FormatSniffer.Sniff(gzip, "terminus.pcf.gz"));
        Assert.Equal(FontFormat.Unknown, FormatSniffer.Sniff(gzip, "archive.gz"));
    }

    [Fact]
    public void Sniff_HandlesAnEmptyOrShortHeader()
    {
        Assert.Equal(FontFormat.TrueType, FormatSniffer.Sniff([], "font.ttf"));
        Assert.Equal(FontFormat.Unknown, FormatSniffer.Sniff([], ""));
        Assert.Equal(FontFormat.Unknown, FormatSniffer.Sniff([0x00], "mystery"));
    }

    [Theory]
    [InlineData(".ttf")]
    [InlineData(".OTF")]
    [InlineData(".ttc")]
    [InlineData(".woff2")]
    [InlineData(".pfb")]
    [InlineData(".bdf")]
    [InlineData(".dfont")]
    public void IsSupportedExtension_AcceptsFontExtensions(string extension)
        => Assert.True(FormatSniffer.IsSupportedExtension(extension));

    [Theory]
    [InlineData(".txt")]
    [InlineData(".afm")]
    [InlineData(".eot")]
    [InlineData("")]
    public void IsSupportedExtension_RejectsEverythingElse(string extension)
        => Assert.False(FormatSniffer.IsSupportedExtension(extension));

    [Fact]
    public void IsSupportedFileName_HandlesTheTwoPartPcfGz()
    {
        Assert.True(FormatSniffer.IsSupportedFileName("terminus.pcf.gz"));
        Assert.True(FormatSniffer.IsSupportedFileName("Inter-Regular.ttf"));
        Assert.False(FormatSniffer.IsSupportedFileName("readme.md"));
    }

    [Theory]
    [InlineData(".afm")]
    [InlineData(".pfm")]
    [InlineData(".INF")]
    public void IsCompanionExtension_RecognisesType1Metrics(string extension)
    {
        Assert.True(FormatSniffer.IsCompanionExtension(extension));

        // A companion is never listed as a font in its own right.
        Assert.False(FormatSniffer.IsSupportedExtension(extension));
    }

    [Theory]
    [InlineData(".eot")]
    [InlineData(".svg")]
    public void IsQuietlyIgnoredExtension_CoversTheFormatsOutOfScope(string extension)
        => Assert.True(FormatSniffer.IsQuietlyIgnoredExtension(extension));

    [Fact]
    public void Sniff_AgreesWithTheCorpusOnDisk()
    {
        string corpus = TestFonts.RequireCorpus();
        var expectations = new Dictionary<string, FontFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [".ttf"] = FontFormat.TrueType,
            [".otf"] = FontFormat.OpenTypeCff,
            [".ttc"] = FontFormat.Collection,
            [".woff"] = FontFormat.Woff,
            [".woff2"] = FontFormat.Woff2,
            [".pfa"] = FontFormat.Type1,
            [".pfb"] = FontFormat.Type1,
            [".bdf"] = FontFormat.Bdf,
            [".pcf"] = FontFormat.Pcf,
            [".fon"] = FontFormat.WindowsBitmap,
        };

        var wrong = new List<string>();
        foreach (string path in TestFonts.All())
        {
            string extension = Path.GetExtension(path);
            if (!expectations.TryGetValue(extension, out FontFormat expected)) continue;

            byte[] header = new byte[FormatSniffer.HeaderBytes];
            using (var stream = File.OpenRead(path))
                header = header[..stream.Read(header)];

            FontFormat actual = FormatSniffer.Sniff(header, Path.GetFileName(path));
            if (actual != expected)
                wrong.Add($"{Path.GetFileName(path)}: expected {expected}, got {actual}");
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.NotEmpty(TestFonts.All());
    }
}
