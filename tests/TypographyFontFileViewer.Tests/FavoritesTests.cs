using System.IO;
using System.Text.Json;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using TypographyFontFileViewer.ViewModels;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>Records what it was asked to remove, and removes it outright.</summary>
/// <remarks>
/// A suite that put a few hundred files into the developer's Recycle Bin on every run
/// would be a nuisance. The real recycler is exercised once, by its own test.
/// </remarks>
internal sealed class RecordingRecycler : IFileRecycler
{
    public List<string> Recycled { get; } = [];

    public string? FailWith { get; set; }

    public string? Recycle(string path)
    {
        if (FailWith is not null) return FailWith;

        Recycled.Add(path);
        if (File.Exists(path)) File.Delete(path);
        return null;
    }
}

public class HashServiceTests : TempFolderTest
{
    public HashServiceTests() : base("hash") { }

    private string Write(string name, string contents)
    {
        string path = Path.Combine(Root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public async Task TheSameContentsHashTheSame_WhateverTheNames()
    {
        string first = Write("Roboto.ttf", "identical bytes");
        string second = Write("SomethingElse.ttf", "identical bytes");

        var hashes = new HashService();

        // This is the whole point: identity is the contents, not the name.
        Assert.Equal(
            await hashes.ComputeAsync(first, TestCancellation),
            await hashes.ComputeAsync(second, TestCancellation));
    }

    [Fact]
    public async Task DifferentContents_HashDifferently()
    {
        string first = Write("A.ttf", "one");
        string second = Write("B.ttf", "two");

        var hashes = new HashService();

        Assert.NotEqual(
            await hashes.ComputeAsync(first, TestCancellation),
            await hashes.ComputeAsync(second, TestCancellation));
    }

    [Fact]
    public async Task AFileThatChanges_IsHashedAgain()
    {
        string path = Write("Changing.ttf", "before");

        var hashes = new HashService();
        string? before = await hashes.ComputeAsync(path, TestCancellation);

        // A new length and write time make a new cache key.
        await Task.Delay(20, TestCancellation);
        File.WriteAllText(path, "after, and longer");

        Assert.NotEqual(before, await hashes.ComputeAsync(path, TestCancellation));
    }

    [Fact]
    public async Task AMissingFile_IsNullRatherThanAnException()
    {
        var hashes = new HashService();
        Assert.Null(await hashes.ComputeAsync(
            Path.Combine(Root, "nowhere.ttf"), TestCancellation));
    }

    [Fact]
    public async Task HashingDoesNotLockTheFile()
    {
        string path = Write("Open.ttf", "contents");

        var hashes = new HashService();
        Assert.NotNull(await hashes.ComputeAsync(path, TestCancellation));

        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}

public class SettingsServiceTests : TempFolderTest
{
    public SettingsServiceTests() : base("settings") { }

    private string Path_ => System.IO.Path.Combine(Root, "TypographyFontFileViewer.json");

    [Fact]
    public void SettingsSurviveARoundTrip()
    {
        var saved = new SettingsService(Path_);
        saved.Current.Favorites.Add(new FavoriteEntry
        {
            FileName = "Inter-Regular.ttf",
            Sha256 = "abc123",
            Size = 4096,
        });

        Assert.True(saved.Save());

        var loaded = new SettingsService(Path_);
        loaded.Load();

        FavoriteEntry entry = Assert.Single(loaded.Current.Favorites);
        Assert.Equal("Inter-Regular.ttf", entry.FileName);
        Assert.Equal("abc123", entry.Sha256);
        Assert.Equal(4096, entry.Size);
    }

    [Fact]
    public void AMissingFile_LoadsDefaults()
    {
        var settings = new SettingsService(Path_);
        settings.Load();

        Assert.Empty(settings.Current.Favorites);
        Assert.Null(settings.LastProblem);
    }

    [Fact]
    public void ACorruptFile_IsRenamedAndDefaultsAreUsed()
    {
        File.WriteAllText(Path_, "{ this is not json");

        var settings = new SettingsService(Path_);
        settings.Load();

        Assert.Empty(settings.Current.Favorites);
        Assert.False(string.IsNullOrWhiteSpace(settings.LastProblem));

        // Renamed rather than deleted: it is the user's data.
        Assert.True(File.Exists(Path_ + ".bad"));
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void FieldsFromANewerVersion_SurviveARoundTrip()
    {
        File.WriteAllText(Path_, """
            { "Favorites": [], "SomethingFromTheFuture": { "a": 1 } }
            """);

        var settings = new SettingsService(Path_);
        settings.Load();
        Assert.True(settings.Save());

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path_));
        Assert.True(document.RootElement.TryGetProperty("SomethingFromTheFuture", out _),
            "an older version should not strip settings it does not understand");
    }

    [Fact]
    public void SavingLeavesNoTemporaryFileBehind()
    {
        var settings = new SettingsService(Path_);
        Assert.True(settings.Save());

        Assert.False(File.Exists(Path_ + ".tmp"));
        Assert.True(File.Exists(Path_));
    }
}

/// <summary>
/// Phase 5 acceptance: favouriting copies the file next to the exe, un-favouriting sends
/// that copy to the Recycle Bin, and the stars stay right across restarts and renames.
/// </summary>
public class FavoritesServiceTests : TempFolderTest
{
    private readonly string _source;
    private readonly string _favorites;

    public FavoritesServiceTests() : base("fav")
    {
        _source = Folder("source");
        _favorites = Path.Combine(Root, "Favorite Fonts");
    }

    private (FavoritesService Service, RecordingRecycler Recycler, SettingsService Settings) Build()
    {
        var settings = new SettingsService(Path.Combine(Root, "settings.json"));
        settings.Load();

        var recycler = new RecordingRecycler();
        var service = new FavoritesService(_favorites, new HashService(), recycler, settings);

        return (service, recycler, settings);
    }

    /// <summary>Writes a file in the source folder and describes it as the scanner would.</summary>
    private FontFile MakeFont(string name, string contents = "font bytes", FontFormat format = FontFormat.TrueType)
    {
        string path = Path.Combine(_source, name);
        File.WriteAllText(path, contents);
        var info = new FileInfo(path);

        return new FontFile
        {
            Path = path,
            Format = format,
            Length = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
        };
    }

    // --- Adding ----------------------------------------------------------------

    [Fact]
    public async Task Favouriting_CopiesTheFileIntoTheFolder()
    {
        (FavoritesService service, _, _) = Build();
        FontFile font = MakeFont("Inter-Regular.ttf");

        FavoriteResult result = await service.AddAsync(font, TestCancellation);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(Path.Combine(_favorites, "Inter-Regular.ttf")));
        Assert.Equal(1, service.Count);

        // The original is untouched.
        Assert.True(File.Exists(font.Path));
    }

    [Fact]
    public async Task FavouritingTwice_DoesNothingTheSecondTime()
    {
        (FavoritesService service, _, _) = Build();
        FontFile font = MakeFont("Inter-Regular.ttf");

        await service.AddAsync(font, TestCancellation);
        await service.AddAsync(font, TestCancellation);

        Assert.Equal(1, service.Count);
        Assert.Single(Directory.GetFiles(_favorites));
    }

    [Fact]
    public async Task ADifferentFontWithTheSameName_StepsAside()
    {
        (FavoritesService service, _, _) = Build();

        FontFile first = MakeFont("Roboto.ttf", "the first Roboto");
        await service.AddAsync(first, TestCancellation);

        // A genuinely different font that happens to share a name — which is exactly
        // what two downloads of "Roboto.ttf" from different sites look like.
        Directory.CreateDirectory(Path.Combine(_source, "elsewhere"));
        string otherPath = Path.Combine(_source, "elsewhere", "Roboto.ttf");
        File.WriteAllText(otherPath, "a completely different Roboto");
        var otherInfo = new FileInfo(otherPath);

        var second = new FontFile
        {
            Path = otherPath,
            Format = FontFormat.TrueType,
            Length = otherInfo.Length,
            LastWriteUtc = otherInfo.LastWriteTimeUtc,
        };

        FavoriteResult result = await service.AddAsync(second, TestCancellation);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(Path.Combine(_favorites, "Roboto.ttf")));
        Assert.True(File.Exists(Path.Combine(_favorites, "Roboto (2).ttf")));
        Assert.Equal(2, service.Count);
    }

