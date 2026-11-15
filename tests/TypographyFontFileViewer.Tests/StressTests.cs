using System.Diagnostics;
using System.IO;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using TypographyFontFileViewer.ViewModels;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// The blueprint's Section 13.3 stress list, as far as it can be automated.
/// </summary>
/// <remarks>
/// <para>
/// Phase 8 asks for no crash, hang or file lock in any of these. The cases here are the
/// ones a test can set up honestly; the ones it cannot — a network share, a USB drive
/// pulled mid-scan, 200% scaling across two monitors with different DPI — are listed in
/// CHANGELOG.txt as needing a person.
/// </para>
/// <para>
/// These are slower than the rest of the suite because that is the point: they build
/// real folders of real fonts and work them hard.
/// </para>
/// </remarks>
public class StressTests : TempFolderTest
{
    public StressTests() : base("stress") { }

    private static byte[] RealFont(string name = "Inter-Regular.ttf")
    {
        string path = Path.Combine(TestFonts.RequireCorpus(), name);
        if (!File.Exists(path)) Assert.Skip($"{name} is not in the corpus.");
        return File.ReadAllBytes(path);
    }

    // --- A big folder with rubbish in it ---------------------------------------

    /// <summary>
    /// Two thousand fonts, fifty of them broken or empty and named as if they were not.
    /// </summary>
    [Fact]
    public async Task AFolderOfTwoThousandFonts_WithFiftyBadOnes_ListsAndSurvives()
    {
        string folder = Folder("big");
        byte[] good = RealFont();

        for (int i = 0; i < 1950; i++)
            File.WriteAllBytes(Path.Combine(folder, $"Font{i:D4}.ttf"), good);

        var random = new Random(8);
        for (int i = 0; i < 25; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"zero{i:D2}.ttf"), []);

