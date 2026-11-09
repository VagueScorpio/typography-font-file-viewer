using CommunityToolkit.Mvvm.ComponentModel;
using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.ViewModels;

/// <summary>
/// One row in the font list: one face, of one file.
/// </summary>
/// <remarks>
/// A row exists as soon as the scanner has seen the file, showing its name. Family,
/// style and glyph count arrive later from the background metadata pass, and a
/// collection turns into several rows at that point.
/// </remarks>
public sealed partial class FontItemViewModel : ObservableObject
{
    public FontItemViewModel(FontFile file, int faceIndex = 0, int faceCount = 1)
    {
        File = file;
        FaceIndex = faceIndex;
        FaceCount = faceCount;
    }

    public FontFile File { get; }

    /// <summary>Which face inside the file; 0 for everything that is not a collection.</summary>
    public int FaceIndex { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FaceLabel))]
    public partial int FaceCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string Family { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string Style { get; set; } = string.Empty;

    [ObservableProperty]
    public partial long GlyphCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBroken))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    public partial string? Error { get; set; }

    /// <summary>False until the metadata pass has reached this row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial bool MetadataLoaded { get; set; }

    /// <summary>The file name until the family name is known, then the family and style.</summary>
    public string DisplayName
    {
        get
        {
            if (!MetadataLoaded || string.IsNullOrWhiteSpace(Family)) return File.FileName;

            return string.IsNullOrWhiteSpace(Style)
                || Style.Equals("Regular", StringComparison.OrdinalIgnoreCase)
                ? Family
                : $"{Family} {Style}";
        }
    }

    public string FormatBadge => File.Format.Badge();

    /// <summary>"(2/3)" for a face inside a collection, empty otherwise.</summary>
    public string FaceLabel => FaceCount > 1 ? $"({FaceIndex + 1}/{FaceCount})" : string.Empty;

    public bool IsBroken => Error is not null;

    public string Tooltip => Error is not null
        ? $"{File.Path}\n\n{Error}"
        : File.Path;

    /// <summary>Identifies this face to the engine, including the file's state on disk.</summary>
    public FaceKey Key => new(File.Path, FaceIndex, File.Length, File.LastWriteUtc);

    /// <summary>Fills the row in from a summary the engine produced.</summary>
    public void Apply(FaceSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        FaceCount = summary.FaceCount;
        Family = summary.Family;
        Style = summary.Style;
        GlyphCount = summary.GlyphCount;
        Error = summary.Error;
        MetadataLoaded = true;
    }

    public override string ToString() => DisplayName;
}
