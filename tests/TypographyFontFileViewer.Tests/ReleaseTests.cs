using System.IO;
using System.Text.RegularExpressions;
using TypographyFontFileViewer.Core;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// Guards the blueprint's standing conventions (Section 16) against the one mistake they
/// invite: bumping the version in one place and forgetting the others.
/// </summary>
/// <remarks>
/// These read the repository's own files, so they skip rather than fail when run from a
/// published binary with no source tree beside it.
/// </remarks>
public class ReleaseTests
{
    /// <summary>The repository root, found by walking up to the solution file.</summary>
    private static string RequireRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TypographyFontFileViewer.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        Assert.Skip("No repository checkout above the test binaries.");
        return string.Empty;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        string path = Path.Combine(RequireRepositoryRoot(), relativePath);
        if (!File.Exists(path)) Assert.Skip($"{relativePath} is not in the checkout.");
        return File.ReadAllText(path);
    }

    /// <summary>The csproj's Version, which is the one authoritative copy of the number.</summary>
    private static string ProjectVersion()
    {
        string csproj = ReadRepositoryFile(
            Path.Combine("src", "TypographyFontFileViewer", "TypographyFontFileViewer.csproj"));

        Match match = Regex.Match(csproj, @"<Version>([^<]+)</Version>");
        Assert.True(match.Success, "The csproj has no <Version> element.");
        return match.Groups[1].Value.Trim();
    }

    [Fact]
    public void TheVersionShownToTheUser_MatchesTheProjectFile()
    {
        string project = ProjectVersion();       // "1.0.0"
        string displayed = AppInfo.Version;      // "1.0"

        // The displayed form drops a zero patch field, so compare field by field rather
        // than as strings.
        Version expected = Version.Parse(project);
        Version actual = Version.Parse(displayed);

        Assert.Equal(expected.Major, actual.Major);
        Assert.Equal(expected.Minor, actual.Minor);
        Assert.Equal(Math.Max(expected.Build, 0), Math.Max(actual.Build, 0));
    }

    [Fact]
    public void TheChangelog_HasAnEntryForTheCurrentVersion()
    {
        Version version = Version.Parse(ProjectVersion());
        string wanted = $"v{version.Major}.{version.Minor}";

        string changelog = ReadRepositoryFile("CHANGELOG.txt");

        Assert.Contains(wanted, changelog, StringComparison.Ordinal);
    }

    /// <summary>
    /// The FreeType License asks for its credit line wherever the software is documented.
    /// It belongs in the README and in Help then About; both are easy to lose in an edit.
    /// </summary>
    [Fact]
    public void TheFreeTypeCredit_IsInTheReadme()
    {
        string readme = ReadRepositoryFile("README.md");

        Assert.Contains("The FreeType Project", readme, StringComparison.Ordinal);
        Assert.Contains("www.freetype.org", readme, StringComparison.Ordinal);
        Assert.Contains("FreeType License", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFreeTypeCredit_IsInTheAboutText()
    {
        Assert.Contains("The FreeType Project", AppInfo.FreeTypeCredit, StringComparison.Ordinal);
        Assert.Contains("www.freetype.org", AppInfo.FreeTypeCredit, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every package the app ships has to be listed in the README with its licence.
    /// </summary>
    [Fact]
    public void EveryPackageReference_IsListedInTheReadme()
    {
        string csproj = ReadRepositoryFile(
            Path.Combine("src", "TypographyFontFileViewer", "TypographyFontFileViewer.csproj"));
        string readme = ReadRepositoryFile("README.md");

        foreach (Match match in Regex.Matches(csproj, @"PackageReference Include=""([^""]+)"""))
        {
            string package = match.Groups[1].Value;

            // The native-assets packages ship the same library as their managed binding,
            // which is the name the README lists.
            string listed = package.Replace(".NativeAssets.Win32", string.Empty);

            Assert.Contains(listed, readme, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The README's format table should name every extension the scanner accepts, so the
    /// documented list cannot quietly fall behind the code.
    /// </summary>
    [Fact]
    public void EverySupportedExtension_IsInTheReadmeTable()
    {
        string readme = ReadRepositoryFile("README.md");

        foreach (string extension in FormatSniffer.SupportedExtensions)
            Assert.Contains($"`{extension}`", readme, StringComparison.OrdinalIgnoreCase);
    }
}