            byte[] junk = new byte[random.Next(64, 8192)];
            random.NextBytes(junk);
            File.WriteAllBytes(Path.Combine(folder, $"junk{i:D2}.woff2"), junk);
        }

        using var render = new RenderService();
        using var model = new MainViewModel(render);

        var timer = Stopwatch.StartNew();
        await model.OpenPathAsync(folder, TestCancellation);
        timer.Stop();

        Assert.Equal(2000, model.Fonts.Count);
        Assert.True(timer.ElapsedMilliseconds < 10_000,
            $"listing 2,000 fonts took {timer.ElapsedMilliseconds} ms");

        await model.MetadataScanCompleted;

        // The bad files are marked, and the good ones beside them are unaffected.
        Assert.Equal(50, model.BrokenCount);
        Assert.Equal(1950, model.Fonts.Count(font => !font.IsBroken));

        // And the engine is still serving requests afterwards.
        FontItemViewModel working = model.Fonts.First(font => !font.IsBroken);
        model.SelectedFont = working;

        RenderedText rendered = await render.RenderTextAsync(
            working.Key, "Still here", 24, 96, 0, RenderStyle.Light,
            cancellationToken: TestCancellation);

        Assert.True(rendered.Width > 0);
    }

    [Fact]
    public async Task TheGalleryOverTwoThousandRows_KeepsItsCachesBounded()
    {
        string folder = Folder("gallery");
        byte[] good = RealFont();

        for (int i = 0; i < 2000; i++)
            File.WriteAllBytes(Path.Combine(folder, $"Font{i:D4}.ttf"), good);

        using var render = new RenderService();
        using var model = new MainViewModel(render);

        await model.OpenPathAsync(folder, TestCancellation);

        // Stand in for scrolling the whole way through.
        foreach (FontItemViewModel font in model.Fonts)
            await model.Gallery.RenderRowAsync(font.Key, TestCancellation);

        Assert.True(model.Gallery.Cache.Count <= FontEngineLimits.GalleryRowCount);
        Assert.True(render.CacheStatistics.Bytes <= FontEngineLimits.FaceCacheBytes,
            $"the face cache held {render.CacheStatistics.Bytes / (1024 * 1024)} MB");
    }

    // --- The displayed font changing underneath -------------------------------

    [Fact]
    public async Task DeletingTheDisplayedFont_LeavesNoLockAndRecoversOnRescan()
    {
        string folder = Folder("deleted");
        byte[] good = RealFont();

        File.WriteAllBytes(Path.Combine(folder, "Keep.ttf"), good);
        string doomed = Path.Combine(folder, "Doomed.ttf");
        File.WriteAllBytes(doomed, good);

        using var render = new RenderService();
        using var model = new MainViewModel(render);

        await model.OpenPathAsync(doomed, TestCancellation);
        Assert.Equal("Doomed.ttf", model.SelectedFont!.File.FileName);

        // Render it so the engine really has it open.
        await render.RenderTextAsync(
            model.SelectedFont.Key, "On screen", 32, 96, 0, RenderStyle.Light,
            cancellationToken: TestCancellation);

        // The user deletes it in Explorer while it is on screen.
        File.Delete(doomed);
        Assert.False(File.Exists(doomed));

        await model.RescanAsync(TestCancellation);

        Assert.Single(model.Fonts);
        Assert.Equal("Keep.ttf", model.Fonts[0].File.FileName);
        Assert.NotNull(model.SelectedFont);
    }

    [Fact]
    public async Task RenamingTheDisplayedFont_IsPickedUpByRescan()
    {
        string folder = Folder("renamed");
        byte[] good = RealFont();

        string before = Path.Combine(folder, "Before.ttf");
        File.WriteAllBytes(before, good);

        using var render = new RenderService();
        using var model = new MainViewModel(render);

        await model.OpenPathAsync(before, TestCancellation);
        await render.RenderTextAsync(
            model.SelectedFont!.Key, "Renaming", 32, 96, 0, RenderStyle.Light,
            cancellationToken: TestCancellation);

        File.Move(before, Path.Combine(folder, "After.ttf"));
        await model.RescanAsync(TestCancellation);

        Assert.Equal("After.ttf", Assert.Single(model.Fonts).File.FileName);
    }

    /// <summary>
    /// Overwriting the displayed font with a different one shows the new font, not the
    /// old one remembered.
    /// </summary>
    /// <remarks>
    /// This is what the face cache keying on length and last-write time is for. Serving
    /// the previous font's pixels for a file that has changed would be the subtle kind
    /// of wrong that nobody notices until they are confused by it.
    /// </remarks>
    [Fact]
    public async Task OverwritingTheDisplayedFont_ShowsTheNewOne()
    {
        string folder = Folder("overwritten");
        string path = Path.Combine(folder, "Changing.ttf");

        File.WriteAllBytes(path, RealFont("Inter-Regular.ttf"));

        using var render = new RenderService();
        using var model = new MainViewModel(render);

        await model.OpenPathAsync(path, TestCancellation);
        await model.MetadataScanCompleted;
        Assert.Contains("Inter", model.SelectedFont!.Family, StringComparison.OrdinalIgnoreCase);

        RenderedText before = await render.RenderTextAsync(
            model.SelectedFont.Key, "Compare", 32, 96, 0, RenderStyle.Light,
            cancellationToken: TestCancellation);

        // A completely different font, same name.
        await Task.Delay(1100, TestCancellation);   // so the write time really differs
        File.WriteAllBytes(path, RealFont("SourceSerif4-Regular.otf"));

        await model.RescanAsync(TestCancellation);
        await model.MetadataScanCompleted;

        Assert.Contains("Source Serif", model.SelectedFont!.Family, StringComparison.OrdinalIgnoreCase);

        RenderedText after = await render.RenderTextAsync(
            model.SelectedFont.Key, "Compare", 32, 96, 0, RenderStyle.Light,
            cancellationToken: TestCancellation);

        Assert.False(before.Bgra.AsSpan().SequenceEqual(after.Bgra),
            "the preview is still showing the font that used to be at this path");
    }

    [Fact]
    public async Task AFontIsNeverLocked_EvenWithEveryPanelLookingAtIt()
    {
        string folder = Folder("unlocked");
        string path = Path.Combine(folder, "Busy.ttf");
        File.WriteAllBytes(path, RealFont());

        using var render = new RenderService();
        using var model = new MainViewModel(render);

        await model.OpenPathAsync(path, TestCancellation);
        var key = model.SelectedFont!.Key;

        // Preview, gallery row, glyph list, glyph cell and metadata, all at once.
        await render.RenderTextAsync(key, "Busy", 48, 96, 0, RenderStyle.Light,
            cancellationToken: TestCancellation);
        await model.Gallery.RenderRowAsync(key, TestCancellation);
        IReadOnlyList<GlyphEntry> glyphs = await render.ListGlyphsAsync(
            key, cancellationToken: TestCancellation);
        await render.RenderGlyphCellsAsync(key, [glyphs[0]], 48, RenderStyle.Light,
            cancellationToken: TestCancellation);
        await render.ReadInfoAsync(key, cancellationToken: TestCancellation);

        // Nothing holds a handle, so the file can simply go.
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    // --- A held-down navigation key --------------------------------------------

    /// <summary>
    /// Ctrl+Down held down: the queue must not grow without limit.
    /// </summary>
    /// <remarks>
    /// Each keypress supersedes the last, and a superseded request is dropped when it
    /// reaches the front rather than being rendered and thrown away. The queue therefore
    /// stays near the number of requests in flight instead of tracking how long the key
    /// was held.
    /// </remarks>
    [Fact]
    public async Task HoldingTheNavigationKey_DoesNotLetTheQueueGrowWithoutLimit()
    {
        string folder = Folder("holding");
        byte[] good = RealFont();

        for (int i = 0; i < 300; i++)
            File.WriteAllBytes(Path.Combine(folder, $"Font{i:D3}.ttf"), good);

        using var render = new RenderService();
        using var model = new MainViewModel(render);

        await model.OpenPathAsync(folder, TestCancellation);
        model.Preview.Dpi = 96;

        int worst = 0;
        var timer = Stopwatch.StartNew();

        // Roughly what a held key produces: a selection change every few milliseconds.
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            model.NextFontCommand.Execute(null);
            if (model.SelectedFont == model.VisibleFonts[^1]) model.FirstFontCommand.Execute(null);

            worst = Math.Max(worst, render.PendingJobCount);
            await Task.Delay(4, TestCancellation);
        }

        Assert.True(worst < 400,
            $"the queue reached {worst} jobs while a navigation key was held");

        // It drains once the key is let go.
        for (int i = 0; i < 100 && render.PendingJobCount > 0; i++)
            await Task.Delay(50, TestCancellation);

        Assert.True(render.PendingJobCount <= 2,
            $"{render.PendingJobCount} jobs were still queued after the key was released");

        // And the app is still usable.
        Assert.NotNull(model.SelectedFont);
    }

    // --- Awkward names and paths -----------------------------------------------

    [Fact]
    public async Task UnicodeFileNames_AreListedAndRendered()
    {
        string folder = Folder("unicode");
        byte[] good = RealFont();

        string[] names =
        [
            "中文字体.ttf",             // Chinese
            "شعار.ttf",             // Arabic
            "Шрифт.ttf",       // Cyrillic
            "emoji\U0001F600font.ttf",                  // outside the BMP
            "accentué.ttf",
        ];

        foreach (string name in names)
            File.WriteAllBytes(Path.Combine(folder, name), good);

        using var render = new RenderService();
        using var model = new MainViewModel(render);

        await model.OpenPathAsync(folder, TestCancellation);
        await model.MetadataScanCompleted;

        Assert.Equal(names.Length, model.Fonts.Count);
        Assert.All(model.Fonts, font => Assert.False(font.IsBroken, font.Error));

        foreach (FontItemViewModel font in model.Fonts)
        {
            RenderedText rendered = await render.RenderTextAsync(
                font.Key, "Name test", 20, 96, 0, RenderStyle.Light,
                cancellationToken: TestCancellation);
            Assert.True(rendered.Width > 0);
        }
    }

    /// <summary>
    /// A path past the old 260-character limit.
    /// </summary>
    /// <remarks>
    /// The manifest declares longPathAware, and every path in the app goes through .NET
    /// rather than being hand-assembled, so this should simply work. Worth proving,
    /// since deep font collections are common.
    /// </remarks>
    [Fact]
    public async Task AVeryLongPath_IsHandled()
    {
        string deep = Root;
        for (int i = 0; i < 12; i++)
            deep = Path.Combine(deep, new string((char)('a' + i), 24));

        try
        {
            Directory.CreateDirectory(deep);
        }
        catch (Exception exception) when (exception is IOException or PathTooLongException)
        {
            Assert.Skip("Long paths are not enabled on this machine.");
        }

        Assert.True(deep.Length > 260, $"the path is only {deep.Length} characters");

        File.WriteAllBytes(Path.Combine(deep, "Deep.ttf"), RealFont());

        using var render = new RenderService();
        using var model = new MainViewModel(render);

        await model.OpenPathAsync(deep, TestCancellation);
        await model.MetadataScanCompleted;

        FontItemViewModel font = Assert.Single(model.Fonts);
        Assert.False(font.IsBroken, font.Error);

        RenderedText rendered = await render.RenderTextAsync(
            font.Key, "Deep", 24, 96, 0, RenderStyle.Light, cancellationToken: TestCancellation);
        Assert.True(rendered.Width > 0);
    }

    [Fact]
    public async Task AFolderThatVanishesMidUse_DoesNotTakeTheAppWithIt()
    {
        string folder = Folder("vanishing");
        byte[] good = RealFont();

        for (int i = 0; i < 20; i++)
            File.WriteAllBytes(Path.Combine(folder, $"Font{i:D2}.ttf"), good);

        using var render = new RenderService();
        using var model = new MainViewModel(render);

        await model.OpenPathAsync(folder, TestCancellation);
        Assert.Equal(20, model.Fonts.Count);

        // Stands in for a USB drive being pulled.
        Directory.Delete(folder, recursive: true);

        await model.RescanAsync(TestCancellation);

        Assert.Empty(model.Fonts);
        Assert.Null(model.SelectedFont);

        // Opening something else still works.
        string other = Folder("still-here");
        File.WriteAllBytes(Path.Combine(other, "Fine.ttf"), good);
        await model.OpenPathAsync(other, TestCancellation);

        Assert.Single(model.Fonts);
    }

    // --- Everything at once -----------------------------------------------------

    [Fact]
    public async Task ALargeCjkFontInEveryPanel_Works()
    {
        string? path = TestFonts.OptionalSystemFont("msyh.ttc");
        if (path is null) Assert.Skip("msyh.ttc is not installed.");

        using var render = new RenderService();
        using var preview = new PreviewViewModel(render);
        using var map = new GlyphMapViewModel(render, preview);

        var key = FaceKey.For(path!, 0);

        RenderedText rendered = await render.RenderTextAsync(
            key, "中文測試", 48, 96, 0, RenderStyle.Light,
            cancellationToken: TestCancellation);
        Assert.True(rendered.Width > 0);

        FontInfo info = await render.ReadInfoAsync(key, cancellationToken: TestCancellation);
        Assert.True(info.GlyphCount > 20_000);

        map.Columns = 12;
        map.Face = key;
        await map.ReloadAsync();
        Assert.True(map.Rows.Count > 1000);

        GlyphDetail detail = await render.ReadGlyphDetailAsync(
            key, map.Rows[500].Cells[0], cancellationToken: TestCancellation);
        Assert.True(detail.UnitsPerEm > 0);
    }

    [Fact]
    public async Task EveryFormatInTheCorpus_SurvivesEveryPanel()
    {
        IReadOnlyList<string> fonts = TestFonts.All();
        if (fonts.Count == 0) TestFonts.RequireCorpus();

        using var render = new RenderService();
        using var preview = new PreviewViewModel(render);
        using var map = new GlyphMapViewModel(render, preview);

        var failures = new List<string>();

        foreach (string path in fonts)
        {
            var key = FaceKey.For(path, 0);
            string name = Path.GetFileName(path);

            try
            {
                await render.RenderTextAsync(key, "Panels", 18, 96, 0, RenderStyle.Light,
                    cancellationToken: TestCancellation);
                await render.ReadInfoAsync(key, cancellationToken: TestCancellation);

                IReadOnlyList<GlyphEntry> glyphs = await render.ListGlyphsAsync(
                    key, cancellationToken: TestCancellation);

                if (glyphs.Count > 0)
                {
                    await render.RenderGlyphCellsAsync(key, [glyphs[0]], 48, RenderStyle.Light,
                        cancellationToken: TestCancellation);
                    await render.ReadGlyphDetailAsync(key, glyphs[0],
                        cancellationToken: TestCancellation);
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
