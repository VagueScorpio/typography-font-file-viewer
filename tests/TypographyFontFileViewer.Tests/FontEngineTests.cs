using System.IO;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// Phase 1 acceptance: the engine opens every font in the corpus, reports the right face
/// counts and names, and renders bitmaps that actually have ink in them.
/// </summary>
public class FontEngineTests
{
    /// <summary>The runner's token, so a wedged test can actually be cancelled.</summary>
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private const string SampleText = "Hamburgefonstiv 123";

    private static int CountInkPixels(byte[] bgra)
    {
        int ink = 0;
        for (int i = 0; i < bgra.Length; i += 4)
            if (bgra[i] < 200) ink++;
        return ink;
    }

    // --- Opening every font ----------------------------------------------------

    [Fact]
    public async Task EveryCorpusFont_Opens()
    {
        IReadOnlyList<string> fonts = TestFonts.All();
        if (fonts.Count == 0) TestFonts.RequireCorpus();

        using var service = new RenderService();
        var failures = new List<string>();

        foreach (string path in fonts)
        {
            FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);

            if (summaries.Length == 0)
            {
                failures.Add($"{Path.GetFileName(path)}: no summaries at all");
                continue;
            }

            foreach (FaceSummary summary in summaries.Where(s => s.IsBroken))
                failures.Add($"{Path.GetFileName(path)} face {summary.FaceIndex}: {summary.Error}");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} of {fonts.Count} corpus fonts failed:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task EveryCorpusFont_ReportsAFamilyName()
    {
        IReadOnlyList<string> fonts = TestFonts.All();
        if (fonts.Count == 0) TestFonts.RequireCorpus();

        using var service = new RenderService();
        var nameless = new List<string>();

        foreach (string path in fonts)
        {
            FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);
            foreach (FaceSummary summary in summaries)
                if (!summary.IsBroken && string.IsNullOrWhiteSpace(summary.Family))
                    nameless.Add($"{Path.GetFileName(path)} face {summary.FaceIndex}");
        }

