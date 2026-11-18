using System.IO;
using System.Text.Json;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using Xunit;

namespace TypographyFontFileViewer.Tests;

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
