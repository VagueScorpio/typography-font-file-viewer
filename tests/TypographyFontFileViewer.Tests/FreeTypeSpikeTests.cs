using System.IO;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Engine;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// Phase 0 acceptance: FreeType opens a real font, HarfBuzz shapes through it, the
/// result rasterizes to pixels, bad input raises <see cref="FontLoadException"/>
/// rather than taking the process down, and nothing is left locked on disk.
/// </summary>
public class FreeTypeSpikeTests
{
    /// <summary>A font that exists on any Windows install; tests skip when none is found.</summary>
    private static string RequireSystemFont()
    {
        string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string candidate in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf", "verdana.ttf" })
        {
            string path = Path.Combine(fontsFolder, candidate);
            if (File.Exists(path)) return path;
        }

        Assert.Skip($"No standard TrueType font found in {fontsFolder}.");
        return string.Empty;
    }

    [Fact]
    public void ReadFaceSummary_ReportsFamilyAndGlyphCount()
    {
        string font = RequireSystemFont();

        var summary = FreeTypeSpike.ReadFaceSummary(font);

        Assert.False(string.IsNullOrWhiteSpace(summary.Family));
        Assert.True(summary.GlyphCount > 100, $"Expected a real glyph count, got {summary.GlyphCount}.");
    }

    [Fact]
    public void Render_ProducesABitmapWithInk()
    {
        string font = RequireSystemFont();

        RenderedBitmap rendered = FreeTypeSpike.Render(font, "Hello", pixelSize: 72);

        Assert.True(rendered.Width > 0 && rendered.Height > 0);
        Assert.Equal(rendered.Width * 4, rendered.Stride);
        Assert.Equal(rendered.Stride * rendered.Height, rendered.Bgra.Length);

        // Five characters of Latin text shape to five glyphs.
        Assert.Equal(5, rendered.GlyphCount);

        // Something must actually be darker than the white background, or we rendered
        // an empty rectangle and would never know.
        int inkPixels = 0;
        for (int i = 0; i < rendered.Bgra.Length; i += 4)
            if (rendered.Bgra[i] < 200) inkPixels++;

        Assert.True(inkPixels > 50, $"Expected rendered ink, found {inkPixels} dark pixels.");
    }

    [Fact]
    public void Render_AtLargerSize_ProducesALargerBitmap()
    {
        string font = RequireSystemFont();

        RenderedBitmap small = FreeTypeSpike.Render(font, "Hello", pixelSize: 24);
        RenderedBitmap large = FreeTypeSpike.Render(font, "Hello", pixelSize: 96);

        Assert.True(large.Width > small.Width);
        Assert.True(large.Height > small.Height);
    }

    [Fact]
    public void Render_BlankText_ReturnsAnEmptyBitmapRatherThanThrowing()
    {
        string font = RequireSystemFont();

        RenderedBitmap rendered = FreeTypeSpike.Render(font, "   ", pixelSize: 32);

        Assert.True(rendered.Width > 0 && rendered.Height > 0);
        for (int i = 0; i < rendered.Bgra.Length; i += 4)
            Assert.Equal(0xFF, rendered.Bgra[i]);  // background only
    }

    [Fact]
    public void Render_DoesNotLockTheFile()
    {
        string font = RequireSystemFont();
        string copy = Path.Combine(Path.GetTempPath(), $"tffv-lock-{Guid.NewGuid():N}.ttf");
        File.Copy(font, copy);

        try
        {
            RenderedBitmap rendered = FreeTypeSpike.Render(copy, "Hello", pixelSize: 48);
            Assert.True(rendered.Bgra.Length > 0);

            // The whole favourites design rests on this: delete it while it is "open".
            File.Delete(copy);
            Assert.False(File.Exists(copy));
        }
        finally
        {
            if (File.Exists(copy)) File.Delete(copy);
        }
    }

    [Fact]
    public void Render_GarbageFile_ThrowsFontLoadException()
    {
        string junk = Path.Combine(Path.GetTempPath(), $"tffv-junk-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(junk, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());

        try
        {
            var ex = Assert.Throws<FontLoadException>(() => FreeTypeSpike.Render(junk, "Hello", 48));
            Assert.Equal(junk, ex.Path);
        }
        finally
        {
            File.Delete(junk);
        }
    }

    [Fact]
    public void Render_EmptyFile_ThrowsFontLoadException()
    {
        string empty = Path.Combine(Path.GetTempPath(), $"tffv-empty-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(empty, []);

        try
        {
            Assert.Throws<FontLoadException>(() => FreeTypeSpike.Render(empty, "Hello", 48));
        }
        finally
        {
            File.Delete(empty);
        }
    }

    [Fact]
    public void Render_MissingFile_ThrowsFontLoadException()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"tffv-missing-{Guid.NewGuid():N}.ttf");

        Assert.Throws<FontLoadException>(() => FreeTypeSpike.Render(missing, "Hello", 48));
    }
}
