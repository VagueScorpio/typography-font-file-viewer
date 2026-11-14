using System.IO;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using TypographyFontFileViewer.ViewModels;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// Phase 6 acceptance, the Glyphs half: a large CJK font lays out and scrolls, and the
/// grid's own behaviour — chunking, jumping, cell caching — holds up.
/// </summary>
public class GlyphMapViewModelTests
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static (GlyphMapViewModel Map, PreviewViewModel Preview, RenderService Render) Build()
    {
        var render = new RenderService();
        var preview = new PreviewViewModel(render);
        return (new GlyphMapViewModel(render, preview), preview, render);
    }

    [Fact]
    public async Task SelectingAFont_FillsTheGridWithItsCharacters()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            map.Columns = 10;
            map.Face = FaceKey.For(path, 0);
            await map.ReloadAsync();

            Assert.NotEmpty(map.Rows);
            Assert.All(map.Rows, row => Assert.InRange(row.Cells.Count, 1, 10));
            Assert.Contains("characters", map.Status, StringComparison.Ordinal);

            // Every cell in a mapped-only grid has a character behind it.
            Assert.All(map.Rows.SelectMany(row => row.Cells), cell => Assert.True(cell.IsMapped));
        }
    }

    [Fact]
    public async Task ChangingTheColumnCount_RelaysTheSameGlyphs()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            map.Columns = 10;
            map.Face = FaceKey.For(path, 0);
            await map.ReloadAsync();

            int total = map.Rows.Sum(row => row.Cells.Count);
            int rowsAtTen = map.Rows.Count;

            map.Columns = 20;

            Assert.Equal(total, map.Rows.Sum(row => row.Cells.Count));
            Assert.True(map.Rows.Count < rowsAtTen, "wider rows mean fewer of them");
        }
    }

    [Fact]
    public async Task ShowingUnmappedGlyphs_AddsThemAfterTheMappedOnes()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            map.Columns = 10;
            map.Face = FaceKey.For(path, 0);
            await map.ReloadAsync();
            int mappedOnly = map.Rows.Sum(row => row.Cells.Count);

            map.ShowUnmapped = true;
            await map.ReloadAsync();

            int everything = map.Rows.Sum(row => row.Cells.Count);
            Assert.True(everything >= mappedOnly);
            Assert.Contains("unmapped", map.Status, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("U+00E9", 0xE9)]
    [InlineData("u+00e9", 0xE9)]
    [InlineData("00E9", 0xE9)]
    [InlineData("e9", 0xE9)]
    [InlineData("41", 0x41)]
    [InlineData("  U+0041  ", 0x41)]
    public void TheJumpBox_UnderstandsCodePoints(string typed, int expected)
    {
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
            Assert.Equal(expected, map.ParseJumpTarget(typed));
    }

    [Fact]
    public void TheJumpBox_AlsoTakesTheCharacterItself()
    {
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            // A character that is not valid hex, so it can only be read as itself.
            Assert.Equal('\u00F1', map.ParseJumpTarget("\u00F1"));
            Assert.Equal('\u4E2D', map.ParseJumpTarget("\u4E2D"));
        }
    }

    [Fact]
    public void TheJumpBox_SaysSoWhenItCannotTell()
    {
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            Assert.Equal(-1, map.ParseJumpTarget(""));
            Assert.Equal(-1, map.ParseJumpTarget("   "));
            Assert.Equal(-1, map.ParseJumpTarget(null));
        }
    }

    [Fact]
    public async Task JumpingToACharacter_FindsItsRow()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            map.Columns = 10;
            map.Face = FaceKey.For(path, 0);
            await map.ReloadAsync();

            map.JumpTo = "U+0041";              // capital A
            int row = map.ResolveJumpRow();

            Assert.InRange(row, 0, map.Rows.Count - 1);
            Assert.Contains(map.Rows[row].Cells, cell => cell.Codepoint == 'A');
        }
    }

    [Fact]
    public async Task JumpingToSomethingTheFontLacks_SaysSoRatherThanScrolling()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            map.Columns = 10;
            map.Face = FaceKey.For(path, 0);
            await map.ReloadAsync();

            map.JumpTo = "U+4E2D";              // a CJK character Inter does not have
            Assert.Equal(-1, map.ResolveJumpRow());
            Assert.Contains("no U+4E2D", map.Status, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ACell_RendersAndIsThenServedFromTheCache()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            map.Columns = 10;
            map.Face = FaceKey.For(path, 0);
            await map.ReloadAsync();

            GlyphEntry glyph = map.Rows.SelectMany(row => row.Cells).First(c => c.Codepoint == 'A');

            var first = await map.RenderCellAsync(glyph, TestCancellation);
            Assert.NotNull(first);
            Assert.True(first!.IsFrozen);

            var second = await map.RenderCellAsync(glyph, TestCancellation);
            Assert.Same(first, second);
            Assert.True(map.Cache.Hits >= 1);
        }
    }

    [Fact]
    public async Task ManyCells_LeaveTheCacheAtItsOwnLimits()
    {
        string path = TestFonts.Require("NotoSans-Regular.ttf");
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            map.Columns = 20;
            map.Face = FaceKey.For(path, 0);
            await map.ReloadAsync();

            foreach (GlyphEntry glyph in map.Rows.SelectMany(row => row.Cells).Take(1200))
                await map.RenderCellAsync(glyph, TestCancellation);

            Assert.True(map.Cache.Count <= FontEngineLimits.GlyphCellCount);
            Assert.True(map.Cache.Bytes <= FontEngineLimits.GlyphCellBytes,
                $"cache held {map.Cache.Bytes / (1024 * 1024)} MB");
        }
    }

    [Fact]
    public async Task ALargeCjkFont_LaysOutWithoutStalling()
    {
        string? path = TestFonts.OptionalSystemFont("msyh.ttc");
        if (path is null) Assert.Skip("msyh.ttc is not installed.");

        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            map.Columns = 12;
            map.Face = FaceKey.For(path!, 0);

            var timer = System.Diagnostics.Stopwatch.StartNew();
            await map.ReloadAsync();
            timer.Stop();

            int glyphs = map.Rows.Sum(row => row.Cells.Count);
            Assert.True(glyphs > 20_000, $"expected a large glyph set, got {glyphs}");
            Assert.True(timer.ElapsedMilliseconds < 20_000,
                $"laying out {glyphs} glyphs took {timer.ElapsedMilliseconds} ms");

            // Only the rows exist up front; the cells inside them are rendered on demand.
            Assert.Equal(0, map.Cache.Count);
        }
    }

    [Fact]
    public async Task OpeningAGlyphsDetail_ReportsItsMeasurements()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            map.Columns = 10;
            map.Face = FaceKey.For(path, 0);
            await map.ReloadAsync();

            GlyphEntry glyph = map.Rows.SelectMany(row => row.Cells).First(c => c.Codepoint == 'H');
            await map.OpenDetailCommand.ExecuteAsync(glyph);

            Assert.True(map.IsDetailOpen);
            GlyphDetail detail = map.Detail!;

            Assert.Equal('H', detail.Codepoint);
            Assert.Equal("U+0048", detail.CodepointLabel);
            Assert.True(detail.UnitsPerEm > 0);
            Assert.True(detail.AdvanceWidth > 0, "a capital H has width");
            Assert.True(detail.Width > 0 && detail.Height > 0);
            Assert.False(detail.IsBlank);

            map.CloseDetailCommand.Execute(null);
            Assert.False(map.IsDetailOpen);
        }
    }

    [Fact]
    public async Task ASpace_IsReportedAsBlankRatherThanMissing()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            map.Columns = 10;
            map.Face = FaceKey.For(path, 0);
            await map.ReloadAsync();

            GlyphEntry space = map.Rows.SelectMany(row => row.Cells).First(c => c.Codepoint == ' ');
            await map.OpenDetailCommand.ExecuteAsync(space);

            GlyphDetail detail = map.Detail!;
            Assert.True(detail.IsBlank);
            Assert.True(detail.AdvanceWidth > 0, "a space takes up room even with no outline");
        }
    }

    [Fact]
    public void WithNoFontSelected_TheGridIsEmptyRatherThanBroken()
    {
        (GlyphMapViewModel map, PreviewViewModel preview, RenderService render) = Build();

        using (render)
        using (preview)
        using (map)
        {
            Assert.Empty(map.Rows);
            Assert.Equal(-1, map.FindRowFor('A'));
        }
    }
}

