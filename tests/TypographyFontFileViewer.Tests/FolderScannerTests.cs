using System.IO;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// The scanner turns "the user opened one font" into "here is the whole folder", so what
/// it includes, what it hides and what order it returns things in is all visible.
/// </summary>
public class FolderScannerTests : IDisposable
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"tffv-scan-{Guid.NewGuid():N}");

    public FolderScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a test left a handle open; the temp folder can wait */ }
        GC.SuppressFinalize(this);
    }

    private string Touch(string relativePath, byte[]? contents = null)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents ?? [0x00, 0x01, 0x00, 0x00]);
        return path;
    }

    [Fact]
    public async Task Scan_FindsFontsAndIgnoresEverythingElse()
    {
        Touch("Alpha.ttf");
        Touch("Beta.otf");
        Touch("Gamma.woff2");
        Touch("notes.txt");
        Touch("picture.png");
        Touch("legacy.eot");           // out of scope for v1.0, skipped quietly

        var scanner = new FolderScanner();
        IReadOnlyList<FontFile> fonts = await scanner.ScanAsync(_root, false, TestCancellation);

        Assert.Equal(
            ["Alpha.ttf", "Beta.otf", "Gamma.woff2"],
            fonts.Select(font => font.FileName));
    }

    [Fact]
    public async Task Scan_HidesCompanionFilesButAttachesThemToTheirFont()
    {
        Touch("Nimbus.pfb", [0x80, 0x01, 0x00, 0x00]);
        Touch("Nimbus.afm");
        Touch("Nimbus.pfm");
        Touch("Nimbus.inf");

        var scanner = new FolderScanner();
        IReadOnlyList<FontFile> fonts = await scanner.ScanAsync(_root, false, TestCancellation);

        FontFile font = Assert.Single(fonts);
        Assert.Equal("Nimbus.pfb", font.FileName);
        Assert.Equal(FontFormat.Type1, font.Format);

        // The metrics travel with the font rather than appearing as fonts themselves.
        Assert.Equal(3, font.Companions.Count);
        Assert.EndsWith(".afm", font.Companions[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scan_OnlyAttachesCompanionsToType1Fonts()
    {
        Touch("Alpha.ttf");
        Touch("Alpha.afm");

        var scanner = new FolderScanner();
        IReadOnlyList<FontFile> fonts = await scanner.ScanAsync(_root, false, TestCancellation);

        FontFile font = Assert.Single(fonts);
        Assert.Empty(font.Companions);
    }

    [Fact]
    public async Task Scan_SortsNamesTheWayAPersonReadsThem()
    {
        foreach (string name in new[] { "Font10.ttf", "Font2.ttf", "Font1.ttf", "Font20.ttf" })
            Touch(name);

        var scanner = new FolderScanner();
        IReadOnlyList<FontFile> fonts = await scanner.ScanAsync(_root, false, TestCancellation);

        Assert.Equal(
            ["Font1.ttf", "Font2.ttf", "Font10.ttf", "Font20.ttf"],
            fonts.Select(font => font.FileName));
    }

    [Fact]
    public async Task Scan_DescendsIntoSubfoldersOnlyWhenAsked()
    {
        Touch("Top.ttf");
        Touch(Path.Combine("Nested", "Deep.ttf"));

        var scanner = new FolderScanner();

        IReadOnlyList<FontFile> shallow = await scanner.ScanAsync(_root, false, TestCancellation);
        Assert.Equal(["Top.ttf"], shallow.Select(font => font.FileName));

        IReadOnlyList<FontFile> deep = await scanner.ScanAsync(_root, true, TestCancellation);
        Assert.Equal(["Deep.ttf", "Top.ttf"], deep.Select(font => font.FileName).Order());
    }

    [Fact]
    public async Task Scan_IdentifiesFormatsByTheirBytes()
    {
        Touch("Mislabelled.ttf", [0x4F, 0x54, 0x54, 0x4F]);     // OTTO inside a .ttf
        Touch("Collection.ttf", [0x74, 0x74, 0x63, 0x66]);      // ttcf inside a .ttf

        var scanner = new FolderScanner();
        IReadOnlyList<FontFile> fonts = await scanner.ScanAsync(_root, false, TestCancellation);

        Assert.Equal(
            FontFormat.Collection,
            fonts.Single(font => font.FileName == "Collection.ttf").Format);
        Assert.Equal(
            FontFormat.OpenTypeCff,
            fonts.Single(font => font.FileName == "Mislabelled.ttf").Format);
    }

    [Fact]
    public async Task Scan_StillListsAZeroByteFont()
    {
        Touch("Empty.ttf", []);

        var scanner = new FolderScanner();
        IReadOnlyList<FontFile> fonts = await scanner.ScanAsync(_root, false, TestCancellation);

        // The row has to exist so the user can see the file is there and broken.
        FontFile font = Assert.Single(fonts);
        Assert.Equal(0, font.Length);
    }

    [Fact]
    public async Task Scan_OfAMissingFolder_ReturnsNothingRatherThanThrowing()
    {
        var scanner = new FolderScanner();
        IReadOnlyList<FontFile> fonts = await scanner.ScanAsync(
            Path.Combine(_root, "not-here"), false, TestCancellation);

        Assert.Empty(fonts);
    }

    [Fact]
    public async Task Scan_OfTheRealCorpus_FindsEveryFormat()
    {
        string corpus = TestFonts.RequireCorpus();

        var scanner = new FolderScanner();
        IReadOnlyList<FontFile> fonts = await scanner.ScanAsync(corpus, false, TestCancellation);

        Assert.NotEmpty(fonts);

        FontFormat[] expected =
        [
            FontFormat.TrueType, FontFormat.OpenTypeCff, FontFormat.Collection,
            FontFormat.Woff, FontFormat.Woff2, FontFormat.Type1,
            FontFormat.Bdf, FontFormat.Pcf, FontFormat.WindowsBitmap,
        ];

        foreach (FontFormat format in expected)
            Assert.Contains(fonts, font => font.Format == format);

        // The folder's own README is not a font.
        Assert.DoesNotContain(fonts, font => font.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>"Font2" belongs before "Font10", however the characters sort.</summary>
public class NaturalFileNameComparerTests
{
    [Theory]
    [InlineData("Font1.ttf", "Font2.ttf", -1)]
    [InlineData("Font2.ttf", "Font10.ttf", -1)]
    [InlineData("Font10.ttf", "Font2.ttf", 1)]
    [InlineData("Font2.ttf", "Font2.ttf", 0)]
    [InlineData("Font02.ttf", "Font2.ttf", 0)]          // leading zeros do not change the number
    [InlineData("Alpha.ttf", "Beta.ttf", -1)]
    [InlineData("alpha.ttf", "ALPHA.ttf", 0)]           // case-insensitive, like the file system
    [InlineData("A.ttf", "A1.ttf", -1)]
    public void Compare_OrdersNumbersNumerically(string left, string right, int expected)
    {
        int actual = NaturalFileNameComparer.Instance.Compare(left, right);
        Assert.Equal(expected, Math.Sign(actual));
    }

    [Fact]
    public void Compare_SortsAWholeListTheWayAPersonWould()
    {
        string[] names =
            ["Weight900.ttf", "Weight100.ttf", "Weight50.ttf", "Weight9.ttf", "Weight200.ttf"];

        Array.Sort(names, NaturalFileNameComparer.Instance);

        Assert.Equal(
            ["Weight9.ttf", "Weight50.ttf", "Weight100.ttf", "Weight200.ttf", "Weight900.ttf"],
            names);
    }
}
