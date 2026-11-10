using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using TypographyFontFileViewer.ViewModels;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>The bitmap cache that makes scrolling back up instant.</summary>
public class RenderedBitmapCacheTests
{
    private static BitmapSource MakeBitmap(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static GalleryRenderKey Key(string path, int size = 28) => new(
        new FaceKey(path, 0, 1000, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        "sample", size, DarkBackground: false);

    [Fact]
    public void AStoredBitmap_ComesBackAgain()
    {
        var cache = new RenderedBitmapCache();
        BitmapSource bitmap = MakeBitmap(100, 20);

        cache.Add(Key("a.ttf"), bitmap);

        Assert.Same(bitmap, cache.TryGet(Key("a.ttf")));
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void EverythingThatChangesThePixels_IsPartOfTheKey()
    {
        var cache = new RenderedBitmapCache();
        cache.Add(Key("a.ttf", size: 28), MakeBitmap(100, 20));

        // A different size is a different picture, so it must not be a hit.
        Assert.Null(cache.TryGet(Key("a.ttf", size: 40)));
        Assert.Null(cache.TryGet(Key("b.ttf", size: 28)));
    }

    [Fact]
    public void TheCache_EvictsTheLeastRecentlyUsedRow()
    {
        var cache = new RenderedBitmapCache(maximumEntries: 3);

        for (int i = 0; i < 3; i++) cache.Add(Key($"{i}.ttf"), MakeBitmap(50, 10));

        // Touch the oldest so it is no longer the oldest.
        Assert.NotNull(cache.TryGet(Key("0.ttf")));

        cache.Add(Key("3.ttf"), MakeBitmap(50, 10));

        Assert.Equal(3, cache.Count);
        Assert.NotNull(cache.TryGet(Key("0.ttf")));
        Assert.Null(cache.TryGet(Key("1.ttf")));       // the one that fell off
        Assert.NotNull(cache.TryGet(Key("3.ttf")));
    }

    [Fact]
    public void TheCache_AlsoStopsAtItsByteBudget()
    {
        // Room for roughly two 100x100 rows.
        var cache = new RenderedBitmapCache(maximumEntries: 1000, maximumBytes: 100 * 100 * 4 * 2);

        for (int i = 0; i < 10; i++) cache.Add(Key($"{i}.ttf"), MakeBitmap(100, 100));

        Assert.True(cache.Count <= 3, $"cache held {cache.Count} rows past its byte budget");
        Assert.True(cache.Bytes <= 100 * 100 * 4 * 3);
    }

    [Fact]
    public void InvalidatingAFile_ForgetsEveryRowDrawnFromIt()
    {
        var cache = new RenderedBitmapCache();

        cache.Add(Key("a.ttf", 20), MakeBitmap(50, 10));
        cache.Add(Key("a.ttf", 40), MakeBitmap(50, 10));
        cache.Add(Key("b.ttf", 20), MakeBitmap(50, 10));

        cache.InvalidatePath("a.ttf");

        Assert.Null(cache.TryGet(Key("a.ttf", 20)));
        Assert.Null(cache.TryGet(Key("a.ttf", 40)));
        Assert.NotNull(cache.TryGet(Key("b.ttf", 20)));
    }

    [Fact]
    public void AnUnfrozenBitmap_IsRefused()
    {
        var cache = new RenderedBitmapCache();
        var bitmap = new WriteableBitmap(10, 10, 96, 96, PixelFormats.Bgra32, null);

        // The cache is read from the UI and written from render continuations, so what
        // goes in has to be safe to share.
        Assert.Throws<ArgumentException>(() => cache.Add(Key("a.ttf"), bitmap));
    }
}

/// <summary>
/// Phase 4 acceptance: a folder of a thousand fonts renders row by row without the
/// memory or the queue growing without limit.
/// </summary>
public class GalleryViewModelTests : IDisposable
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), $"tffv-gallery-{Guid.NewGuid():N}");

    public GalleryViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private List<string> SeedCopies(int count)
    {
        string source = Path.Combine(TestFonts.RequireCorpus(), "Inter-Regular.ttf");
        if (!File.Exists(source)) Assert.Skip("Inter-Regular.ttf is not in the corpus.");

        byte[] bytes = File.ReadAllBytes(source);
        var paths = new List<string>(count);

        for (int i = 0; i < count; i++)
        {
            string path = Path.Combine(_folder, $"Font{i:D4}.ttf");
            File.WriteAllBytes(path, bytes);
            paths.Add(path);
        }

        return paths;
    }

    [Fact]
    public async Task ARow_RendersAndIsThenServedFromTheCache()
    {
        List<string> fonts = SeedCopies(1);

        using var service = new RenderService();
        using var preview = new PreviewViewModel(service);
        using var gallery = new GalleryViewModel(service, preview);

        var key = FaceKey.For(fonts[0], 0);

        BitmapSource? first = await gallery.RenderRowAsync(key, TestCancellation);
        Assert.NotNull(first);
        Assert.True(first!.IsFrozen);
        Assert.Equal(1, gallery.Cache.Count);

        BitmapSource? second = await gallery.RenderRowAsync(key, TestCancellation);
        Assert.Same(first, second);
        Assert.True(gallery.Cache.Hits >= 1);
    }