/// <summary>
/// Phase 6 acceptance, the Info half: every field the font supplies is shown, and
/// nothing it does not.
/// </summary>
public class InfoViewModelTests
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheInfoPanel_ShowsWhatTheFontSaysAboutItself()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");

        using var render = new RenderService();
        var info = new InfoViewModel(render, new HashService()) { Face = FaceKey.For(path, 0) };

        await info.ReloadAsync();

        Assert.NotEmpty(info.Rows);
        Assert.Null(info.Error);

        string[] labels = [.. info.Rows.Select(row => row.Label)];
        Assert.Contains("Family", labels);
        Assert.Contains("Format", labels);
        Assert.Contains("Glyphs", labels);
        Assert.Contains("Units per em", labels);
        Assert.Contains("Embedding", labels);
        Assert.Contains("File", labels);
        Assert.Contains("File size", labels);
        Assert.Contains("Modified", labels);

        // Nothing is shown blank: a row exists only when there is something in it.
        Assert.All(info.Rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Value)));

        await Task.CompletedTask;
    }

    [Fact]
    public void AVariableFont_ListsItsAxesAsTheirOwnRows()
    {
        var info = new FontInfo
        {
            Path = @"C:\fonts\Variable.ttf",
            FaceIndex = 0,
            Container = FontFormat.TrueType,
            OutlineFormat = "TrueType",
            Family = "Variable",
            GlyphCount = 100,
            UnitsPerEm = 1000,
            VariationAxes =
            [
                new VariationAxis("wght", "Weight", 100, 400, 900),
                new VariationAxis("opsz", "Optical size", 14, 14, 32),
            ],
            NamedInstances = ["Regular", "Bold"],
        };

        IReadOnlyList<InfoRow> rows = InfoViewModel.Build(info);

        Assert.Contains(rows, row => row.Label == "Variable" && row.Value == "2 axes");
        Assert.Contains(rows, row => row.Label.Trim() == "wght" && row.Value.Contains("Weight"));
        Assert.Contains(rows, row => row.Label.Trim() == "opsz");
        Assert.Contains(rows, row => row.Label == "Named instances");
    }

    [Fact]
    public void AWebFont_SaysBothWhatItIsAndWhatIsInside()
    {
        var info = new FontInfo
        {
            Path = @"C:\fonts\Thing.woff2",
            FaceIndex = 0,
            Container = FontFormat.Woff2,
            OutlineFormat = "CFF",
            Family = "Thing",
        };

        IReadOnlyList<InfoRow> rows = InfoViewModel.Build(info);

        InfoRow format = rows.Single(row => row.Label == "Format");
        Assert.Equal("WOFF2 containing CFF", format.Value);
    }

    [Fact]
    public void UrlsAreMarkedAsLinks_AndNothingElseIs()
    {
        var info = new FontInfo
        {
            Path = @"C:\fonts\Thing.ttf",
            FaceIndex = 0,
            Container = FontFormat.TrueType,
            Family = "Thing",
            VendorUrl = "https://example.com",
            LicenseUrl = "https://example.com/licence",
            Copyright = "Copyright somebody",
        };

        IReadOnlyList<InfoRow> rows = InfoViewModel.Build(info);

        Assert.True(rows.Single(row => row.Label == "Vendor URL").IsLink);
        Assert.True(rows.Single(row => row.Label == "Licence URL").IsLink);
        Assert.False(rows.Single(row => row.Label == "Copyright").IsLink);
    }

    [Fact]
    public void FieldsTheFontDoesNotSupply_AreLeftOutEntirely()
    {
        var bare = new FontInfo
        {
            Path = @"C:\fonts\Bare.ttf",
            FaceIndex = 0,
            Container = FontFormat.TrueType,
            OutlineFormat = "TrueType",
        };

        IReadOnlyList<InfoRow> rows = InfoViewModel.Build(bare);

        Assert.DoesNotContain(rows, row => row.Label == "Designer");
        Assert.DoesNotContain(rows, row => row.Label == "Copyright");
        Assert.DoesNotContain(rows, row => row.Label == "SHA-256");

        // What it does know is still there.
        Assert.Contains(rows, row => row.Label == "Format");
    }

    [Fact]
    public void TheHashRow_AppearsOnlyOnceItHasBeenComputed()
    {
        var info = new FontInfo
        {
            Path = @"C:\fonts\Thing.ttf",
            FaceIndex = 0,
            Container = FontFormat.TrueType,
            Family = "Thing",
        };

        Assert.DoesNotContain(InfoViewModel.Build(info), row => row.Label == "SHA-256");
        Assert.Contains(InfoViewModel.Build(info, "abc123"), row => row.Label == "SHA-256");
    }

    [Fact]
    public async Task HashingIsOnRequest_NotOnSelection()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");

        using var render = new RenderService();
        var info = new InfoViewModel(render, new HashService()) { Face = FaceKey.For(path, 0) };
        await info.ReloadAsync();

        // A 30 MB font should not be hashed just because it was selected.
        Assert.True(info.CanComputeHash);
        Assert.DoesNotContain(info.Rows, row => row.Label == "SHA-256");

        await info.ComputeHashCommand.ExecuteAsync(null);

        Assert.False(info.CanComputeHash);
        Assert.Contains(info.Rows, row => row.Label == "SHA-256");
        Assert.Matches("^[0-9a-f]{64}$", info.Sha256!);
    }

    [Fact]
    public async Task ABrokenFont_ReportsItsErrorRatherThanAnEmptyPanel()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tffv-info-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);

        try
        {
            using var render = new RenderService();
            var info = new InfoViewModel(render, new HashService())
            {
                Face = new FaceKey(path, 0, 8, File.GetLastWriteTimeUtc(path)),
            };

            await info.ReloadAsync();

            Assert.Empty(info.Rows);
            Assert.False(string.IsNullOrWhiteSpace(info.Error));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ACollectionFace_SaysWhichFaceItIs()
    {
        string path = TestFonts.Require("TestCollection.ttc");

        using var render = new RenderService();
        var info = new InfoViewModel(render, new HashService()) { Face = FaceKey.For(path, 1) };
        await info.ReloadAsync();

        Assert.Contains(info.Rows, row => row.Label == "Face" && row.Value == "2 of 2");
    }
}