    [Fact]
    public async Task ARenamedCopyOfAFavourite_IsStillRecognised()
    {
        (FavoritesService service, _, _) = Build();

        FontFile original = MakeFont("Inter-Regular.ttf", "the very same bytes");
        await service.AddAsync(original, TestCancellation);

        // The same font, under another name, in another folder.
        string elsewhere = Path.Combine(_source, "MyFavouriteFont.ttf");
        File.WriteAllText(elsewhere, "the very same bytes");

        Assert.True(await service.IsFavoriteAsync(
            elsewhere, new FileInfo(elsewhere).Length, TestCancellation));
    }

    [Fact]
    public async Task AFontThatIsNotAFavourite_IsNotStarred()
    {
        (FavoritesService service, _, _) = Build();

        await service.AddAsync(MakeFont("Inter-Regular.ttf", "one"), TestCancellation);
        FontFile other = MakeFont("Something.ttf", "quite different, and longer");

        Assert.False(await service.IsFavoriteAsync(other.Path, other.Length, TestCancellation));
    }

    [Fact]
    public async Task AType1Font_BringsItsMetricsAndTheyAreRenamedToMatch()
    {
        (FavoritesService service, _, _) = Build();

        string pfb = Path.Combine(_source, "Nimbus.pfb");
        File.WriteAllText(pfb, "type 1 outlines");
        File.WriteAllText(Path.Combine(_source, "Nimbus.afm"), "metrics with kerning");
        File.WriteAllText(Path.Combine(_source, "Nimbus.pfm"), "binary metrics");

        var info = new FileInfo(pfb);
        var font = new FontFile
        {
            Path = pfb,
            Format = FontFormat.Type1,
            Length = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            Companions = [
                Path.Combine(_source, "Nimbus.afm"),
                Path.Combine(_source, "Nimbus.pfm"),
            ],
        };

        // Occupy the name so the font has to step aside, and check the metrics follow it.
        File.WriteAllText(Path.Combine(_source, "decoy.pfb"), "something else entirely");
        Directory.CreateDirectory(_favorites);
        File.WriteAllText(Path.Combine(_favorites, "Nimbus.pfb"), "a different Nimbus");

        FavoriteResult result = await service.AddAsync(font, TestCancellation);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(Path.Combine(_favorites, "Nimbus (2).pfb")));
        Assert.True(File.Exists(Path.Combine(_favorites, "Nimbus (2).afm")),
            "the metrics must be renamed to match their font, or FreeType will not find them");
        Assert.True(File.Exists(Path.Combine(_favorites, "Nimbus (2).pfm")));
    }

    // --- Removing --------------------------------------------------------------

    [Fact]
    public async Task UnFavouriting_SendsTheCopyToTheRecyclerAndForgetsIt()
    {
        (FavoritesService service, RecordingRecycler recycler, _) = Build();
        FontFile font = MakeFont("Inter-Regular.ttf");

        await service.AddAsync(font, TestCancellation);
        FavoriteResult result = await service.RemoveAsync(font.Path, font.Length, TestCancellation);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(0, service.Count);
        Assert.Single(recycler.Recycled);
        Assert.False(File.Exists(Path.Combine(_favorites, "Inter-Regular.ttf")));

        // The font the user favourited from is not the thing that was removed.
        Assert.True(File.Exists(font.Path));
    }

    [Fact]
    public async Task UnFavouritingAType1Font_TakesItsMetricsWithIt()
    {
        (FavoritesService service, RecordingRecycler recycler, _) = Build();

        string pfb = Path.Combine(_source, "Nimbus.pfb");
        File.WriteAllText(pfb, "type 1 outlines");
        File.WriteAllText(Path.Combine(_source, "Nimbus.afm"), "metrics");

        var info = new FileInfo(pfb);
        var font = new FontFile
        {
            Path = pfb,
            Format = FontFormat.Type1,
            Length = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            Companions = [Path.Combine(_source, "Nimbus.afm")],
        };

        await service.AddAsync(font, TestCancellation);
        await service.RemoveAsync(font.Path, font.Length, TestCancellation);

        Assert.Equal(2, recycler.Recycled.Count);
        Assert.Empty(Directory.GetFiles(_favorites));
    }

    [Fact]
    public async Task IfTheRecyclerRefuses_TheFavouriteStays()
    {
        (FavoritesService service, RecordingRecycler recycler, _) = Build();
        FontFile font = MakeFont("Inter-Regular.ttf");

        await service.AddAsync(font, TestCancellation);
        recycler.FailWith = "the file is in use";

        FavoriteResult result = await service.RemoveAsync(font.Path, font.Length, TestCancellation);

        Assert.False(result.Succeeded);
        Assert.Contains("in use", result.Error!, StringComparison.Ordinal);

        // Still a favourite, because the file is still there.
        Assert.Equal(1, service.Count);
    }

    /// <summary>
    /// The one guard worth testing on its own: nothing outside the favourites folder is
    /// ever a candidate for deletion.
    /// </summary>
    /// <remarks>
    /// Reconciling rebuilds the index from the folder listing, so in normal use an
    /// entry's file name is always a bare name and this can never trigger. It is here
    /// because this is the only code in the app that removes a file, and "can never
    /// happen" is a poor foundation for that.
    /// </remarks>
    [Theory]
    [InlineData("Inter-Regular.ttf", true)]
    [InlineData("sub/Inter.ttf", true)]
    [InlineData("../outside.ttf", false)]
    [InlineData("../../far-outside.ttf", false)]
    [InlineData("sub/../../outside.ttf", false)]
    public void OnlyPathsInsideTheFavouritesFolder_MayBeDeleted(string relative, bool expected)
    {
        (FavoritesService service, _, _) = Build();

        string candidate = Path.Combine(_favorites, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected, service.IsInsideFavorites(candidate));
    }

    [Fact]
    public void TheFavouritesFolderItself_IsNotInsideItself()
    {
        (FavoritesService service, _, _) = Build();

        // Deleting the folder is not something un-favouriting should ever reach.
        Assert.False(service.IsInsideFavorites(_favorites));
    }

    // --- Reconciling -----------------------------------------------------------

    [Fact]
    public async Task AFontDroppedInByHand_IsPickedUpOnReconcile()
    {
        (FavoritesService service, _, _) = Build();
        Directory.CreateDirectory(_favorites);

        File.WriteAllText(Path.Combine(_favorites, "DroppedIn.ttf"), "dropped in by hand");

        await service.ReconcileAsync(TestCancellation);

        Assert.Equal(1, service.Count);
        Assert.True(await service.IsFavoriteAsync(
            Path.Combine(_favorites, "DroppedIn.ttf"),
            new FileInfo(Path.Combine(_favorites, "DroppedIn.ttf")).Length,
            TestCancellation));
    }

    [Fact]
    public async Task AFontDeletedByHand_IsDroppedOnReconcile()
    {
        (FavoritesService service, _, SettingsService settings) = Build();
        FontFile font = MakeFont("Inter-Regular.ttf");

        await service.AddAsync(font, TestCancellation);
        Assert.Equal(1, service.Count);

        File.Delete(Path.Combine(_favorites, "Inter-Regular.ttf"));
        await service.ReconcileAsync(TestCancellation);

        Assert.Equal(0, service.Count);
        Assert.Empty(settings.Current.Favorites);
    }

    [Fact]
    public async Task StarsSurviveARestart()
    {
        FontFile font = MakeFont("Inter-Regular.ttf");

        // First run.
        (FavoritesService first, _, _) = Build();
        await first.AddAsync(font, TestCancellation);

        // Second run: a fresh service reading the settings the first one wrote.
        (FavoritesService second, _, _) = Build();
        await second.ReconcileAsync(TestCancellation);

        Assert.Equal(1, second.Count);
        Assert.True(await second.IsFavoriteAsync(font.Path, font.Length, TestCancellation));
    }

    [Fact]
    public async Task OpeningTheFavouritesFolderMeansEverythingInItIsAFavourite()
    {
        (FavoritesService service, _, _) = Build();
        await service.AddAsync(MakeFont("Inter-Regular.ttf"), TestCancellation);

        Assert.True(service.IsFavoritesFolder(_favorites));
        Assert.True(service.IsFavoritesFolder(_favorites + Path.DirectorySeparatorChar));
        Assert.False(service.IsFavoritesFolder(_source));
    }

    // --- Errors ----------------------------------------------------------------

    [Fact]
    public async Task AFolderThatCannotBeCreated_SaysSoPlainly()
    {
        // A folder cannot live inside a file, so this stands in for Program Files.
        string blocker = Path.Combine(Root, "blocker");
        File.WriteAllText(blocker, "not a directory");

        var settings = new SettingsService(Path.Combine(Root, "settings.json"));
        var service = new FavoritesService(
            Path.Combine(blocker, "Favorite Fonts"),
            new HashService(), new RecordingRecycler(), settings);

        FavoriteResult result = await service.AddAsync(MakeFont("Inter.ttf"), TestCancellation);

        Assert.False(result.Succeeded);
        Assert.Contains("Favorite Fonts", result.Error!, StringComparison.Ordinal);
        Assert.Contains("Move the app", result.Error!, StringComparison.Ordinal);
        Assert.False(service.IsWritable);
    }

    [Fact]
    public async Task AnUnreadableSourceFont_FailsWithoutLeavingAnything()
    {
        (FavoritesService service, _, _) = Build();

        var missing = new FontFile
        {
            Path = Path.Combine(_source, "gone.ttf"),
            Format = FontFormat.TrueType,
            Length = 1024,
            LastWriteUtc = DateTime.UtcNow,
        };

        FavoriteResult result = await service.AddAsync(missing, TestCancellation);

        Assert.False(result.Succeeded);
        Assert.Equal(0, service.Count);
        Assert.False(Directory.Exists(_favorites) && Directory.GetFiles(_favorites).Length > 0);
    }

    [Fact]
    public async Task NoHalfWrittenFileIsLeftBehind()
    {
        (FavoritesService service, _, _) = Build();
        await service.AddAsync(MakeFont("Inter-Regular.ttf"), TestCancellation);

        // The copy goes to a temporary name first and is moved into place.
        Assert.Empty(Directory.GetFiles(_favorites, "*.part-*"));
    }

    // --- The real Recycle Bin ---------------------------------------------------

    /// <summary>
    /// Exercises the shipping recycler once, so the path everyone else fakes is known
    /// to work.
    /// </summary>
    [Fact]
    public void TheRealRecycler_RemovesAFileFromDisk()
    {
        string path = Path.Combine(Root, $"tffv-recyclebin-test-{Guid.NewGuid():N}.ttf");
        Directory.CreateDirectory(Root);
        File.WriteAllText(path, "this goes to the Recycle Bin");

        string? error = new RecycleBinFileRecycler().Recycle(path);

        Assert.Null(error);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TheRealRecycler_IsHappyWithAFileThatHasAlreadyGone()
    {
        string path = Path.Combine(Root, "never-existed.ttf");
        Assert.Null(new RecycleBinFileRecycler().Recycle(path));
    }
}

