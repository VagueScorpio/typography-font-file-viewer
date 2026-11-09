using System.IO;
using TypographyFontFileViewer.Services;
using TypographyFontFileViewer.ViewModels;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// Phase 3 acceptance: opening one font loads its whole folder with that font selected,
/// and the navigation commands walk the list from end to end.
/// </summary>
public class MainViewModelTests : IDisposable
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), $"tffv-vm-{Guid.NewGuid():N}");

    public MainViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Copies a few real fonts into a folder of this test's own.</summary>
    private List<string> SeedFonts(params string[] names)
    {
        string corpus = TestFonts.RequireCorpus();
        var copied = new List<string>();

        foreach (string name in names)
        {
            string source = Path.Combine(corpus, name);
            if (!File.Exists(source)) Assert.Skip($"{name} is not in the corpus.");

            string target = Path.Combine(_folder, name);
            File.Copy(source, target, overwrite: true);
            copied.Add(target);
        }

        return copied;
    }

    [Fact]
    public async Task OpeningOneFont_LoadsTheWholeFolderAndSelectsThatFont()
    {
        List<string> fonts = SeedFonts(
            "Inter-Regular.ttf", "Inter-Bold.ttf", "NotoSans-Regular.ttf", "SourceSerif4-Regular.otf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(fonts[1], TestCancellation);   // open Inter-Bold

        Assert.Equal(4, model.Fonts.Count);
        Assert.Equal(_folder, model.CurrentFolder);
        Assert.NotNull(model.SelectedFont);
        Assert.Equal("Inter-Bold.ttf", model.SelectedFont!.File.FileName);

        // The preview is pointed at the selected face straight away.
        Assert.NotNull(model.Preview.Face);
        Assert.Equal(fonts[1], model.Preview.Face!.Value.Path);
    }

    [Fact]
    public async Task OpeningAFolder_SelectsTheFirstFont()
    {
        SeedFonts("Inter-Regular.ttf", "Inter-Bold.ttf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);

        Assert.Equal(2, model.Fonts.Count);
        Assert.Equal("Inter-Bold.ttf", model.SelectedFont!.File.FileName);   // natural order
    }

    [Fact]
    public async Task TheMetadataPass_ReplacesFileNamesWithFamilyNames()
    {
        SeedFonts("Inter-Regular.ttf", "SourceSerif4-Regular.otf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);

        // Before the pass, a row knows only what the directory entry said.
        Assert.All(model.Fonts, font => Assert.EndsWith(
            Path.GetExtension(font.File.FileName), font.File.FileName, StringComparison.Ordinal));

        await model.MetadataScanCompleted;

        Assert.All(model.Fonts, font =>
        {
            Assert.True(font.MetadataLoaded, $"{font.File.FileName} never loaded");
            Assert.False(font.IsBroken, font.Error);
            Assert.False(string.IsNullOrWhiteSpace(font.Family));
        });

        Assert.Contains(model.Fonts, font => font.DisplayName.Contains("Inter", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(model.Fonts, font => font.DisplayName.Contains("Source Serif", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ACollection_BecomesOneRowPerFace()
    {
        SeedFonts("TestCollection.ttc");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);
        Assert.Single(model.Fonts);          // one file, so one row to begin with

        await model.MetadataScanCompleted;

        // How many faces a file holds is not knowable until it has been opened, so the
        // extra rows appear with the metadata.
        Assert.Equal(2, model.Fonts.Count);
        Assert.Equal(0, model.Fonts[0].FaceIndex);
        Assert.Equal(1, model.Fonts[1].FaceIndex);
        Assert.Equal("(1/2)", model.Fonts[0].FaceLabel);
        Assert.Equal("(2/2)", model.Fonts[1].FaceLabel);
        Assert.NotEqual(model.Fonts[0].DisplayName, model.Fonts[1].DisplayName);
    }

    [Fact]
    public async Task ABrokenFont_GetsARowWithItsError()
    {
        SeedFonts("Inter-Regular.ttf");
        File.WriteAllBytes(Path.Combine(_folder, "Broken.ttf"), [1, 2, 3, 4, 5, 6, 7, 8]);

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);
        await model.MetadataScanCompleted;

        FontItemViewModel broken = model.Fonts.Single(font => font.File.FileName == "Broken.ttf");

        Assert.True(broken.IsBroken);
        Assert.False(string.IsNullOrWhiteSpace(broken.Error));

        // The row still shows something, and the good font beside it is unaffected.
        Assert.Equal("Broken.ttf", broken.DisplayName);
        Assert.Equal(1, model.BrokenCount);
        Assert.False(model.Fonts.Single(font => font.File.FileName == "Inter-Regular.ttf").IsBroken);
    }

    [Fact]
    public async Task SelectingABrokenFont_ClearsThePreviewInsteadOfFailing()
    {
        File.WriteAllBytes(Path.Combine(_folder, "Broken.ttf"), [9, 9, 9, 9]);

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);
        await model.MetadataScanCompleted;

        model.SelectedFont = model.Fonts[0];

        Assert.Null(model.Preview.Face);
        Assert.False(string.IsNullOrWhiteSpace(model.Preview.ErrorMessage));
    }

    // --- Navigation ------------------------------------------------------------

    [Fact]
    public async Task NextAndPrevious_WalkTheWholeList()
    {
        SeedFonts("Inter-Regular.ttf", "Inter-Bold.ttf", "NotoSans-Regular.ttf",
                  "NotoSans-Bold.ttf", "SourceSerif4-Regular.otf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);
        int count = model.Fonts.Count;
        Assert.Equal(5, count);

        // Walk to the end.
        var visited = new List<string> { model.SelectedFont!.File.FileName };
        for (int i = 1; i < count; i++)
        {
            model.NextFontCommand.Execute(null);
            visited.Add(model.SelectedFont!.File.FileName);
        }

        Assert.Equal(count, visited.Distinct().Count());
        Assert.Equal(model.Fonts[^1], model.SelectedFont);

        // And back again.
        for (int i = 1; i < count; i++) model.PreviousFontCommand.Execute(null);
        Assert.Equal(model.Fonts[0], model.SelectedFont);
    }

    [Fact]
    public async Task Navigation_StopsAtTheEndsRatherThanWrapping()
    {
        SeedFonts("Inter-Regular.ttf", "Inter-Bold.ttf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);

        model.PreviousFontCommand.Execute(null);
        Assert.Equal(model.Fonts[0], model.SelectedFont);

        for (int i = 0; i < 5; i++) model.NextFontCommand.Execute(null);
        Assert.Equal(model.Fonts[^1], model.SelectedFont);

        model.LastFontCommand.Execute(null);
        Assert.Equal(model.Fonts[^1], model.SelectedFont);

        model.FirstFontCommand.Execute(null);
        Assert.Equal(model.Fonts[0], model.SelectedFont);
    }

    [Fact]
    public async Task Navigation_OnAnEmptyFolder_DoesNothing()
    {
        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);

        model.NextFontCommand.Execute(null);
        model.PreviousFontCommand.Execute(null);

        Assert.Empty(model.Fonts);
        Assert.Null(model.SelectedFont);
        Assert.False(string.IsNullOrWhiteSpace(model.Message));
    }

    [Fact]
    public async Task TheStatusBar_SaysWhereYouAreInTheFolder()
    {
        SeedFonts("Inter-Regular.ttf", "Inter-Bold.ttf", "NotoSans-Regular.ttf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);
        await model.MetadataScanCompleted;

        Assert.Equal("Font 1 of 3", model.SelectedPosition);
        Assert.Contains("3 fonts", model.FolderSummary, StringComparison.Ordinal);

        model.NextFontCommand.Execute(null);
        Assert.Equal("Font 2 of 3", model.SelectedPosition);

        Assert.Contains("glyphs", model.StatusText, StringComparison.Ordinal);
        Assert.Contains(model.SelectedFont!.File.FileName, model.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningSomethingThatIsNotThere_SaysSoWithoutThrowing()
    {
        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(Path.Combine(_folder, "nowhere.ttf"), TestCancellation);

        Assert.Empty(model.Fonts);
        Assert.False(string.IsNullOrWhiteSpace(model.Message));
    }

    [Fact]
    public async Task Rescan_PicksUpAFontAddedAfterTheFolderWasOpened()
    {
        SeedFonts("Inter-Regular.ttf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);
        Assert.Single(model.Fonts);

        SeedFonts("NotoSans-Regular.ttf");
        await model.RescanAsync(TestCancellation);

        Assert.Equal(2, model.Fonts.Count);
        Assert.NotNull(model.SelectedFont);
    }

    [Fact]
    public async Task Rescan_KeepsTheSelectionWhenTheFontIsStillThere()
    {
        SeedFonts("Inter-Regular.ttf", "NotoSans-Regular.ttf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);
        model.LastFontCommand.Execute(null);
        string selected = model.SelectedFont!.File.FileName;

        await model.RescanAsync(TestCancellation);

        Assert.Equal(selected, model.SelectedFont!.File.FileName);
    }

    // --- Preview ---------------------------------------------------------------

    [Fact]
    public async Task ThePreview_RendersTheSelectedFont()
    {
        SeedFonts("Inter-Regular.ttf", "Inter-Bold.ttf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);

        model.Preview.PreviewText = "Rendering";
        model.Preview.PointSize = 36;

        await WaitForPreviewAsync(model.Preview);

        Assert.NotNull(model.Preview.MainImage);
        Assert.True(model.Preview.MainImage!.PixelWidth > 0);
        Assert.Null(model.Preview.ErrorMessage);
    }

    [Fact]
    public async Task ABitmapFont_ReportsTheSizesItActuallyHas()
    {
        SeedFonts("spleen-8x16.pcf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);
        await model.MetadataScanCompleted;

        // Give the fixed-size lookup a moment; it is fired off with the selection.
        for (int i = 0; i < 50 && model.Preview.FixedSizes.Count == 0; i++)
            await Task.Delay(20, TestCancellation);

        Assert.True(model.Preview.HasFixedSizes);
        Assert.Contains("Bitmap font", model.Preview.FixedSizeNotice, StringComparison.Ordinal);
    }

    /// <summary>Waits for the debounced render to produce something.</summary>
    private static async Task WaitForPreviewAsync(PreviewViewModel preview)
    {
        for (int i = 0; i < 100; i++)
        {
            if (preview.MainImage is not null || preview.ErrorMessage is not null) return;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