        Assert.True(nameless.Count == 0,
            "These fonts opened but reported no family name:\n" + string.Join("\n", nameless));
    }

    [Fact]
    public async Task EveryCorpusFont_RendersInk()
    {
        IReadOnlyList<string> fonts = TestFonts.All();
        if (fonts.Count == 0) TestFonts.RequireCorpus();

        using var service = new RenderService();
        var blank = new List<string>();

        foreach (string path in fonts)
        {
            FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);
            FaceSummary summary = summaries[0];
            if (summary.IsBroken) continue;

            var key = FaceKey.For(path, summary.FaceIndex);
            RenderedText rendered = await service.RenderTextAsync(
                key, SampleText, pointSize: 24, dpi: 96, maxWidthPx: 0, RenderStyle.Light, cancellationToken: TestCancellation);

            if (rendered.Width <= 0 || rendered.Height <= 0)
            {
                blank.Add($"{Path.GetFileName(path)}: empty bitmap");
                continue;
            }

            int ink = CountInkPixels(rendered.Bgra);
            if (ink < 20) blank.Add($"{Path.GetFileName(path)}: only {ink} ink pixels");
        }

        Assert.True(blank.Count == 0,
            "These fonts rendered nothing visible:\n" + string.Join("\n", blank));
    }

    // --- Collections -----------------------------------------------------------

    [Fact]
    public async Task Collection_ReportsEveryFaceSeparately()
    {
        string path = TestFonts.Require("TestCollection.ttc");

        using var service = new RenderService();
        FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);

        Assert.Equal(2, summaries.Length);
        Assert.All(summaries, summary =>
        {
            Assert.False(summary.IsBroken, summary.Error);
            Assert.Equal(2, summary.FaceCount);
            Assert.Equal(FontFormat.Collection, summary.Format);
        });

        // The two faces are different fonts, and each says which one it is.
        Assert.Equal(0, summaries[0].FaceIndex);
        Assert.Equal(1, summaries[1].FaceIndex);
        Assert.Equal("(1/2)", summaries[0].FaceLabel);
        Assert.NotEqual(summaries[0].DisplayName, summaries[1].DisplayName);
    }

    [Fact]
    public async Task Collection_RendersEachFaceDifferently()
    {
        string path = TestFonts.Require("TestCollection.ttc");
        using var service = new RenderService();

        RenderedText first = await service.RenderTextAsync(
            FaceKey.For(path, 0), "Weight", 32, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
        RenderedText second = await service.RenderTextAsync(
            FaceKey.For(path, 1), "Weight", 32, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);

        // Regular and Bold of the same family must not produce identical pixels.
        Assert.False(first.Bgra.AsSpan().SequenceEqual(second.Bgra),
            "Both faces of the collection rendered identically.");
    }

    [Fact]
    public async Task SystemCollection_EnumeratesItsFaces()
    {
        string? path = TestFonts.OptionalSystemFont("cambria.ttc");
        if (path is null) Assert.Skip("cambria.ttc is not installed.");

        using var service = new RenderService();
        FaceSummary[] summaries = await service.ReadSummariesAsync(path!, cancellationToken: TestCancellation);

        Assert.True(summaries.Length >= 2, $"Expected several faces, got {summaries.Length}.");
        Assert.All(summaries, summary => Assert.False(summary.IsBroken, summary.Error));
    }

    // --- Names and metadata ----------------------------------------------------

    [Fact]
    public async Task NamesComeFromTheNameTable()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");

        using var service = new RenderService();
        FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);

        Assert.Single(summaries);
        Assert.Contains("Inter", summaries[0].Family, StringComparison.OrdinalIgnoreCase);
        Assert.True(summaries[0].GlyphCount > 100);
        Assert.True(summaries[0].IsScalable);
    }

    [Fact]
    public async Task Info_ReadsTheNameTableAndMetrics()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");

        using var service = new RenderService();
        FontInfo info = await service.ReadInfoAsync(FaceKey.For(path, 0), cancellationToken: TestCancellation);

        Assert.Equal("TrueType", info.OutlineFormat);
        Assert.Contains("Inter", info.Family ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(info.UnitsPerEm > 0, "units per em should be known");
        Assert.True(info.GlyphCount > 100);
        Assert.True(info.MappedCharacterCount > 50, "the cmap should map plenty of characters");
        Assert.True(info.CharmapCount >= 1);
        Assert.NotEqual(EmbeddingPermission.Unknown, info.Embedding);
        Assert.False(string.IsNullOrWhiteSpace(info.Copyright), "an OFL font states its copyright");
    }

    [Fact]
    public async Task Info_ReadsCffOutlinesAsSuch()
    {
        string path = TestFonts.Require("SourceSerif4-Regular.otf");

        using var service = new RenderService();
        FontInfo info = await service.ReadInfoAsync(FaceKey.For(path, 0), cancellationToken: TestCancellation);

        Assert.Equal("CFF", info.OutlineFormat);
        Assert.False(string.IsNullOrWhiteSpace(info.PostScriptName));
    }

    [Fact]
    public async Task VariableFont_ListsItsAxes()
    {
        string path = TestFonts.Require("Inter[opsz,wght].ttf");

        using var service = new RenderService();
        FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);
        FontInfo info = await service.ReadInfoAsync(FaceKey.For(path, 0), cancellationToken: TestCancellation);

        Assert.True(summaries[0].IsVariable);
        Assert.True(info.IsVariable);
        Assert.Contains(info.VariationAxes, axis => axis.Tag == "wght");
        Assert.Contains(info.VariationAxes, axis => axis.Tag == "opsz");
        Assert.All(info.VariationAxes, axis =>
        {
            Assert.True(axis.Minimum <= axis.Default, $"{axis.Tag}: default below minimum");
            Assert.True(axis.Default <= axis.Maximum, $"{axis.Tag}: default above maximum");
        });
        Assert.NotEmpty(info.NamedInstances);
    }

    // --- Type 1 ----------------------------------------------------------------

    [Fact]
    public async Task Type1Font_OpensAndRenders()
    {
        string path = TestFonts.All()
            .FirstOrDefault(p => Path.GetExtension(p) is ".pfa" or ".pfb")
            ?? TestFonts.Require("NimbusRoman-Regular.pfa");

        using var service = new RenderService();
        FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);

        Assert.False(summaries[0].IsBroken, summaries[0].Error);
        Assert.Equal(FontFormat.Type1, summaries[0].Format);
        Assert.False(string.IsNullOrWhiteSpace(summaries[0].Family));

        FontInfo info = await service.ReadInfoAsync(FaceKey.For(path, 0), cancellationToken: TestCancellation);
        Assert.Equal("Type 1", info.OutlineFormat);

        // Type 1 is not SFNT, so this exercises the FreeType fallback shaping path.
        RenderedText rendered = await service.RenderTextAsync(
            FaceKey.For(path, 0), "AVWA Type 1", 36, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);

        Assert.True(CountInkPixels(rendered.Bgra) > 50);
    }

    // --- Bitmap fonts ----------------------------------------------------------

    [Theory]
    [InlineData(".bdf")]
    [InlineData(".pcf")]
    [InlineData(".fon")]
    public async Task BitmapFonts_ReportFixedSizesAndRender(string extension)
    {
        IReadOnlyList<string> fonts = TestFonts.WithExtension(extension);
        if (fonts.Count == 0) Assert.Skip($"No {extension} font in the corpus.");

        string path = fonts[0];
        using var service = new RenderService();

        FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);
        FaceSummary summary = summaries[0];

        Assert.False(summary.IsBroken, summary.Error);
        Assert.False(summary.IsScalable, "a bitmap font is not scalable");
        Assert.NotEmpty(summary.FixedSizes);

        // Ask for a size the font certainly does not have; it must snap to one it does.
        RenderedText rendered = await service.RenderTextAsync(
            FaceKey.For(path, summary.FaceIndex), "Bitmap", 41, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);

        Assert.True(rendered.Width > 0 && rendered.Height > 0);
        Assert.True(CountInkPixels(rendered.Bgra) > 20,
            $"{Path.GetFileName(path)} rendered no ink at a size it had to snap.");
    }

    /// <summary>
    /// A bitmap font must render the characters it was given, not a row of .notdef boxes.
    /// </summary>
    /// <remarks>
    /// Windows FNT fonts index their glyphs by raw byte value rather than by Unicode, so
    /// asking for code points returns glyph 0 every time. The result still has plenty of
    /// ink — .notdef is usually a filled box — so a test that only counts dark pixels
    /// passes while the text is unreadable. Rendering two different strings and
    /// demanding different pixels is what actually catches it.
    /// </remarks>
    [Theory]
    [InlineData(".bdf")]
    [InlineData(".pcf")]
    [InlineData(".fon")]
    public async Task BitmapFonts_RenderTheActualCharacters(string extension)
    {
        IReadOnlyList<string> fonts = TestFonts.WithExtension(extension);
        if (fonts.Count == 0) Assert.Skip($"No {extension} font in the corpus.");

        using var service = new RenderService();
        var key = FaceKey.For(fonts[0], 0);

        RenderedText first = await service.RenderTextAsync(
            key, "AAAA", 12, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
        RenderedText second = await service.RenderTextAsync(
            key, "WWWW", 12, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);

        Assert.True(CountInkPixels(first.Bgra) > 4, "nothing was drawn at all");
        Assert.False(
            first.Width == second.Width && first.Bgra.AsSpan().SequenceEqual(second.Bgra),
            $"{Path.GetFileName(fonts[0])} drew 'AAAA' and 'WWWW' identically, which means "
            + "it is drawing .notdef rather than the characters asked for.");
    }

    [Fact]
    public async Task AWindowsFntFont_MapsItsCharactersByByteValue()
    {
        IReadOnlyList<string> fonts = TestFonts.WithExtension(".fon");
        if (fonts.Count == 0) Assert.Skip("No .fon font in the corpus.");

        using var service = new RenderService();
        var key = FaceKey.For(fonts[0], 0);

        // Every one of these is a distinct glyph in any reasonable text font.
        IReadOnlyList<GlyphEntry> glyphs = await service.ListGlyphsAsync(
            key, cancellationToken: TestCancellation);

        Assert.NotEmpty(glyphs);

        var distinct = new HashSet<uint>();
        foreach (char character in "ABCWXYZ")
        {
            GlyphEntry? entry = glyphs.FirstOrDefault(g => g.Codepoint == character);
            Assert.True(entry is not null, $"the font maps nothing for '{character}'");
            distinct.Add(entry!.Value.GlyphIndex);
        }

        Assert.True(distinct.Count >= 6,
            $"Expected distinct glyphs per letter, got {distinct.Count} for seven letters.");
    }

    [Fact]
    public async Task BitmapFont_ReportsTheSizesItHas()
    {
        IReadOnlyList<string> fonts = TestFonts.WithExtension(".pcf");
        if (fonts.Count == 0) Assert.Skip("No .pcf font in the corpus.");

        using var service = new RenderService();
        IReadOnlyList<int> sizes = await service.ReadFixedSizesAsync(FaceKey.For(fonts[0], 0), cancellationToken: TestCancellation);

        Assert.NotEmpty(sizes);
        Assert.All(sizes, size => Assert.InRange(size, 1, 512));
    }

    // --- Colour fonts ----------------------------------------------------------

    [Fact]
    public async Task ColourFont_RendersThroughTheBgraPath()
    {
        string? path = TestFonts.OptionalSystemFont("seguiemj.ttf");
        if (path is null) Assert.Skip("seguiemj.ttf is not installed.");

        using var service = new RenderService();
        var key = FaceKey.For(path!, 0);

        FontInfo info = await service.ReadInfoAsync(key, cancellationToken: TestCancellation);
        Assert.True(info.HasColor, "Segoe UI Emoji should report colour glyphs");

        RenderedText rendered = await service.RenderTextAsync(
            key, "\U0001F600\U0001F680", 48, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);

        // Colour glyphs must come out coloured, not as flat silhouettes: look for a
        // pixel whose channels differ from one another.
        bool sawColour = false;
        for (int i = 0; i < rendered.Bgra.Length; i += 4)
        {
            byte b = rendered.Bgra[i], g = rendered.Bgra[i + 1], r = rendered.Bgra[i + 2];
            if (Math.Abs(b - g) > 24 || Math.Abs(g - r) > 24) { sawColour = true; break; }
        }

        Assert.True(sawColour, "Emoji rendered without colour.");
    }

    // --- Large fonts -----------------------------------------------------------

    [Fact]
    public async Task LargeCjkFont_ListsGlyphsWithoutStalling()
    {
        string? path = TestFonts.OptionalSystemFont("msyh.ttc");
        if (path is null) Assert.Skip("msyh.ttc is not installed.");

        using var service = new RenderService();
        var key = FaceKey.For(path!, 0);

        IReadOnlyList<GlyphEntry> glyphs = await service.ListGlyphsAsync(key, cancellationToken: TestCancellation);

        Assert.True(glyphs.Count > 10_000, $"Expected a large glyph set, got {glyphs.Count}.");
        Assert.All(glyphs.Take(200), glyph => Assert.True(glyph.IsMapped));

        // Code points come back in ascending order, which the glyph grid relies on.
        for (int i = 1; i < Math.Min(glyphs.Count, 5000); i++)
            Assert.True(glyphs[i].Codepoint > glyphs[i - 1].Codepoint);
    }

    // Web fonts are no longer a special case: the corpus-wide tests above cover
    // .woff and .woff2 alongside everything else. WoffDecoderTests checks that the
    // decoders reproduce the original font exactly.
}
