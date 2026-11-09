namespace TypographyFontFileViewer.Core;

/// <summary>
/// What the font list needs to show a row: enough to label the face, without the cost
/// of reading the whole name table.
/// </summary>
/// <remarks>
/// A file that fails to open still produces a summary, with <see cref="Error"/> set.
/// That is how a broken font becomes a row with a warning rather than a missing entry
/// or a crash (requirement R10).
/// </remarks>
public sealed record FaceSummary
{
    public required string Path { get; init; }

    /// <summary>Which face inside the file; 0 for everything that is not a collection.</summary>
    public required int FaceIndex { get; init; }

    /// <summary>How many faces the file holds. 1 for ordinary fonts.</summary>
    public required int FaceCount { get; init; }

    public required FontFormat Format { get; init; }

    public string Family { get; init; } = string.Empty;

    public string Style { get; init; } = string.Empty;

    public long GlyphCount { get; init; }

    /// <summary>Available pixel sizes for bitmap-only fonts; empty for scalable ones.</summary>
    public IReadOnlyList<int> FixedSizes { get; init; } = [];

    public bool IsScalable { get; init; }

    public bool IsVariable { get; init; }

    /// <summary>Set when this face could not be opened; the row shows a warning instead.</summary>
    public string? Error { get; init; }

    public bool IsBroken => Error is not null;

    /// <summary>What the list shows: "Family Style", falling back to the file name.</summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Family))
                return System.IO.Path.GetFileName(Path);

            return string.IsNullOrWhiteSpace(Style) || Style.Equals("Regular", StringComparison.OrdinalIgnoreCase)
                ? Family
                : $"{Family} {Style}";
        }
    }

    /// <summary>"(2/3)" for collection faces, empty otherwise.</summary>
    public string FaceLabel => FaceCount > 1 ? $"({FaceIndex + 1}/{FaceCount})" : string.Empty;

    /// <summary>Builds the summary shown for a file that would not open.</summary>
    public static FaceSummary Broken(string path, FontFormat format, string error) => new()
    {
        Path = path,
        FaceIndex = 0,
        FaceCount = 1,
        Format = format,
        Error = error,
    };
}
