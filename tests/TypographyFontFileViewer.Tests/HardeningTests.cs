using System.IO;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// The red-team probes, kept as regression tests now that each hole is shut.
/// </summary>
/// <remarks>
/// Each one broke the app when it was written. They are here so that a later change
/// reopening one of those holes fails the build rather than reaching a user.
/// </remarks>
public class HardeningTests : TempFolderTest
{
    public HardeningTests() : base("redteam") { }

    private static string NonSfntFont()
    {
        // Type 1, BDF, PCF and Windows FNT all go down TextLayout's FreeType path
        // rather than HarfBuzz, which is the path under test here.
        foreach (string extension in new[] { ".pfa", ".pfb", ".bdf", ".pcf", ".fon" })
        {
            IReadOnlyList<string> found = TestFonts.WithExtension(extension);
            if (found.Count > 0) return found[0];
        }

        Assert.Skip("No non-SFNT font in the corpus.");
        return string.Empty;
    }

    // --- RT probe: a lone surrogate in the preview text -------------------------

    /// <summary>
    /// A single unpaired high surrogate, which is what a paste or a hand-edited
    /// settings file can easily contain.
    /// </summary>
    [Fact]
    public async Task LoneSurrogate_ThroughTheFreeTypeShapingPath()
    {
        string path = NonSfntFont();
        using var service = new RenderService();

        string loneHigh = "\ud83d";          // high surrogate with nothing after it
        string loneLow = "A\udc00B";         // low surrogate with nothing before it

        Exception? first = await Record.ExceptionAsync(() => service.RenderTextAsync(
            FaceKey.For(path, 0), loneHigh, 24, 96, 0, RenderStyle.Light,
            cancellationToken: Tests.Cancellation));

        Exception? second = await Record.ExceptionAsync(() => service.RenderTextAsync(
            FaceKey.For(path, 0), loneLow, 24, 96, 0, RenderStyle.Light,
            cancellationToken: Tests.Cancellation));

        Assert.True(
            first is null && second is null,
            $"lone high surrogate -> {first?.GetType().Name ?? "ok"}; "
            + $"lone low surrogate -> {second?.GetType().Name ?? "ok"}");
    }

    /// <summary>The same text through HarfBuzz, as a control.</summary>
    [Fact]
    public async Task LoneSurrogate_ThroughHarfBuzz()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");
        using var service = new RenderService();

        Exception? thrown = await Record.ExceptionAsync(() => service.RenderTextAsync(
            FaceKey.For(path, 0), "\ud83dA\udc00", 24, 96, 0, RenderStyle.Light,
            cancellationToken: Tests.Cancellation));

