namespace TypographyFontFileViewer.Core;

/// <summary>How the font list is ordered.</summary>
public enum SortOrder
{
    /// <summary>By file name, in natural order, so "Font2" precedes "Font10".</summary>
    FileName = 0,

    /// <summary>By family then style, which groups a family's weights together.</summary>
    FamilyName = 1,

    /// <summary>By format, which gathers all the WOFF2s or all the Type 1s.</summary>
    Format = 2,
}
