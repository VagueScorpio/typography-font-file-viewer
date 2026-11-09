using System.IO;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// Phase 2 acceptance: for every X.ttf / X.woff / X.woff2 triple, the decoded fonts must
/// be the same font — same glyph count, same family name, and the same pixels — and
/// malformed input must raise <see cref="FontLoadException"/> rather than crashing.
/// </summary>
public class WoffDecoderTests
{
    /// <summary>The runner's token, so a wedged test can actually be cancelled.</summary>
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    /// <summary>Text with enough variety to catch a mistake in any single glyph.</summary>
    private const string Specimen = "Hamburgefonstiv AVWA 0123 éñ .,;:!?";

    private static string WriteTempFile(string extension, byte[] contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tffv-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, contents);
        return path;
    }

    /// <summary>Every SFNT font in the corpus that has both web-font siblings.</summary>
    private static IEnumerable<(string Sfnt, string Woff, string Woff2)> Triples()
    {
        foreach (string sfnt in TestFonts.WithExtension(".ttf", ".otf"))
        {
            string woff = Path.ChangeExtension(sfnt, ".woff");
            string woff2 = Path.ChangeExtension(sfnt, ".woff2");

            if (File.Exists(woff) && File.Exists(woff2))
                yield return (sfnt, woff, woff2);
        }
    }

    // --- The triples all agree -------------------------------------------------

    [Fact]
    public async Task EveryTriple_ReportsTheSameGlyphCountAndFamily()
    {
        var triples = Triples().ToList();
        if (triples.Count == 0) TestFonts.RequireCorpus();
        Assert.NotEmpty(triples);

        using var service = new RenderService();
        var mismatches = new List<string>();

        foreach ((string sfnt, string woff, string woff2) in triples)
        {
            FaceSummary original = (await service.ReadSummariesAsync(sfnt, cancellationToken: TestCancellation))[0];
            FaceSummary fromWoff = (await service.ReadSummariesAsync(woff, cancellationToken: TestCancellation))[0];
            FaceSummary fromWoff2 = (await service.ReadSummariesAsync(woff2, cancellationToken: TestCancellation))[0];

            string name = Path.GetFileNameWithoutExtension(sfnt);

            if (fromWoff.IsBroken) { mismatches.Add($"{name}.woff: {fromWoff.Error}"); continue; }
            if (fromWoff2.IsBroken) { mismatches.Add($"{name}.woff2: {fromWoff2.Error}"); continue; }

            if (original.GlyphCount != fromWoff.GlyphCount)
                mismatches.Add($"{name}.woff: {fromWoff.GlyphCount} glyphs, expected {original.GlyphCount}");
            if (original.GlyphCount != fromWoff2.GlyphCount)
                mismatches.Add($"{name}.woff2: {fromWoff2.GlyphCount} glyphs, expected {original.GlyphCount}");

            if (original.Family != fromWoff.Family)
                mismatches.Add($"{name}.woff: family '{fromWoff.Family}', expected '{original.Family}'");
            if (original.Family != fromWoff2.Family)
                mismatches.Add($"{name}.woff2: family '{fromWoff2.Family}', expected '{original.Family}'");

            // The badge still reflects how the file arrived, not what it decoded to.
            if (fromWoff.Format != FontFormat.Woff) mismatches.Add($"{name}.woff: format {fromWoff.Format}");
            if (fromWoff2.Format != FontFormat.Woff2) mismatches.Add($"{name}.woff2: format {fromWoff2.Format}");
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    /// <summary>
    /// The strong test: decoded web fonts must rasterize to exactly the same pixels as
    /// the font they were made from.
    /// </summary>
    /// <remarks>
    /// This is what catches a wrong triplet-decode sign, a misplaced bounding box or a
    /// dropped hinting instruction. A glyph count can match while the outlines are
    /// quietly wrong; identical pixels cannot.
    /// </remarks>
    [Fact]
    public async Task EveryTriple_RendersIdenticalPixels()
    {
        var triples = Triples().ToList();
        if (triples.Count == 0) TestFonts.RequireCorpus();
        Assert.NotEmpty(triples);

        using var service = new RenderService();
        var differences = new List<string>();

        foreach ((string sfnt, string woff, string woff2) in triples)
        {
            string name = Path.GetFileNameWithoutExtension(sfnt);

            RenderedText original = await Render(service, sfnt);
            RenderedText fromWoff = await Render(service, woff);
            RenderedText fromWoff2 = await Render(service, woff2);

            differences.AddRange(Compare($"{name}.woff", original, fromWoff));
            differences.AddRange(Compare($"{name}.woff2", original, fromWoff2));
        }

        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    private static Task<RenderedText> Render(RenderService service, string path, double points = 32)
        => service.RenderTextAsync(
            FaceKey.For(path, 0), Specimen, points, 96, 0, RenderStyle.Light,
            cancellationToken: TestCancellation);

    private static IEnumerable<string> Compare(string label, RenderedText expected, RenderedText actual)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            yield return $"{label}: {actual.Width}x{actual.Height}, expected "
                       + $"{expected.Width}x{expected.Height}";
            yield break;
        }

        if (expected.GlyphCount != actual.GlyphCount)
            yield return $"{label}: {actual.GlyphCount} glyphs shaped, expected {expected.GlyphCount}";

        if (!expected.Bgra.AsSpan().SequenceEqual(actual.Bgra))
        {
            int differing = 0;
            for (int i = 0; i < expected.Bgra.Length; i++)
                if (expected.Bgra[i] != actual.Bgra[i]) differing++;

            yield return $"{label}: {differing} of {expected.Bgra.Length} bytes differ";
        }
    }

    // --- The other transform paths ---------------------------------------------

    /// <summary>
    /// A WOFF2 whose hmtx was transformed drops the left side bearings; the decoder has
    /// to put them back from the reconstructed glyph bounding boxes.
    /// </summary>
    /// <remarks>
    /// Left side bearings decide where each glyph sits relative to its advance, so
    /// getting them wrong shifts glyphs without changing their shapes. Comparing pixels
    /// against the untransformed original is what makes that visible.
    /// </remarks>
    [Fact]
    public async Task HmtxTransform_RebuildsTheLeftSideBearings()
    {
        string original = TestFonts.Require("Inter-Regular.ttf");
        string transformed = TestFonts.Require("Inter-Regular-hmtxtransform.woff2");

        using var service = new RenderService();

        RenderedText expected = await Render(service, original);
        RenderedText actual = await Render(service, transformed);

        Assert.Empty(Compare("hmtx-transformed", expected, actual));
    }

    [Fact]
    public async Task AWoff2WithNoTransforms_DecodesToo()
    {
        string original = TestFonts.Require("Inter-Regular.ttf");
        string untransformed = TestFonts.Require("Inter-Regular-notransform.woff2");

        using var service = new RenderService();

        RenderedText expected = await Render(service, original);
        RenderedText actual = await Render(service, untransformed);

        Assert.Empty(Compare("untransformed", expected, actual));
    }

    [Fact]
    public async Task CffWebFonts_DecodeAsWell()
    {
        string woff2 = TestFonts.Require("SourceSerif4-Regular.woff2");

        using var service = new RenderService();
        FontInfo info = await service.ReadInfoAsync(FaceKey.For(woff2, 0), cancellationToken: TestCancellation);

        // A CFF font has no glyf to transform, so this exercises the plain pass-through
        // of every table through the Brotli stream.
        Assert.Equal("CFF", info.OutlineFormat);
        Assert.Equal(FontFormat.Woff2, info.Container);
        Assert.True(info.GlyphCount > 100);
    }

    [Fact]
    public async Task VariableWebFonts_KeepTheirAxes()
    {
        string woff2 = TestFonts.Require("Inter[opsz,wght].woff2");

        using var service = new RenderService();
        FontInfo info = await service.ReadInfoAsync(FaceKey.For(woff2, 0), cancellationToken: TestCancellation);

        Assert.True(info.IsVariable);
        Assert.Contains(info.VariationAxes, axis => axis.Tag == "wght");
        Assert.Contains(info.VariationAxes, axis => axis.Tag == "opsz");
    }

    [Fact]
    public async Task WebFonts_ListTheirGlyphsLikeAnyOther()
    {
        string sfnt = TestFonts.Require("Inter-Regular.ttf");
        string woff2 = TestFonts.Require("Inter-Regular.woff2");

        using var service = new RenderService();

        IReadOnlyList<GlyphEntry> fromSfnt =
            await service.ListGlyphsAsync(FaceKey.For(sfnt, 0), cancellationToken: TestCancellation);
        IReadOnlyList<GlyphEntry> fromWoff2 =
            await service.ListGlyphsAsync(FaceKey.For(woff2, 0), cancellationToken: TestCancellation);

        Assert.Equal(fromSfnt.Count, fromWoff2.Count);
        Assert.Equal(
            fromSfnt.Take(100).Select(g => g.Codepoint),
            fromWoff2.Take(100).Select(g => g.Codepoint));
    }

    // --- Malformed input -------------------------------------------------------

    public static TheoryData<string> WebFontExtensions() => [".woff", ".woff2"];

    [Theory]
    [MemberData(nameof(WebFontExtensions))]
    public async Task ATruncatedWebFont_ReportsAnErrorRatherThanCrashing(string extension)
    {
        IReadOnlyList<string> sources = TestFonts.WithExtension(extension);
        if (sources.Count == 0) Assert.Skip($"No {extension} font in the corpus.");

        byte[] whole = File.ReadAllBytes(sources[0]);
        using var service = new RenderService();

        // Cut at several points: inside the header, inside the directory, and inside
        // the compressed stream. Each lands in a different part of the decoder.
        foreach (double fraction in new[] { 0.02, 0.1, 0.5, 0.95 })
        {
            string path = WriteTempFile(extension, whole[..(int)(whole.Length * fraction)]);
            try
            {
                FaceSummary summary = (await service.ReadSummariesAsync(path, cancellationToken: TestCancellation))[0];
                Assert.True(summary.IsBroken,
                    $"A {extension} truncated to {fraction:P0} opened anyway.");
                Assert.False(string.IsNullOrWhiteSpace(summary.Error));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Theory]
    [MemberData(nameof(WebFontExtensions))]
    public async Task ACorruptedWebFont_ReportsAnErrorRatherThanCrashing(string extension)
    {
        IReadOnlyList<string> sources = TestFonts.WithExtension(extension);
        if (sources.Count == 0) Assert.Skip($"No {extension} font in the corpus.");

        byte[] whole = File.ReadAllBytes(sources[0]);
        using var service = new RenderService();

        // Flip bytes deep in the compressed payload, past the header and directory.
        var random = new Random(20261108);
        byte[] damaged = (byte[])whole.Clone();
        for (int i = 0; i < 200; i++)
        {
            int at = random.Next(whole.Length / 2, whole.Length);
            damaged[at] ^= 0xFF;
        }

        string path = WriteTempFile(extension, damaged);
        try
        {
            // Either it fails cleanly or the damage happened to land somewhere harmless.
            // What it must never do is crash the engine or hang.
            FaceSummary summary = (await service.ReadSummariesAsync(path, cancellationToken: TestCancellation))[0];
            if (!summary.IsBroken)
            {
                RenderedText rendered = await Render(service, path);
                Assert.True(rendered.Width > 0);
            }

            // And the engine is still alive afterwards.
            FaceSummary good = (await service.ReadSummariesAsync(sources[0], cancellationToken: TestCancellation))[0];
            Assert.False(good.IsBroken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AWebFontWithALyingLength_IsRejected()
    {
        IReadOnlyList<string> sources = TestFonts.WithExtension(".woff2");
        if (sources.Count == 0) Assert.Skip("No .woff2 font in the corpus.");

        byte[] whole = File.ReadAllBytes(sources[0]);

        // The length field at offset 8 must match the real file size.
        byte[] lying = (byte[])whole.Clone();
        lying[8] = 0x7F; lying[9] = 0xFF; lying[10] = 0xFF; lying[11] = 0xFF;

        string path = WriteTempFile(".woff2", lying);
        try
        {
            using var service = new RenderService();
            FaceSummary summary = (await service.ReadSummariesAsync(path, cancellationToken: TestCancellation))[0];

            Assert.True(summary.IsBroken);
            Assert.Contains("bytes", summary.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AWebFontClaimingAnImplausibleUnpackedSize_IsRejected()
    {
        IReadOnlyList<string> sources = TestFonts.WithExtension(".woff2");
        if (sources.Count == 0) Assert.Skip("No .woff2 font in the corpus.");

        byte[] whole = File.ReadAllBytes(sources[0]);

        // totalSfntSize sits at offset 16. A file that says it unpacks to a gigabyte is
        // a decompression bomb, and must be refused before anything is allocated.
        byte[] bomb = (byte[])whole.Clone();
        bomb[16] = 0x40; bomb[17] = 0x00; bomb[18] = 0x00; bomb[19] = 0x00;

        string path = WriteTempFile(".woff2", bomb);
        try
        {
            using var service = new RenderService();
            FaceSummary summary = (await service.ReadSummariesAsync(path, cancellationToken: TestCancellation))[0];

            Assert.True(summary.IsBroken);
            Assert.Contains("MB", summary.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(WebFontExtensions))]
    public async Task AWebFontWithNoTables_IsRejected(string extension)
    {
        IReadOnlyList<string> sources = TestFonts.WithExtension(extension);
        if (sources.Count == 0) Assert.Skip($"No {extension} font in the corpus.");

        byte[] whole = File.ReadAllBytes(sources[0]);

        // numTables sits at offset 12 in both formats.
        byte[] empty = (byte[])whole.Clone();
        empty[12] = 0; empty[13] = 0;

        string path = WriteTempFile(extension, empty);
        try
        {
            using var service = new RenderService();
            FaceSummary summary = (await service.ReadSummariesAsync(path, cancellationToken: TestCancellation))[0];

            Assert.True(summary.IsBroken);
            Assert.Contains("no tables", summary.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileWithAWoffSignatureButNothingElse_IsRejected()
    {
        using var service = new RenderService();

        foreach ((string extension, byte[] signature) in new (string, byte[])[]
        {
            (".woff", [(byte)'w', (byte)'O', (byte)'F', (byte)'F']),
            (".woff2", [(byte)'w', (byte)'O', (byte)'F', (byte)'2']),
        })
        {
            string path = WriteTempFile(extension, signature);
            try
            {
                FaceSummary summary = (await service.ReadSummariesAsync(path, cancellationToken: TestCancellation))[0];
                Assert.True(summary.IsBroken);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ManyMalformedWebFontsInARow_LeaveTheEngineWorking()
    {
        IReadOnlyList<string> sources = TestFonts.WithExtension(".woff2");
        if (sources.Count == 0) Assert.Skip("No .woff2 font in the corpus.");

        byte[] whole = File.ReadAllBytes(sources[0]);
        var random = new Random(7);
        using var service = new RenderService();

        // A folder full of junk renamed to .woff2 is a real scenario, and the engine
        // thread has to come through all of it.
        for (int i = 0; i < 40; i++)
        {
            byte[] junk = new byte[random.Next(16, 4096)];
            random.NextBytes(junk);
            junk[0] = (byte)'w'; junk[1] = (byte)'O'; junk[2] = (byte)'F'; junk[3] = (byte)'2';

            string path = WriteTempFile(".woff2", junk);
            try
            {
                FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);
                Assert.NotEmpty(summaries);
            }
            finally
            {
                File.Delete(path);
            }
        }

        FaceSummary good = (await service.ReadSummariesAsync(sources[0], cancellationToken: TestCancellation))[0];
        Assert.False(good.IsBroken, "the engine stopped working after a run of bad files");
    }

    [Fact]
    public async Task AWebFontIsNotLockedWhileItIsOpen()
    {
        IReadOnlyList<string> sources = TestFonts.WithExtension(".woff2");
        if (sources.Count == 0) Assert.Skip("No .woff2 font in the corpus.");

        string copy = WriteTempFile(".woff2", File.ReadAllBytes(sources[0]));
        try
        {
            using var service = new RenderService();
            await Render(service, copy);

            // Decoding reads the file into memory and hands FreeType the decoded bytes,
            // so the original is no more locked than any other font.
            File.Delete(copy);
            Assert.False(File.Exists(copy));
        }
        finally
        {
            if (File.Exists(copy)) File.Delete(copy);
        }
    }
}