/// <summary>Favourites as the user meets them: stars in the list, Ctrl+D, the folder.</summary>
public class FavoritesInTheAppTests : TempFolderTest
{
    private readonly string _fonts;
    private readonly string _favorites;

    public FavoritesInTheAppTests() : base("favapp")
    {
        _fonts = Folder("fonts");
        _favorites = Path.Combine(Root, "Favorite Fonts");
    }

    private void Seed(params string[] names) => SeedCorpusFonts(_fonts, names);

    private (MainViewModel Model, FavoritesService Favorites, RecordingRecycler Recycler, RenderService Render)
        Build()
    {
        var settings = new SettingsService(Path.Combine(Root, "settings.json"));
        settings.Load();

        var recycler = new RecordingRecycler();
        var favorites = new FavoritesService(_favorites, new HashService(), recycler, settings);

        var render = new RenderService();
        return (new MainViewModel(render, favorites), favorites, recycler, render);
    }

    [Fact]
    public async Task StarringTheSelectedFont_CopiesItAndLightsTheStar()
    {
        Seed("Inter-Regular.ttf", "Inter-Bold.ttf");
        (MainViewModel model, _, _, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            FontItemViewModel font = model.SelectedFont!;
            Assert.False(font.IsFavorite);

            await model.ToggleFavoriteCommand.ExecuteAsync(null);

            Assert.True(font.IsFavorite);
            Assert.True(File.Exists(Path.Combine(_favorites, font.File.FileName)));
            Assert.Contains("★", model.FolderSummary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StarringOneFaceOfACollection_StarsThemAll()
    {
        Seed("TestCollection.ttc");
        (MainViewModel model, _, _, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            await model.MetadataScanCompleted;

            Assert.Equal(2, model.Fonts.Count);

            model.SelectedFont = model.Fonts[0];
            await model.ToggleFavoriteCommand.ExecuteAsync(null);

            // One file holds both faces, so starring either starts the same copy.
            Assert.All(model.Fonts, face => Assert.True(face.IsFavorite));
            Assert.Single(Directory.GetFiles(_favorites));
        }
    }

    [Fact]
    public async Task UnStarringWhileViewingTheFavouritesFolder_RemovesTheRow()
    {
        Seed("Inter-Regular.ttf", "Inter-Bold.ttf", "NotoSans-Regular.ttf");
        (MainViewModel model, _, _, RenderService render) = Build();

        using (render)
        using (model)
        {
            // Star all three, then go and look at the folder.
            await model.OpenPathAsync(_fonts, TestCancellation);
            foreach (FontItemViewModel font in model.Fonts.ToList())
            {
                model.SelectedFont = font;
                await model.ToggleFavoriteCommand.ExecuteAsync(null);
            }

            await model.OpenFavoritesCommand.ExecuteAsync(null);

            Assert.True(model.ViewingFavorites);
            Assert.Equal(3, model.Fonts.Count);
            Assert.All(model.Fonts, font => Assert.True(font.IsFavorite));

            model.SelectedFont = model.Fonts[0];
            await model.ToggleFavoriteCommand.ExecuteAsync(null);

            // The file has gone to the Recycle Bin, so the row goes too, and the
            // selection lands on something rather than nothing.
            Assert.Equal(2, model.Fonts.Count);
            Assert.NotNull(model.SelectedFont);
        }
    }

    [Fact]
    public async Task ARenamedDuplicateInAnotherFolder_ShowsTheSameStar()
    {
        Seed("Inter-Regular.ttf");
        (MainViewModel model, _, _, RenderService render) = Build();

        using (render)
        using (model)
        {
            await model.OpenPathAsync(_fonts, TestCancellation);
            await model.ToggleFavoriteCommand.ExecuteAsync(null);

            // The same bytes under a different name, somewhere else entirely.
            string elsewhere = Path.Combine(Root, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            File.Copy(
                Path.Combine(_fonts, "Inter-Regular.ttf"),
                Path.Combine(elsewhere, "A Font I Liked.ttf"));

            await model.OpenPathAsync(elsewhere, TestCancellation);

            FontItemViewModel renamed = Assert.Single(model.Fonts);
            Assert.True(renamed.IsFavorite,
                "identity is the file's contents, so a renamed copy is the same favourite");
        }
    }

    [Fact]
    public async Task WithNoFavouritesService_TheStarCommandsDoNothingRatherThanThrow()
    {
        Seed("Inter-Regular.ttf");

        using var render = new RenderService();
        using var model = new MainViewModel(render);     // no favourites configured

        await model.OpenPathAsync(_fonts, TestCancellation);

        Assert.False(model.FavoritesAvailable);
        await model.ToggleFavoriteCommand.ExecuteAsync(null);
        await model.OpenFavoritesCommand.ExecuteAsync(null);

        Assert.False(model.SelectedFont!.IsFavorite);
    }
}
