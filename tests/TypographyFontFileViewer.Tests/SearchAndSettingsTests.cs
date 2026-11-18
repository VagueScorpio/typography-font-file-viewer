using System.Diagnostics;
using System.IO;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using TypographyFontFileViewer.ViewModels;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// Phase 7 acceptance: every setting survives a restart, and search on a thousand fonts
/// is instant.
/// </summary>
public class SearchAndSortTests : TempFolderTest
{
    private readonly string _fonts;

    public SearchAndSortTests() : base("search") => _fonts = Folder("fonts");

    private void Seed(params string[] names) => SeedCorpusFonts(_fonts, names);

    private static (MainViewModel Model, RenderService Render) Build()
    {
        var render = new RenderService();
        return (new MainViewModel(render), render);
    }

    [Fact]
    public async Task Searching_NarrowsBothListsToTheMatches()
    {
        Seed("Inter-Regular.ttf", "Inter-Bold.ttf", "NotoSans-Regular.ttf", "SourceSerif4-Regular.otf");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            Assert.Equal(4, model.VisibleFonts.Count);

            model.SearchText = "inter";

            Assert.Equal(2, model.VisibleFonts.Count);
            Assert.All(model.VisibleFonts, font =>
                Assert.Contains("Inter", font.File.FileName, StringComparison.OrdinalIgnoreCase));

            // The folder itself is unchanged; only what is shown has narrowed.
            Assert.Equal(4, model.Fonts.Count);
            Assert.True(model.IsFiltered);
            Assert.Contains("2 of 4", model.FolderSummary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Searching_MatchesTheFamilyNameTooOnceItIsKnown()
    {
        Seed("SourceSerif4-Regular.otf", "Inter-Regular.ttf");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            await model.MetadataScanCompleted;

            // "Source Serif" is the family; the file is called SourceSerif4-Regular.otf.
            model.SearchText = "Source Serif";

            FontItemViewModel match = Assert.Single(model.VisibleFonts);
            Assert.Contains("Source Serif", match.Family, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ClearingTheSearch_BringsEverythingBack()
    {
        Seed("Inter-Regular.ttf", "NotoSans-Regular.ttf");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);

            model.SearchText = "inter";
            Assert.Single(model.VisibleFonts);

            model.ClearSearchCommand.Execute(null);

            Assert.Equal(2, model.VisibleFonts.Count);
            Assert.False(model.IsFiltered);
        }
    }

    [Fact]
    public async Task ASearchThatMatchesNothing_LeavesAnEmptyListRatherThanBreaking()
    {
        Seed("Inter-Regular.ttf", "NotoSans-Regular.ttf");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            model.SearchText = "nothing matches this";

            Assert.Empty(model.VisibleFonts);
            Assert.Null(model.SelectedFont);
            Assert.Equal(string.Empty, model.SelectedPosition);

            // And navigating does nothing rather than throwing.
            model.NextFontCommand.Execute(null);
            Assert.Null(model.SelectedFont);
        }
    }

    [Fact]
    public async Task NavigationWalksTheMatches_NotTheWholeFolder()
    {
        Seed("Inter-Regular.ttf", "Inter-Bold.ttf", "NotoSans-Regular.ttf", "NotoSans-Bold.ttf");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            model.SearchText = "noto";

            Assert.Equal(2, model.VisibleFonts.Count);
            model.FirstFontCommand.Execute(null);

            var walked = new List<FontItemViewModel> { model.SelectedFont! };
            model.NextFontCommand.Execute(null);
            walked.Add(model.SelectedFont!);

            model.NextFontCommand.Execute(null);   // already at the end

            Assert.Equal(2, walked.Distinct().Count());
            Assert.All(walked, font =>
                Assert.Contains("Noto", font.File.FileName, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Font 2 of 2", model.SelectedPosition);
        }
    }

    [Fact]
    public async Task HidingTheSelectedFont_MovesTheSelectionSomewhereVisible()
    {
        Seed("Inter-Regular.ttf", "NotoSans-Regular.ttf");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);

            model.SelectedFont = model.Fonts.First(f => f.File.FileName.StartsWith("Noto"));
            model.SearchText = "inter";

            Assert.NotNull(model.SelectedFont);
            Assert.Contains(model.SelectedFont!, model.VisibleFonts);
        }
    }