    [Fact]
    public async Task ChangingTheSize_ProducesADifferentRow()
    {
        List<string> fonts = SeedCopies(1);

        using var service = new RenderService();
        using var preview = new PreviewViewModel(service);
        using var gallery = new GalleryViewModel(service, preview);

        var key = FaceKey.For(fonts[0], 0);

        gallery.GallerySize = 16;
        BitmapSource? small = await gallery.RenderRowAsync(key, TestCancellation);

        gallery.GallerySize = 48;
        BitmapSource? large = await gallery.RenderRowAsync(key, TestCancellation);

        Assert.NotNull(small);
        Assert.NotNull(large);
        Assert.True(large!.PixelHeight > small!.PixelHeight);
    }

    [Fact]
    public async Task ChangingTheTextOrTheSize_AsksEveryRowToRedrawOnce()
    {
        using var service = new RenderService();
        using var preview = new PreviewViewModel(service);
        using var gallery = new GalleryViewModel(service, preview);

        int before = gallery.Epoch;

        gallery.GallerySize = 40;
        Assert.Equal(before + 1, gallery.Epoch);

        preview.PreviewText = "Something else";
        Assert.Equal(before + 2, gallery.Epoch);

        preview.DarkBackground = true;
        Assert.Equal(before + 3, gallery.Epoch);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task AThousandRows_RenderWithoutTheCacheGrowingWithoutLimit()
    {
        // Distinct files, so every row is a genuine miss and the cache is really pushed.
        List<string> fonts = SeedCopies(1000);

        using var service = new RenderService();
        using var preview = new PreviewViewModel(service);
        using var gallery = new GalleryViewModel(service, preview);

        foreach (string path in fonts)
        {
            await gallery.RenderRowAsync(FaceKey.For(path, 0), TestCancellation);
        }

        // Bounded by its own limits, not by how many fonts were thrown at it.
        Assert.True(gallery.Cache.Count <= 300,
            $"cache held {gallery.Cache.Count} rows");
        Assert.True(gallery.Cache.Bytes <= 96L * 1024 * 1024,
            $"cache held {gallery.Cache.Bytes / (1024 * 1024)} MB");
    }

    [Fact]
    public async Task ACancelledRow_DoesNotCompleteOrPoisonTheCache()
    {
        List<string> fonts = SeedCopies(1);

        using var service = new RenderService();
        using var preview = new PreviewViewModel(service);
        using var gallery = new GalleryViewModel(service, preview);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gallery.RenderRowAsync(FaceKey.For(fonts[0], 0), cancellation.Token));

        Assert.Equal(0, gallery.Cache.Count);

        // And a fresh request still works.
        Assert.NotNull(await gallery.RenderRowAsync(FaceKey.For(fonts[0], 0), TestCancellation));
    }

    [Fact]
    public async Task ManyRowsAbandonedMidFlight_StillLeaveTheGalleryWorking()
    {
        List<string> fonts = SeedCopies(40);

        using var service = new RenderService();
        using var preview = new PreviewViewModel(service);
        using var gallery = new GalleryViewModel(service, preview);

        // What a fast scroll looks like: start a row, give up, start the next.
        for (int i = 0; i < fonts.Count; i++)
        {
            var cancellation = new CancellationTokenSource();
            Task<BitmapSource?> row = gallery.RenderRowAsync(
                FaceKey.For(fonts[i], 0), cancellation.Token);

            await cancellation.CancelAsync();
            try { await row; } catch (OperationCanceledException) { }
            cancellation.Dispose();
        }

        BitmapSource? settled = await gallery.RenderRowAsync(
            FaceKey.For(fonts[0], 0), TestCancellation);

        Assert.NotNull(settled);
    }

    [Fact]
    public async Task InvalidatingAFile_ForcesTheRowToBeDrawnAgain()
    {
        List<string> fonts = SeedCopies(1);

        using var service = new RenderService();
        using var preview = new PreviewViewModel(service);
        using var gallery = new GalleryViewModel(service, preview);

        var key = FaceKey.For(fonts[0], 0);
        BitmapSource? first = await gallery.RenderRowAsync(key, TestCancellation);

        gallery.Invalidate(fonts[0]);
        Assert.Equal(0, gallery.Cache.Count);

        BitmapSource? second = await gallery.RenderRowAsync(key, TestCancellation);

        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task TheGalleryRowHeight_FollowsTheSize()
    {
        using var service = new RenderService();
        using var preview = new PreviewViewModel(service);
        using var gallery = new GalleryViewModel(service, preview);

        gallery.GallerySize = 12;
        double small = gallery.RowHeight;

        gallery.GallerySize = 72;
        double large = gallery.RowHeight;

        Assert.True(large > small * 3, "row height should scale with the gallery size");
        await Task.CompletedTask;
    }
}
