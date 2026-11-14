using System.Reflection;

namespace TypographyFontFileViewer.Core;

/// <summary>
/// The app's name and version, read from the assembly rather than written out by hand.
/// </summary>
/// <remarks>
/// The standing convention is that every change bumps the version in the csproj, the
/// title bar and the zip name. Three hand-maintained copies of one number drift, and a
/// title bar claiming the wrong version is the kind of thing nobody notices until a bug
/// report quotes it. Only the csproj's <c>&lt;Version&gt;</c> is authoritative; this
/// class reads it, and the title bar and About box ask this class.
/// </remarks>
public static class AppInfo
{
    public const string Name = "Typography Font File Viewer";

    /// <summary>The version as a person would say it: "1.0", or "1.0.3" if there is a patch.</summary>
    public static string Version { get; } = ReadVersion();

    public static string NameAndVersion => $"{Name} {Version}";

    private static string ReadVersion()
    {
        // AssemblyVersion is always four fields, whatever the csproj said, so an
        // unspecified patch field reads as 0 and is left off rather than shown as ".0".
        Version? version = typeof(AppInfo).Assembly.GetName().Version;
        if (version is null) return "1.0";

        return version.Build > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    /// <summary>
    /// The FreeType License credit line, required wherever the app names its components.
    /// </summary>
    public const string FreeTypeCredit =
        "Portions of this software are copyright \u00a9 2026 The FreeType Project\n"
        + "(www.freetype.org). All rights reserved.";
}
