using System.IO;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// The engine's behaviour at its boundary: no file locks, no crashes on bad input,
/// cancellation that works, and a cache that actually caches.
/// </summary>
public class RenderServiceTests
{
    /// <summary>The runner's token, so a wedged test can actually be cancelled.</summary>
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static int CountInkPixels(byte[] bgra)
    {
        int ink = 0;
        for (int i = 0; i < bgra.Length; i += 4)
            if (bgra[i] < 200) ink++;
        return ink;
    }

    private static string WriteTempFile(string extension, byte[] contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tffv-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, contents);
        return path;
    }

    // --- No file locks ---------------------------------------------------------

    [Fact]
    public async Task Rendering_DoesNotLockTheFile()
    {
        string source = TestFonts.RequireSystemFont();
        string copy = WriteTempFile(".ttf", File.ReadAllBytes(source));

        try
        {
            using var service = new RenderService();
            var key = FaceKey.For(copy, 0);

            RenderedText rendered = await service.RenderTextAsync(
                key, "Still open", 32, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
            Assert.True(CountInkPixels(rendered.Bgra) > 20);

            // The favourites design rests on this: delete the font while it is on screen.
            File.Delete(copy);
            Assert.False(File.Exists(copy));

            // And the cached face still renders afterwards, from memory.
            RenderedText again = await service.RenderTextAsync(
                key, "Still open", 32, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
            Assert.True(CountInkPixels(again.Bgra) > 20);
        }
        finally
        {
            if (File.Exists(copy)) File.Delete(copy);
        }
    }

    [Fact]
    public async Task Rendering_DoesNotStopTheFileBeingOverwritten()
    {
        string source = TestFonts.RequireSystemFont();
        string copy = WriteTempFile(".ttf", File.ReadAllBytes(source));

        try
        {
            using var service = new RenderService();
            await service.RenderTextAsync(FaceKey.For(copy, 0), "Open", 24, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);

            // Overwriting in place is what an installer or a sync client does.
            File.WriteAllBytes(copy, File.ReadAllBytes(source));
            Assert.True(File.Exists(copy));
        }
        finally
        {
            if (File.Exists(copy)) File.Delete(copy);
        }
    }

    // --- Bad input never crashes ----------------------------------------------

    [Fact]
    public async Task Summaries_ForGarbage_ReportAnErrorInsteadOfThrowing()
    {
        byte[] junk = [.. Enumerable.Range(0, 8192).Select(i => (byte)(i * 31))];
        string path = WriteTempFile(".ttf", junk);

        try
        {
            using var service = new RenderService();
            FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);

            FaceSummary summary = Assert.Single(summaries);
            Assert.True(summary.IsBroken);
            Assert.False(string.IsNullOrWhiteSpace(summary.Error));

            // The row still has something to show: the file name.
            Assert.Equal(Path.GetFileName(path), summary.DisplayName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Summaries_ForAZeroByteFile_ReportAnError()
    {
        string path = WriteTempFile(".woff2", []);

        try
        {
            using var service = new RenderService();
            FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);

            Assert.True(Assert.Single(summaries).IsBroken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Summaries_ForAMissingFile_ReportAnError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tffv-gone-{Guid.NewGuid():N}.ttf");

        using var service = new RenderService();
        FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);

        Assert.True(Assert.Single(summaries).IsBroken);
    }

    [Fact]
    public async Task Summaries_ForATruncatedFont_ReportAnError()
    {
        string source = TestFonts.RequireSystemFont();
        byte[] whole = File.ReadAllBytes(source);
        string path = WriteTempFile(".ttf", whole[..(whole.Length / 4)]);

        try
        {
            using var service = new RenderService();
            FaceSummary[] summaries = await service.ReadSummariesAsync(path, cancellationToken: TestCancellation);

            // Either it fails cleanly or FreeType salvages it; what it must not do is
            // crash, hang, or hand back a face that cannot render.
            foreach (FaceSummary summary in summaries.Where(s => !s.IsBroken))
            {
                RenderedText rendered = await service.RenderTextAsync(
                    FaceKey.For(path, summary.FaceIndex), "Test", 24, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
                Assert.True(rendered.Width > 0);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Rendering_AMissingFile_ThrowsFontLoadException()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tffv-gone-{Guid.NewGuid():N}.ttf");

        using var service = new RenderService();

        await Assert.ThrowsAsync<FontLoadException>(() => service.RenderTextAsync(
            new FaceKey(path, 0, 1024, DateTime.UtcNow), "Test", 24, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation));
    }

    [Fact]
    public async Task OneBadFont_DoesNotStopTheNextOne()
    {
        string good = TestFonts.RequireSystemFont();
        string bad = WriteTempFile(".ttf", [1, 2, 3, 4, 5, 6, 7, 8]);

        try
        {
            using var service = new RenderService();

            Assert.True((await service.ReadSummariesAsync(bad, cancellationToken: TestCancellation))[0].IsBroken);

            // The engine thread survived, and the very next request works.
            FaceSummary[] summaries = await service.ReadSummariesAsync(good, cancellationToken: TestCancellation);
            Assert.False(summaries[0].IsBroken);
        }
        finally
        {
            File.Delete(bad);
        }
    }

    // --- Text layout -----------------------------------------------------------

    [Fact]
    public async Task Wrapping_ProducesMoreLinesInANarrowerBox()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();
        var key = FaceKey.For(path, 0);

        const string Paragraph =
            "The quick brown fox jumps over the lazy dog, again and again and again.";

        RenderedText wide = await service.RenderTextAsync(
            key, Paragraph, 20, 96, 2000, RenderStyle.Light, cancellationToken: TestCancellation);
        RenderedText narrow = await service.RenderTextAsync(
            key, Paragraph, 20, 96, 240, RenderStyle.Light, cancellationToken: TestCancellation);

        Assert.Equal(1, wide.LineCount);
        Assert.True(narrow.LineCount > 1, "narrow text should wrap");
        Assert.True(narrow.Height > wide.Height, "more lines should be taller");
        Assert.True(narrow.Width <= wide.Width, "wrapped text should not be wider");
    }

    [Fact]
    public async Task ExplicitLineBreaks_AreHonoured()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();

        RenderedText rendered = await service.RenderTextAsync(
            FaceKey.For(path, 0), "one\ntwo\nthree", 20, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);

        Assert.Equal(3, rendered.LineCount);
    }

    [Fact]
    public async Task AWordWiderThanTheBox_IsBrokenRatherThanDropped()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();

        RenderedText rendered = await service.RenderTextAsync(
            FaceKey.For(path, 0), "Supercalifragilisticexpialidocious", 32, 96, 100, RenderStyle.Light, cancellationToken: TestCancellation);

        Assert.True(rendered.LineCount > 1, "an over-long word should break across lines");
        Assert.True(CountInkPixels(rendered.Bgra) > 50, "and still be drawn");
    }

    [Fact]
    public async Task BlankText_ProducesABackgroundOnlyBitmap()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();

        RenderedText rendered = await service.RenderTextAsync(
            FaceKey.For(path, 0), "   ", 24, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);

        Assert.True(rendered.Width > 0 && rendered.Height > 0);
        Assert.Equal(0, CountInkPixels(rendered.Bgra));
    }

    [Fact]
    public async Task DarkStyle_InvertsTheRender()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();
        var key = FaceKey.For(path, 0);

        RenderedText light = await service.RenderTextAsync(key, "Contrast", 32, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
        RenderedText dark = await service.RenderTextAsync(key, "Contrast", 32, 96, 0, RenderStyle.Dark, cancellationToken: TestCancellation);

        Assert.Equal(light.Width, dark.Width);
        Assert.Equal(light.Height, dark.Height);

        // "Ink" here means a dark pixel. On a white background only the strokes are
        // dark; on a near-black background almost everything is.
        int pixelCount = light.Bgra.Length / 4;
        Assert.True(CountInkPixels(light.Bgra) < pixelCount / 2,
            "a light render should be mostly background");
        Assert.True(CountInkPixels(dark.Bgra) > pixelCount / 2,
            "a dark render should be mostly background too — but a dark one");
    }

    [Fact]
    public async Task MissingGlyphs_AreReportedRatherThanDropped()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();

        // Private-use code points, which no ordinary text font maps. Built from their
        // numbers rather than written literally: invisible characters in source are
        // exactly the kind of thing an editor or an encoding change quietly eats.
        string privateUse = char.ConvertFromUtf32(0xE000) + char.ConvertFromUtf32(0xE001);

        RenderedText rendered = await service.RenderTextAsync(
            FaceKey.For(path, 0), privateUse, 32, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);

        Assert.True(rendered.HasMissingGlyphs);
        Assert.True(rendered.GlyphCount > 0, "missing characters still occupy glyph slots");
    }

    [Fact]
    public async Task PointSizeAndDpi_AgreeWithTheBlueprintFormula()
    {
        // Pixel size = pt x (DPI / 72), so 48 pt is 64 px at the 96 DPI baseline —
        // points are 1/72 inch, not 1/96.
        Assert.Equal(64, RenderService.ToPixelSize(48, 96));
        Assert.Equal(128, RenderService.ToPixelSize(48, 192));
        Assert.Equal(80, RenderService.ToPixelSize(48, 120));
        Assert.Equal(12, RenderService.ToPixelSize(12, 72));

        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();
        var key = FaceKey.For(path, 0);

        RenderedText at96 = await service.RenderTextAsync(key, "Size", 24, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
        RenderedText at192 = await service.RenderTextAsync(key, "Size", 24, 192, 0, RenderStyle.Light, cancellationToken: TestCancellation);

        // Twice the DPI is twice the pixels, give or take rounding and padding.
        Assert.InRange(at192.Height, at96.Height * 3 / 2, at96.Height * 5 / 2);
    }

    // --- Glyph map -------------------------------------------------------------

    [Fact]
    public async Task ListGlyphs_ReturnsMappedCharactersInOrder()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();

        IReadOnlyList<GlyphEntry> glyphs = await service.ListGlyphsAsync(FaceKey.For(path, 0), cancellationToken: TestCancellation);

        Assert.True(glyphs.Count > 100);
        Assert.All(glyphs, glyph => Assert.True(glyph.IsMapped));
        Assert.Contains(glyphs, glyph => glyph.Codepoint == 'A');

        for (int i = 1; i < glyphs.Count; i++)
            Assert.True(glyphs[i].Codepoint > glyphs[i - 1].Codepoint);
    }

    [Fact]
    public async Task ListGlyphs_CanIncludeUnmappedGlyphs()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();
        var key = FaceKey.For(path, 0);

        IReadOnlyList<GlyphEntry> mapped = await service.ListGlyphsAsync(key, includeUnmapped: false, cancellationToken: TestCancellation);
        IReadOnlyList<GlyphEntry> all = await service.ListGlyphsAsync(key, includeUnmapped: true, cancellationToken: TestCancellation);

        Assert.True(all.Count >= mapped.Count);
        if (all.Count > mapped.Count)
            Assert.Contains(all, glyph => !glyph.IsMapped);
    }

    [Fact]
    public async Task RenderGlyphCells_DrawsEachGlyphInItsOwnCell()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();
        var key = FaceKey.For(path, 0);

        IReadOnlyList<GlyphEntry> glyphs = await service.ListGlyphsAsync(key, cancellationToken: TestCancellation);
        var sample = glyphs.Where(g => g.Codepoint is >= 'A' and <= 'Z').Take(8).ToList();
        Assert.NotEmpty(sample);

        GlyphCellBitmap[] cells = await service.RenderGlyphCellsAsync(
            key, sample, cellPx: 64, RenderStyle.Light, cancellationToken: TestCancellation);

        Assert.Equal(sample.Count, cells.Length);
        Assert.All(cells, cell =>
        {
            Assert.Equal(64, cell.Width);
            Assert.Equal(64, cell.Height);
            Assert.Equal(64 * 64 * 4, cell.Bgra.Length);
            Assert.False(cell.IsBlank);
            Assert.True(CountInkPixels(cell.Bgra) > 10);
        });
    }

    [Fact]
    public async Task RenderGlyphCells_MarksABlankGlyphAsBlank()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();
        var key = FaceKey.For(path, 0);

        IReadOnlyList<GlyphEntry> glyphs = await service.ListGlyphsAsync(key, cancellationToken: TestCancellation);
        GlyphEntry space = glyphs.First(g => g.Codepoint == ' ');

        GlyphCellBitmap[] cells = await service.RenderGlyphCellsAsync(
            key, [space], cellPx: 48, RenderStyle.Light, cancellationToken: TestCancellation);

        Assert.True(cells[0].IsBlank);
    }

    // --- Cache and cancellation ------------------------------------------------

    [Fact]
    public async Task SecondRequestForTheSameFace_HitsTheCache()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();
        var key = FaceKey.For(path, 0);

        await service.RenderTextAsync(key, "First", 24, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
        int hitsAfterFirst = service.CacheStatistics.Hits;

        await service.RenderTextAsync(key, "Second", 24, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
        await service.RenderTextAsync(key, "Third", 24, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);

        Assert.True(service.CacheStatistics.Hits > hitsAfterFirst,
            "repeat requests for one face should be served from the cache");
        Assert.True(service.CacheStatistics.Count >= 1);
    }

    [Fact]
    public async Task Invalidate_DropsTheCachedFace()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();

        await service.RenderTextAsync(FaceKey.For(path, 0), "Cached", 24, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
        Assert.True(service.CacheStatistics.Count >= 1);

        int dropped = await service.InvalidateAsync(path, cancellationToken: TestCancellation);

        Assert.True(dropped >= 1);
        Assert.Equal(0, service.CacheStatistics.Count);
    }

    [Fact]
    public async Task ASmallCache_EvictsRatherThanGrowing()
    {
        string path = TestFonts.RequireSystemFont();

        // One face allowed, so every new face must push the previous one out.
        using var service = new RenderService(maximumCachedFaces: 1);

        var copies = new List<string>();
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            for (int i = 0; i < 4; i++)
            {
                string copy = WriteTempFile(".ttf", bytes);
                copies.Add(copy);
                await service.RenderTextAsync(FaceKey.For(copy, 0), "Evict", 20, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
            }

            Assert.True(service.CacheStatistics.Count <= 1,
                $"cache held {service.CacheStatistics.Count} faces with a limit of 1");
            Assert.True(service.CacheStatistics.Evictions >= 3);
        }
        finally
        {
            foreach (string copy in copies) if (File.Exists(copy)) File.Delete(copy);
        }
    }

    [Fact]
    public async Task ACancelledRequest_DoesNotComplete()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();
        await service.Ready;

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RenderTextAsync(
            FaceKey.For(path, 0), "Never drawn", 24, 96, 0, RenderStyle.Light,
            JobPriority.High, cancellation.Token));
    }

    [Fact]
    public async Task ManyQueuedRequests_AllSettle()
    {
        string path = TestFonts.RequireSystemFont();
        using var service = new RenderService();
        var key = FaceKey.For(path, 0);

        // Rapid Ctrl+Down, in effect: a burst of overlapping requests at both priorities.
        var requests = new List<Task<RenderedText>>();
        for (int i = 0; i < 60; i++)
        {
            requests.Add(service.RenderTextAsync(
                key, $"Burst {i}", 18 + (i % 12), 96, 0, RenderStyle.Light,
                i % 2 == 0 ? JobPriority.High : JobPriority.Low, cancellationToken: TestCancellation));
        }

        RenderedText[] results = await Task.WhenAll(requests);

        Assert.Equal(60, results.Length);
        Assert.All(results, rendered => Assert.True(rendered.Width > 0 && rendered.Height > 0));
    }

    [Fact]
    public async Task TheEngineReportsItsFreeTypeVersion()
    {
        using var service = new RenderService();
        await service.Ready;

        Assert.Matches(@"^\d+\.\d+\.\d+$", service.NativeVersion);
    }

    [Fact]
    public async Task AfterDispose_RequestsFailRatherThanHang()
    {
        string path = TestFonts.RequireSystemFont();
        var service = new RenderService();
        await service.Ready;
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RenderTextAsync(
            FaceKey.For(path, 0), "Too late", 24, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation));
    }
}