    /// <summary>
    /// A search that still shows the selected font leaves it selected.
    /// </summary>
    /// <remarks>
    /// Worth its own test: refreshing the filter resets the bound lists, and a list
    /// clearing its own selection on reset writes null back into the view model. Without
    /// care that loses the selection on every keystroke, blanking the preview while the
    /// font is still right there in the list.
    /// </remarks>
    [Fact]
    public async Task ASearchThatStillShowsTheSelectedFont_KeepsItSelected()
    {
        Seed("Inter-Regular.ttf", "Inter-Bold.ttf", "NotoSans-Regular.ttf");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);

            FontItemViewModel chosen = model.Fonts.First(f => f.File.FileName == "Inter-Regular.ttf");
            model.SelectedFont = chosen;

            model.SearchText = "inter";

            Assert.Same(chosen, model.SelectedFont);
            Assert.NotNull(model.Preview.Face);
        }
    }

    [Fact]
    public async Task ASearchAlwaysLeavesSomethingSelected()
    {
        Seed("Inter-Regular.ttf", "NotoSans-Regular.ttf", "SourceSerif4-Regular.otf");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);

            foreach (string typed in new[] { "n", "no", "not", "noto", "not", "no", "n", "" })
            {
                model.SearchText = typed;

                if (model.VisibleFonts.Count > 0)
                {
                    Assert.NotNull(model.SelectedFont);
                    Assert.Contains(model.SelectedFont!, model.VisibleFonts);
                }
            }
        }
    }

    [Fact]
    public async Task SearchOnAThousandFonts_IsInstant()
    {
        string source = Path.Combine(TestFonts.RequireCorpus(), "Inter-Regular.ttf");
        if (!File.Exists(source)) Assert.Skip("Inter-Regular.ttf is not in the corpus.");

        byte[] bytes = File.ReadAllBytes(source);
        for (int i = 0; i < 1000; i++)
            File.WriteAllBytes(Path.Combine(_fonts, $"Font{i:D4}.ttf"), bytes);

        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            Assert.Equal(1000, model.Fonts.Count);

            // Typing is what this has to keep up with, so time a few keystrokes' worth.
            var timer = Stopwatch.StartNew();
            foreach (string typed in new[] { "F", "Fo", "Fon", "Font0", "Font01", "Font012" })
                model.SearchText = typed;
            timer.Stop();

            Assert.True(timer.ElapsedMilliseconds < 1500,
                $"six keystrokes over 1,000 fonts took {timer.ElapsedMilliseconds} ms");
            Assert.Equal(10, model.VisibleFonts.Count);   // Font0120 to Font0129
        }
    }

    // --- Sorting ---------------------------------------------------------------

    [Fact]
    public async Task SortingByFileName_IsNaturalOrder()
    {
        Seed("Inter-Regular.ttf", "NotoSans-Regular.ttf", "SourceSerif4-Regular.otf");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            model.SetSortCommand.Execute("FileName");

            Assert.Equal(
                ["Inter-Regular.ttf", "NotoSans-Regular.ttf", "SourceSerif4-Regular.otf"],
                model.VisibleFonts.Select(font => font.File.FileName));
        }
    }

    [Fact]
    public async Task SortingByFamily_GroupsAFamilyTogether()
    {
        Seed("NotoSans-Regular.ttf", "Inter-Bold.ttf", "Inter-Regular.ttf", "NotoSans-Bold.ttf");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            await model.MetadataScanCompleted;

            model.SetSortCommand.Execute("FamilyName");

            string[] families = [.. model.VisibleFonts.Select(font => font.Family)];

            // Every family appears as one unbroken run.
            Assert.Equal(families.Distinct().Count(),
                families.Where((name, i) => i == 0 || name != families[i - 1]).Count());
        }
    }

    [Fact]
    public async Task SortingByFormat_GathersTheFormats()
    {
        Seed("Inter-Regular.ttf", "Inter-Regular.woff2", "SourceSerif4-Regular.otf", "Inter-Regular.woff");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            model.SetSortCommand.Execute("Format");

            string[] badges = [.. model.VisibleFonts.Select(font => font.FormatBadge)];
            Assert.Equal(badges.Distinct().Count(),
                badges.Where((badge, i) => i == 0 || badge != badges[i - 1]).Count());
        }
    }

    [Fact]
    public async Task Sorting_KeepsTheSelectedFontSelected()
    {
        Seed("Inter-Regular.ttf", "NotoSans-Regular.ttf", "SourceSerif4-Regular.otf");
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            await model.MetadataScanCompleted;

            model.LastFontCommand.Execute(null);
            FontItemViewModel selected = model.SelectedFont!;

            model.SetSortCommand.Execute("Format");

            Assert.Same(selected, model.SelectedFont);
        }
    }

    // --- Subfolders ------------------------------------------------------------

    [Fact]
    public async Task TheSubfolderToggle_RescansWiderAndNarrower()
    {
        Seed("Inter-Regular.ttf");

        string nested = Path.Combine(_fonts, "nested");
        Directory.CreateDirectory(nested);
        File.Copy(
            Path.Combine(TestFonts.RequireCorpus(), "NotoSans-Regular.ttf"),
            Path.Combine(nested, "NotoSans-Regular.ttf"));

        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            Assert.Single(model.Fonts);

            // Toggling rescans by itself; awaiting it is how the test keeps up.
            model.IncludeSubfolders = true;
            await model.PendingRescan;
            Assert.Equal(2, model.Fonts.Count);

            model.IncludeSubfolders = false;
            await model.PendingRescan;
            Assert.Single(model.Fonts);
        }
    }

    // --- Recent folders --------------------------------------------------------

    [Fact]
    public async Task OpeningFolders_RemembersThemMostRecentFirst()
    {
        Seed("Inter-Regular.ttf");

        string second = Path.Combine(Root, "second");
        Directory.CreateDirectory(second);
        File.Copy(
            Path.Combine(TestFonts.RequireCorpus(), "NotoSans-Regular.ttf"),
            Path.Combine(second, "NotoSans-Regular.ttf"));

        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            await model.OpenPathAsync(second, TestCancellation);

            Assert.Equal(2, model.RecentFolders.Count);
            Assert.Equal(second, model.RecentFolders[0]);

            // Reopening the first moves it back to the top rather than duplicating it.
            await model.OpenPathAsync(_fonts, TestCancellation);

            Assert.Equal(2, model.RecentFolders.Count);
            Assert.Equal(_fonts, model.RecentFolders[0]);
        }
    }

    [Fact]
    public async Task TheRecentList_StopsAtTen()
    {
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            for (int i = 0; i < 15; i++)
            {
                string folder = Path.Combine(Root, $"folder{i}");
                Directory.CreateDirectory(folder);
                await model.OpenPathAsync(folder, TestCancellation);
            }

            Assert.Equal(AppSettings.MaximumRecentFolders, model.RecentFolders.Count);
            Assert.Equal(Path.Combine(Root, "folder14"), model.RecentFolders[0]);
        }
    }

    // --- Settings round trip ---------------------------------------------------

    [Fact]
    public async Task EverySettingSurvivesARestart()
    {
        Seed("Inter-Regular.ttf", "NotoSans-Regular.ttf");

        string settingsPath = Path.Combine(Root, "settings.json");

        // First run: change things, then write them out.
        var first = new SettingsService(settingsPath);
        first.Load();

        using (var render = new RenderService())
        using (var model = new MainViewModel(render))
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            model.LastFontCommand.Execute(null);

            model.IncludeSubfolders = true;
            model.SortOrder = SortOrder.Format;
            model.Preview.PreviewText = "Remembered text";
            model.Preview.PointSize = 72;
            model.Preview.DarkBackground = true;
            model.Gallery.GallerySize = 44;
            model.GlyphMap.CellSize = 96;
            model.CurrentView = ViewMode.Gallery;
            model.CurrentPanel = SingleFontPanel.Info;

            model.CaptureSettings(first.Current);
            Assert.True(first.Save());
        }

        // Second run: a fresh service and model reading what the first wrote.
        var second = new SettingsService(settingsPath);
        second.Load();

        using (var render = new RenderService())
        using (var model = new MainViewModel(render))
        {
            model.ApplySettings(second.Current);

            Assert.True(model.IncludeSubfolders);
            Assert.Equal(SortOrder.Format, model.SortOrder);
            Assert.Equal("Remembered text", model.Preview.PreviewText);
            Assert.Equal(72, model.Preview.PointSize);
            Assert.True(model.Preview.DarkBackground);
            Assert.Equal(44, model.Gallery.GallerySize);
            Assert.Equal(96, model.GlyphMap.CellSize);
            Assert.Equal(ViewMode.Gallery, model.CurrentView);
            Assert.Equal(SingleFontPanel.Info, model.CurrentPanel);
            Assert.Contains(_fonts, model.RecentFolders);

            // And the folder reopens where it was left.
            await model.RestoreLastSessionAsync(second.Current, TestCancellation);

            Assert.Equal(2, model.Fonts.Count);
            Assert.Equal(second.Current.LastSelectedFile, model.SelectedFont!.File.Path);
        }
    }

    [Fact]
    public void TheSettingsFileIsReadable()
    {
        string path = Path.Combine(Root, "settings.json");
        Directory.CreateDirectory(Root);

        var settings = new SettingsService(path);
        settings.Current.SortOrder = SortOrder.FamilyName;
        settings.Current.SelectedView = ViewMode.Gallery;
        Assert.True(settings.Save());

        string json = File.ReadAllText(path);

        // Enums as names: the file stays legible, and reordering an enum later cannot
        // silently change what someone's saved settings mean.
        Assert.Contains("\"FamilyName\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Gallery\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReopeningAFolderThatHasGone_StartsEmptyRatherThanComplaining()
    {
        string missing = Path.Combine(Root, "unplugged");

        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.RestoreLastSessionAsync(
                new AppSettings { LastFolder = missing }, TestCancellation);

            Assert.Empty(model.Fonts);
            Assert.Null(model.Message);    // not worth a complaint on startup
        }
    }

    [Fact]
    public void ASettingsFileFromTheFuture_DoesNotLoseItsUnknownFields()
    {
        string path = Path.Combine(Root, "settings.json");
        Directory.CreateDirectory(Root);

        File.WriteAllText(path, """
            {
              "SortOrder": "Format",
              "SomethingAddedLater": { "value": 42 }
            }
            """);

        var settings = new SettingsService(path);
        settings.Load();
        Assert.Equal(SortOrder.Format, settings.Current.SortOrder);

        Assert.True(settings.Save());
        Assert.Contains("SomethingAddedLater", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutOfRangeSetting_IsClampedRatherThanTrusted()
    {
        (MainViewModel model, RenderService render) = Build();

        using (render)
        using (model)
        {
            // A hand-edited or corrupted file should not leave the app unusable.
            model.ApplySettings(new AppSettings
            {
                PreviewPointSize = 100000,
                GallerySize = -5,
                GlyphCellSize = 9999,
            });

            Assert.InRange(model.Preview.PointSize,
                PreviewViewModel.MinimumPointSize, PreviewViewModel.MaximumPointSize);
            Assert.InRange(model.Gallery.GallerySize,
                GalleryViewModel.MinimumSize, GalleryViewModel.MaximumSize);
            Assert.InRange(model.GlyphMap.CellSize,
                GlyphMapViewModel.MinimumCellSize, GlyphMapViewModel.MaximumCellSize);
        }
    }
}
