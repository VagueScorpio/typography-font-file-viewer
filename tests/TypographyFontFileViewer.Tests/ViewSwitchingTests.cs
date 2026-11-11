using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using TypographyFontFileViewer.Controls;
using TypographyFontFileViewer.Services;
using TypographyFontFileViewer.ViewModels;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// Switching between the two views: one font large, or all of them at once.
/// </summary>
public class ViewSwitchingTests : IDisposable
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), $"tffv-views-{Guid.NewGuid():N}");

    public ViewSwitchingTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Seed(params string[] names)
    {
        string corpus = TestFonts.RequireCorpus();

        foreach (string name in names)
        {
            string source = Path.Combine(corpus, name);
            if (!File.Exists(source)) Assert.Skip($"{name} is not in the corpus.");
            File.Copy(source, Path.Combine(_folder, name), overwrite: true);
        }
    }

    [Fact]
    public void TheAppOpensOnTheSingleFontView()
    {
        using var service = new RenderService();
        using var model = new MainViewModel(service);

        Assert.Equal(ViewMode.SingleFont, model.CurrentView);
        Assert.True(model.IsSingleFont);
        Assert.False(model.IsGallery);
    }

    [Fact]
    public void TheViewCommands_SwitchAndReportBothWays()
    {
        using var service = new RenderService();
        using var model = new MainViewModel(service);

        model.ShowViewCommand.Execute("Gallery");
        Assert.Equal(ViewMode.Gallery, model.CurrentView);
        Assert.True(model.IsGallery);
        Assert.False(model.IsSingleFont);

        model.ShowViewCommand.Execute("SingleFont");
        Assert.Equal(ViewMode.SingleFont, model.CurrentView);

        // Anything unrecognised leaves the view alone rather than throwing.
        model.ShowViewCommand.Execute("Nonsense");
        Assert.Equal(ViewMode.SingleFont, model.CurrentView);
    }

    [Fact]
    public void ToggleView_FlipsBetweenTheTwo()
    {
        using var service = new RenderService();
        using var model = new MainViewModel(service);

        model.ToggleViewCommand.Execute(null);
        Assert.Equal(ViewMode.Gallery, model.CurrentView);

        model.ToggleViewCommand.Execute(null);
        Assert.Equal(ViewMode.SingleFont, model.CurrentView);
    }

    [Fact]
    public async Task DoubleClickingAGalleryRow_OpensThatFontLarge()
    {
        Seed("Inter-Regular.ttf", "Inter-Bold.ttf", "NotoSans-Regular.ttf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);

        await model.OpenPathAsync(_folder, TestCancellation);
        model.CurrentView = ViewMode.Gallery;

        FontItemViewModel wanted = model.Fonts[2];
        model.OpenInSingleView(wanted);

        Assert.Equal(ViewMode.SingleFont, model.CurrentView);
        Assert.Same(wanted, model.SelectedFont);

        // And the preview is already pointed at it.
        Assert.Equal(wanted.File.Path, model.Preview.Face!.Value.Path);
    }

    [Fact]
    public void OpeningWithNoFont_StillSwitchesToTheSingleView()
    {
        using var service = new RenderService();
        using var model = new MainViewModel(service);

        model.CurrentView = ViewMode.Gallery;
        model.OpenInSingleView(null);

        Assert.Equal(ViewMode.SingleFont, model.CurrentView);
    }

    [Fact]
    public async Task ResizingAppliesToWhicheverViewIsShowing()
    {
        Seed("Inter-Regular.ttf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);
        await model.OpenPathAsync(_folder, TestCancellation);

        double preview = model.Preview.PointSize;
        double gallery = model.Gallery.GallerySize;

        model.CurrentView = ViewMode.SingleFont;
        model.AdjustSize(4);
        Assert.Equal(preview + 4, model.Preview.PointSize);
        Assert.Equal(gallery, model.Gallery.GallerySize);

        model.CurrentView = ViewMode.Gallery;
        model.AdjustSize(4);
        Assert.Equal(gallery + 4, model.Gallery.GallerySize);
    }

    [Fact]
    public async Task SwitchingViews_KeepsTheSelectedFont()
    {
        Seed("Inter-Regular.ttf", "Inter-Bold.ttf");

        using var service = new RenderService();
        using var model = new MainViewModel(service);
        await model.OpenPathAsync(_folder, TestCancellation);

        model.NextFontCommand.Execute(null);
        FontItemViewModel selected = model.SelectedFont!;

        model.CurrentView = ViewMode.Gallery;
        Assert.Same(selected, model.SelectedFont);

        model.CurrentView = ViewMode.SingleFont;
        Assert.Same(selected, model.SelectedFont);
    }

    // --- The nested panel strip -------------------------------------------------

    [Fact]
    public void TheSingleFontViewOpensOnPreview()
    {
        using var service = new RenderService();
        using var model = new MainViewModel(service);

        Assert.Equal(SingleFontPanel.Preview, model.CurrentPanel);
    }

    [Fact]
    public void ShowPanel_SwitchesToTheSingleFontViewAsWellAsThePanel()
    {
        using var service = new RenderService();
        using var model = new MainViewModel(service);

        model.CurrentView = ViewMode.Gallery;
        model.ShowPanelCommand.Execute("Preview");

        // Asking for a panel implies asking for the view that holds it.
        Assert.Equal(ViewMode.SingleFont, model.CurrentView);
        Assert.Equal(SingleFontPanel.Preview, model.CurrentPanel);
    }

    /// <summary>
    /// Ctrl+3 and Ctrl+4 name panels that Phase 6 has not built yet.
    /// </summary>
    /// <remarks>
    /// They still switch to the single-font view, because that is half of what the user
    /// asked for, and settle on Preview rather than selecting a panel that is not on the
    /// strip. When Phase 6 adds the other two, raising AvailablePanelCount is all that
    /// changes here.
    /// </remarks>
    [Theory]
    [InlineData("Glyphs")]
    [InlineData("Info")]
    public void APanelThatDoesNotExistYet_FallsBackToPreview(string panel)
    {
        using var service = new RenderService();
        using var model = new MainViewModel(service);

        model.CurrentView = ViewMode.Gallery;
        model.ShowPanelCommand.Execute(panel);

        Assert.Equal(ViewMode.SingleFont, model.CurrentView);
        Assert.Equal(SingleFontPanel.Preview, model.CurrentPanel);
    }

    [Fact]
    public void AnUnrecognisedPanel_ChangesNothing()
    {
        using var service = new RenderService();
        using var model = new MainViewModel(service);

        model.CurrentView = ViewMode.Gallery;
        model.ShowPanelCommand.Execute("Nonsense");

        Assert.Equal(ViewMode.Gallery, model.CurrentView);
        Assert.Equal(SingleFontPanel.Preview, model.CurrentPanel);
    }

    [Fact]
    public void ThePanelStrip_ListsItsPanelsInTheOrderTheyAreShown()
    {
        // The enum values are the tab indices, so their order is the strip's order.
        Assert.Equal(0, (int)SingleFontPanel.Preview);
        Assert.Equal(1, (int)SingleFontPanel.Glyphs);
        Assert.Equal(2, (int)SingleFontPanel.Info);

        Assert.InRange(MainViewModel.AvailablePanelCount, 1, 3);
    }

    [Fact]
    public void EnumToIndex_MapsBothWaysAndClampsPastTheEnd()
    {
        var converter = new EnumToIndexConverter();

        Assert.Equal(0, converter.Convert(SingleFontPanel.Preview, typeof(int), null, CultureInfo.InvariantCulture));
        Assert.Equal(2, converter.Convert(SingleFontPanel.Info, typeof(int), null, CultureInfo.InvariantCulture));

        Assert.Equal(SingleFontPanel.Glyphs,
            converter.ConvertBack(1, typeof(SingleFontPanel), null, CultureInfo.InvariantCulture));

        // A tab strip shorter than the enum reports -1 when it has no selection.
        Assert.Equal(SingleFontPanel.Preview,
            converter.ConvertBack(-1, typeof(SingleFontPanel), null, CultureInfo.InvariantCulture));
        Assert.Equal(SingleFontPanel.Preview,
            converter.ConvertBack(99, typeof(SingleFontPanel), null, CultureInfo.InvariantCulture));
    }

    // --- Gallery size presets ---------------------------------------------------

    [Fact]
    public void TheSizePresets_AreNamedAndInOrder()
    {
        Assert.Equal(4, GalleryViewModel.SizePresets.Count);
        Assert.Equal(["Small", "Medium", "Large", "Extra large"],
            GalleryViewModel.SizePresets.Select(preset => preset.Name));

        double previous = 0;
        foreach ((string _, double size) in GalleryViewModel.SizePresets)
        {
            Assert.True(size > previous, "the presets should increase in size");
            Assert.InRange(size, GalleryViewModel.MinimumSize, GalleryViewModel.MaximumSize);
            previous = size;
        }
    }

    [Fact]
    public void TheGalleryStartsAtMedium()
    {
        using var service = new RenderService();
        using var preview = new PreviewViewModel(service);
        using var gallery = new GalleryViewModel(service, preview);

        Assert.Equal(GalleryViewModel.MediumSize, gallery.GallerySize);
        Assert.Equal("Medium", gallery.SizeName);
    }

    [Fact]
    public void PickingAPreset_NamesItselfAndChangesTheRowHeight()
    {
        using var service = new RenderService();
        using var preview = new PreviewViewModel(service);
        using var gallery = new GalleryViewModel(service, preview);

        gallery.GallerySize = GalleryViewModel.SmallSize;
        Assert.Equal("Small", gallery.SizeName);
        double small = gallery.RowHeight;

        gallery.GallerySize = GalleryViewModel.ExtraLargeSize;
        Assert.Equal("Extra large", gallery.SizeName);

        Assert.True(gallery.RowHeight > small * 2,
            "extra large rows should be much taller than small ones");
    }

    [Fact]
    public void ASizeBetweenPresets_ReportsTheNumberInstead()
    {
        using var service = new RenderService();
        using var preview = new PreviewViewModel(service);
        using var gallery = new GalleryViewModel(service, preview);

        gallery.GallerySize = 33;

        // The slider is still free to land anywhere; it just is not a named size.
        Assert.Equal("33 pt", gallery.SizeName);
    }

    // --- The converters the switch is built on ----------------------------------

    [Fact]
    public void EnumToBoolean_MatchesAndWritesBack()
    {
        var converter = new EnumToBooleanConverter();

        Assert.True((bool)converter.Convert(ViewMode.Gallery, typeof(bool), "Gallery", CultureInfo.InvariantCulture));
        Assert.False((bool)converter.Convert(ViewMode.Gallery, typeof(bool), "SingleFont", CultureInfo.InvariantCulture));

        Assert.Equal(ViewMode.Gallery,
            converter.ConvertBack(true, typeof(ViewMode), "Gallery", CultureInfo.InvariantCulture));

        // Only the button being switched on may write; the one switching off must not.
        Assert.Equal(Binding.DoNothing,
            converter.ConvertBack(false, typeof(ViewMode), "SingleFont", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EnumToVisibility_ShowsOnlyTheMatchingView()
    {
        var converter = new EnumToVisibilityConverter();

        Assert.Equal(Visibility.Visible,
            converter.Convert(ViewMode.Gallery, typeof(Visibility), "Gallery", CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed,
            converter.Convert(ViewMode.Gallery, typeof(Visibility), "SingleFont", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DoubleEquals_LightsUpThePresetTheSliderLandsOn()
    {
        var converter = new DoubleEqualsConverter();

        Assert.True((bool)converter.Convert(28.0, typeof(bool), "28", CultureInfo.InvariantCulture));
        Assert.False((bool)converter.Convert(29.0, typeof(bool), "28", CultureInfo.InvariantCulture));

        Assert.Equal(44.0, converter.ConvertBack(true, typeof(double), "44", CultureInfo.InvariantCulture));
        Assert.Equal(Binding.DoNothing,
            converter.ConvertBack(false, typeof(double), "44", CultureInfo.InvariantCulture));
    }
}
