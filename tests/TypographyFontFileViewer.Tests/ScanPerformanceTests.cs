using System.Diagnostics;
using System.IO;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// The blueprint's performance target: a folder of 500 fonts shows its file list in
/// under a second, with the names filling in afterwards.
/// </summary>
/// <remarks>
/// The thresholds here are deliberately loose — several times the target — because a
/// test machine under load is not a user's machine. They are here to catch a change
/// that makes scanning slow by an order of magnitude, not to measure milliseconds.
/// </remarks>
public class ScanPerformanceTests : IDisposable
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), $"tffv-perf-{Guid.NewGuid():N}");

    public ScanPerformanceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Makes a folder of small, real font files by copying one many times.</summary>
    private int Seed(int count)
    {
        string source = Path.Combine(TestFonts.RequireCorpus(), "Inter-Regular.ttf");
        if (!File.Exists(source)) Assert.Skip("Inter-Regular.ttf is not in the corpus.");

        byte[] bytes = File.ReadAllBytes(source);
        for (int i = 0; i < count; i++)
            File.WriteAllBytes(Path.Combine(_folder, $"Font{i:D4}.ttf"), bytes);

        return count;
    }

    [Fact]
    public async Task Scanning500Fonts_IsQuick()
    {
        int count = Seed(500);

        var scanner = new FolderScanner();

        // Warm the directory metadata so this measures the scan, not the first stat of
        // a folder the file system has never seen.
        await scanner.ScanAsync(_folder, false, TestCancellation);

        var timer = Stopwatch.StartNew();
        var fonts = await scanner.ScanAsync(_folder, false, TestCancellation);
        timer.Stop();

        Assert.Equal(count, fonts.Count);
        Assert.True(timer.ElapsedMilliseconds < 3000,
            $"Scanning {count} fonts took {timer.ElapsedMilliseconds} ms; the target is under 1000.");
    }

    /// <summary>
    /// The blueprint wants a preview re-rendered within about 50 ms of a keystroke.
    /// </summary>
    /// <remarks>
    /// Measured after the face is open, which is the case that matters: the user is
    /// typing into the preview box for a font already on screen.
    /// </remarks>
    [Fact]
    public async Task RenderingAPreviewAfterAKeystroke_IsQuick()
    {
        string font = TestFonts.RequireSystemFont();

        using var service = new RenderService();
        var key = FaceKey.For(font, 0);

        // Warm: open the face and let the first render pay for it.
        await service.RenderTextAsync(key, "warm up", 36, 96, 800, RenderStyle.Light,
            cancellationToken: TestCancellation);

        var measurements = new List<long>();
        string text = "The quick brown fox";

        for (int i = 1; i <= text.Length; i++)
        {
            var timer = Stopwatch.StartNew();
            await service.RenderTextAsync(key, text[..i], 36, 96, 800, RenderStyle.Light,
                cancellationToken: TestCancellation);
            timer.Stop();
            measurements.Add(timer.ElapsedMilliseconds);
        }

        measurements.Sort();
        long median = measurements[measurements.Count / 2];

        // Several times the 50 ms target: this is a guard against an order-of-magnitude
        // regression, not a benchmark.
        Assert.True(median < 200, $"the median re-render took {median} ms");
    }

    [Fact]
    public async Task MovingToTheNextFont_ShowsItQuickly()
    {
        int count = Seed(40);

        using var service = new RenderService();
        var timings = new List<long>();

        for (int i = 0; i < count; i++)
        {
            var key = FaceKey.For(Path.Combine(_folder, $"Font{i:D4}.ttf"), 0);

            var timer = Stopwatch.StartNew();
            await service.RenderTextAsync(key, "Next font", 36, 96, 800, RenderStyle.Light,
                cancellationToken: TestCancellation);
            timer.Stop();

            timings.Add(timer.ElapsedMilliseconds);
        }

        timings.Sort();
        long median = timings[timings.Count / 2];

        // Each step opens a font that has never been seen, which is the slow case.
        Assert.True(median < 400, $"the median step to a new font took {median} ms");
    }

    [Fact]
    public async Task Scanning_IsNotDominatedByOpeningEveryFile()
    {
        Seed(400);

        // What the scan costs over and above simply listing the directory.
        var enumerate = Stopwatch.StartNew();
        int listed = Directory.EnumerateFiles(_folder, "*").Count();
        enumerate.Stop();

        var scanner = new FolderScanner();
        await scanner.ScanAsync(_folder, false, TestCancellation);

        var scan = Stopwatch.StartNew();
        var fonts = await scanner.ScanAsync(_folder, false, TestCancellation);
        scan.Stop();

        Assert.Equal(listed, fonts.Count);
        Assert.True(scan.ElapsedMilliseconds < Math.Max(2000, enumerate.ElapsedMilliseconds * 40),
            $"Listing took {enumerate.ElapsedMilliseconds} ms but scanning took "
            + $"{scan.ElapsedMilliseconds} ms — the per-file work has grown out of proportion.");
    }
}
