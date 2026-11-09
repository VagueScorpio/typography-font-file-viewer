using System.IO;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// Finds the local font corpus. It is git-ignored and built on demand, so every test
/// that needs it skips with a clear message when it is not there.
/// </summary>
internal static class TestFonts
{
    private static readonly Lazy<string?> Directory = new(FindCorpusDirectory);

    /// <summary>The tests/fonts folder, or null when the corpus has not been built.</summary>
    public static string? CorpusDirectory => Directory.Value;

    private const string BuildInstruction =
        "The font corpus is missing. Build it with:\n" +
        "  tools\\.venv\\Scripts\\python.exe tools\\make-test-fonts.py\n" +
        "See tools/README.md.";

    /// <summary>Returns the corpus folder, or skips the calling test.</summary>
    public static string RequireCorpus()
    {
        string? directory = CorpusDirectory;
        if (directory is null) Assert.Skip(BuildInstruction);
        return directory!;
    }

    /// <summary>Returns one corpus font by file name, or skips the calling test.</summary>
    public static string Require(string fileName)
    {
        string directory = RequireCorpus();
        string path = Path.Combine(directory, fileName);

        if (!File.Exists(path))
            Assert.Skip($"{fileName} is not in the corpus. {BuildInstruction}");

        return path;
    }

    /// <summary>Every font file in the corpus, excluding the folder's own README.</summary>
    public static IReadOnlyList<string> All()
    {
        string? directory = CorpusDirectory;
        if (directory is null) return [];

        return [.. System.IO.Directory
            .EnumerateFiles(directory)
            .Where(path => !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).StartsWith('.'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Corpus fonts with any of the given extensions.</summary>
    public static IReadOnlyList<string> WithExtension(params string[] extensions)
    {
        var wanted = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        return [.. All().Where(path => wanted.Contains(Path.GetExtension(path)))];
    }

    /// <summary>A Windows system font, for tests that should not depend on the corpus.</summary>
    public static string RequireSystemFont()
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

    /// <summary>An optional Windows system font, or null when it is not installed.</summary>
    public static string? OptionalSystemFont(string fileName)
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Walks up from the test binaries to the repository's tests/fonts folder.</summary>
    private static string? FindCorpusDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "tests", "fonts");
            if (System.IO.Directory.Exists(candidate)
                && System.IO.Directory.EnumerateFiles(candidate, "*.ttf").Any())
                return candidate;

            current = current.Parent;
        }

        return null;
    }
}