        Assert.True(thrown is null, $"SFNT path threw {thrown?.GetType().Name}");
    }

    // --- RT probe: how large a canvas can one render ask for? -------------------

    /// <summary>
    /// The rasterizer caps each dimension at 16,384 but multiplies them together.
    /// </summary>
    [Fact]
    public async Task AVeryTallRender_DoesNotAskForAGigabyte()
    {
        string path = TestFonts.Require("Inter-Regular.ttf");
        using var service = new RenderService();

        // Narrow wrap width and a long string: every word becomes its own line.
        string many = string.Join(' ', Enumerable.Repeat("Wm", 4000));

        RenderedText rendered = await service.RenderTextAsync(
            FaceKey.For(path, 0), many, 288, 96, 40, RenderStyle.Light,
            cancellationToken: Tests.Cancellation);

        long bytes = (long)rendered.Width * rendered.Height * 4;
        Assert.True(bytes < 256L * 1024 * 1024,
            $"one render allocated {bytes / (1024 * 1024)} MB "
            + $"({rendered.Width}x{rendered.Height}) for {rendered.LineCount} lines");
    }

    // --- RT probe: does the hash cache ever let go? -----------------------------

    /// <summary>
    /// HashService remembered every path it had ever hashed, with no eviction.
    /// </summary>
    /// <remarks>
    /// Pushed past the bound on purpose: a thousand more files than the cache is
    /// allowed to hold, so the test fails both if there is no bound at all and if
    /// the bound exists but nothing ever trims to it.
    /// </remarks>
    [Fact]
    public async Task TheHashCache_IsBounded()
    {
        var hashes = new HashService();
        int overflow = HashService.MaximumEntries + 1000;

        for (int i = 0; i < overflow; i++)
        {
            string path = Path.Combine(Root, $"f{i:D5}.ttf");
            File.WriteAllBytes(path, [0x00, 0x01, 0x00, 0x00, (byte)i, (byte)(i >> 8)]);
            await hashes.ComputeAsync(path, Tests.Cancellation);
        }

        Assert.True(hashes.Count <= HashService.MaximumEntries,
            $"{overflow} files hashed and the cache holds {hashes.Count}, "
            + $"against a stated bound of {HashService.MaximumEntries}");

        // And the most recent ones are the ones kept, which is the point of an LRU.
        string newest = Path.Combine(Root, $"f{overflow - 1:D5}.ttf");
        int before = hashes.Count;
        await hashes.ComputeAsync(newest, Tests.Cancellation);

        Assert.Equal(before, hashes.Count);
    }

    // --- RT probe: a WOFF2 whose maxp disagrees with its glyf transform ---------

    /// <summary>
    /// The rebuilt loca is sized from the glyf transform's own glyph count; maxp is
    /// passed through untouched. If they disagree, loca is shorter than maxp claims.
    /// </summary>
    [Fact]
    public async Task AWoff2WhoseMaxpOutrunsItsGlyf_IsRejectedRatherThanRebuiltShort()
    {
        string source = TestFonts.Require("Inter-Regular.woff2");
        byte[] original = File.ReadAllBytes(source);

        byte[] decoded;
        try
        {
            decoded = TypographyFontFileViewer.Engine.Woff.Woff2Decoder.Decode(original, source);
        }
        catch (FontLoadException)
        {
            Assert.Skip("the corpus WOFF2 did not decode, so this probe has nothing to bend");
            return;
        }

        // Find maxp in the rebuilt font and inflate its numGlyphs by a thousand.
        int numTables = SfntReadUInt16(decoded, 4);
        int maxpRecord = -1;
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + i * 16;
            if (SfntReadUInt32(decoded, record) == 0x6D617870u) { maxpRecord = record; break; }
        }

        Assert.True(maxpRecord >= 0, "the rebuilt font has no maxp");

        int maxpAt = (int)SfntReadUInt32(decoded, maxpRecord + 8);
        int declared = SfntReadUInt16(decoded, maxpAt + 4);

        byte[] tampered = (byte[])decoded.Clone();
        int inflated = Math.Min(declared + 1000, ushort.MaxValue);
        tampered[maxpAt + 4] = (byte)(inflated >> 8);
        tampered[maxpAt + 5] = (byte)inflated;

        using TempFile temp = TempFile.With(".ttf", tampered);
        using var service = new RenderService();

        FaceSummary summary =
            (await service.ReadSummariesAsync(temp.Path, cancellationToken: Tests.Cancellation))[0];

        if (summary.IsBroken) return;   // refused, which is the wanted outcome

        // It opened. Ask it to draw every glyph index maxp now claims exist.
        Exception? thrown = await Record.ExceptionAsync(async () =>
        {
            IReadOnlyList<GlyphEntry> glyphs = await service.ListGlyphsAsync(
                FaceKey.For(temp.Path, 0), includeUnmapped: true,
                cancellationToken: Tests.Cancellation);

            foreach (GlyphEntry glyph in glyphs.TakeLast(50))
                await service.RenderGlyphCellsAsync(
                    FaceKey.For(temp.Path, 0), [glyph], 32, RenderStyle.Light,
                    cancellationToken: Tests.Cancellation);
        });

        Assert.True(thrown is null,
            $"a font whose maxp outruns its loca threw {thrown?.GetType().Name}: {thrown?.Message}");
    }

    // --- RT probe: a directory junction that points back at its own parent ------

    /// <summary>
    /// FolderScanner recurses with AttributesToSkip = Hidden | System, which does not
    /// include ReparsePoint, so a junction loop is walked rather than stepped over.
    /// </summary>
    /// <remarks>
    /// The MaximumFiles cap counts font files, so a loop holding none never reaches it.
    /// Junctions need no administrator rights, so any user or installer can leave one.
    /// </remarks>
    [Fact]
    public async Task AJunctionLoop_DoesNotTrapTheScanner()
    {
        string deep = Folder("deep");
        string link = Path.Combine(deep, "loop");

        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{Root}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            await mklink.WaitForExitAsync(Tests.Cancellation);
            if (mklink.ExitCode != 0) Assert.Skip("could not create a junction here");
        }

        File.WriteAllBytes(Path.Combine(Root, "One.ttf"), [0x00, 0x01, 0x00, 0x00]);

        var scanner = new FolderScanner();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            IReadOnlyList<FontFile> fonts = await scanner.ScanAsync(Root, true, budget.Token);
            Assert.True(fonts.Count < 1000, $"the loop produced {fonts.Count} rows for one font");
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("the scan did not finish within 20 seconds: the junction loop traps it");
        }
        finally
        {
            // Remove the junction before the base class deletes the tree, or the
            // recursive delete follows it.
            try { System.IO.Directory.Delete(link); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The same junction loop, reached the way a user reaches it: ticking Subfolders.
    /// </summary>
    /// <remarks>
    /// RescanAsync calls LoadFolderAsync directly, and LoadFolderAsync catches only
    /// OperationCanceledException, so whatever the scan throws comes out of the task
    /// the toggle started.
    /// </remarks>
    [Fact]
    public async Task TickingSubfolders_OnAFolderHoldingAJunctionLoop()
    {
        string fonts = Folder("fonts");
        string deep = Path.Combine(fonts, "deep");
        System.IO.Directory.CreateDirectory(deep);

        string link = Path.Combine(deep, "loop");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{fonts}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            await mklink.WaitForExitAsync(Tests.Cancellation);
            if (mklink.ExitCode != 0) Assert.Skip("could not create a junction here");
        }

        try
        {
            SeedCorpusFonts(fonts, "Inter-Regular.ttf");

            using var render = new RenderService();
            using var model = new ViewModels.MainViewModel(render);

            await model.OpenPathAsync(fonts, Tests.Cancellation);
            Assert.Single(model.Fonts);

            model.IncludeSubfolders = true;                 // Ctrl+Shift+S
            Exception? thrown = await Record.ExceptionAsync(() => model.PendingRescan);

            Assert.True(thrown is null,
                $"ticking Subfolders threw {thrown?.GetType().Name}, which nothing above it catches");
        }
        finally
        {
            try { System.IO.Directory.Delete(link); } catch (IOException) { }
        }
    }

    // --- RT probe: a hand-edited settings file ---------------------------------

    /// <summary>
    /// The settings file sits beside the exe in plain JSON and is meant to be editable.
    /// Its doubles are clamped on load, but Math.Clamp returns NaN for NaN.
    /// </summary>
    [Fact]
    public void NonFiniteSizesInTheSettingsFile_AreNotLetThrough()
    {
        string path = Path.Combine(Root, "TypographyFontFileViewer.json");

        // System.Text.Json writes NaN and Infinity as strings when asked; a person
        // editing the file by hand writes whatever they like.
        File.WriteAllText(path, """
            {
              "PreviewPointSize": "NaN",
              "GallerySize": "Infinity"
            }
            """);

        var settings = new SettingsService(path);
        settings.Load();

        using var render = new RenderService();
        using var model = new ViewModels.MainViewModel(render);

        model.ApplySettings(settings.Current);

        Assert.True(double.IsFinite(model.Preview.PointSize),
            $"preview point size is {model.Preview.PointSize}");
        Assert.True(double.IsFinite(model.Gallery.GallerySize),
            $"gallery size is {model.Gallery.GallerySize}");
        Assert.True(double.IsFinite(model.Gallery.RowHeight),
            $"gallery row height is {model.Gallery.RowHeight}, which WPF lays out with");
    }

    /// <summary>
    /// The settings file cannot smuggle a lone surrogate into the preview text.
    /// </summary>
    /// <remarks>
    /// Worth pinning down rather than assuming: the remembered text is replayed into
    /// the renderer on the next run, so if JSON let an unpaired surrogate through it
    /// would reach <c>TextLayout</c> on startup with no user action at all. It does
    /// not — System.Text.Json rejects the escape, the load is quarantined, and the
    /// defaults come back. That is what closes the worst route into RT-02.
    /// </remarks>
    [Fact]
    public void ALoneSurrogateInTheSettingsFile_IsRejectedByTheJsonReader()
    {
        string path = Path.Combine(Root, "TypographyFontFileViewer.json");
        File.WriteAllText(path, "{ \"PreviewText\": \"\\ud83d\" }");

        var settings = new SettingsService(path);
        settings.Load();

        Assert.Null(settings.Current.PreviewText);
        Assert.False(string.IsNullOrWhiteSpace(settings.LastProblem));
        Assert.True(File.Exists(path + ".bad"), "the unreadable file should be set aside");
    }

    // --- RT-07: a path cannot escape its own argument ---------------------------

    /// <summary>
    /// The shape MainViewModel.RevealInExplorer now uses: one path, one argument,
    /// whatever the path contains.
    /// </summary>
    /// <remarks>
    /// Windows forbids a quote in a file name, so the old concatenated command line
    /// was not reachable — but this pins the mechanism rather than the file system's
    /// politeness, and it proves <c>UseShellExecute = false</c> still finds a program
    /// by name, which was the real risk in the change.
    /// </remarks>
    [Fact]
    public async Task APathWithAQuoteInIt_StaysOneArgument()
    {
        const string Awkward = @"C:\a""b\c d\e.ttf";

        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("echo");
        start.ArgumentList.Add($"/select,{Awkward}");

        using var child = System.Diagnostics.Process.Start(start)!;
        string echoed = (await child.StandardOutput.ReadToEndAsync(Tests.Cancellation)).Trim();
        await child.WaitForExitAsync(Tests.Cancellation);

        // cmd's echo prints the raw command-line tail rather than its parsed argv,
        // which is the more useful view here: it shows what ArgumentList composed.
        // The whole thing is one quoted token and the inner quote is escaped, so it
        // cannot end the argument. The old concatenation produced a bare quote here,
        // which would have ended the token and handed the rest to Explorer as further
        // arguments.
        Assert.Equal("\"/select,C:\\a\\\"b\\c d\\e.ttf\"", echoed);

        Assert.DoesNotContain("\" ", echoed[1..^1]);   // no token boundary inside it
    }

    private static ushort SfntReadUInt16(byte[] b, int at) => (ushort)((b[at] << 8) | b[at + 1]);

    private static uint SfntReadUInt32(byte[] b, int at)
        => ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];
}
