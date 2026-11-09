namespace TypographyFontFileViewer.Core;

/// <summary>What a font may say about embedding, from the OS/2 fsType field.</summary>
public enum EmbeddingPermission
{
    Unknown = 0,
    Installable,
    Restricted,
    PreviewAndPrint,
    Editable,
}

/// <summary>One variation axis of a variable font.</summary>
/// <param name="Tag">Four-character axis tag, such as <c>wght</c>.</param>
public readonly record struct VariationAxis(string Tag, string Name, double Minimum, double Default, double Maximum)
{
    public override string ToString() => $"{Name} ({Tag}) {Minimum:0.##}–{Maximum:0.##}, default {Default:0.##}";
}

/// <summary>
/// Everything the Info tab shows for one face. Fields the font does not supply stay
/// null or empty rather than being invented.
/// </summary>
public sealed record FontInfo
{
    public required string Path { get; init; }
    public required int FaceIndex { get; init; }
    public required FontFormat Container { get; init; }

    /// <summary>What FreeType calls the outline format: TrueType, CFF, Type 1, Windows FNT, BDF, PCF.</summary>
    public string OutlineFormat { get; init; } = string.Empty;

    // --- Name table (IDs from the blueprint, section 6.7) ---
    public string? Copyright { get; init; }          // 0
    public string? Family { get; init; }             // 16 preferred, else 1
    public string? Subfamily { get; init; }          // 17 preferred, else 2
    public string? UniqueId { get; init; }           // 3
    public string? FullName { get; init; }           // 4
    public string? Version { get; init; }            // 5
    public string? PostScriptName { get; init; }     // 6
    public string? Trademark { get; init; }          // 7
    public string? Manufacturer { get; init; }       // 8
    public string? Designer { get; init; }           // 9
    public string? Description { get; init; }        // 10
    public string? VendorUrl { get; init; }          // 11
    public string? DesignerUrl { get; init; }        // 12
    public string? License { get; init; }            // 13
    public string? LicenseUrl { get; init; }         // 14

    // --- Counts and metrics ---
    public long GlyphCount { get; init; }
    public int UnitsPerEm { get; init; }
    public int FaceCount { get; init; }
    public int MappedCharacterCount { get; init; }
    public int CharmapCount { get; init; }
    public short Ascender { get; init; }
    public short Descender { get; init; }
    public short LineHeight { get; init; }

    public bool IsScalable { get; init; }
    public bool HasKerning { get; init; }
    public bool HasGlyphNames { get; init; }
    public bool HasColor { get; init; }
    public bool IsFixedWidth { get; init; }

    public IReadOnlyList<int> FixedSizes { get; init; } = [];

    public EmbeddingPermission Embedding { get; init; }

    /// <summary>Axes of a variable font. v1.0 lists them and previews the default instance.</summary>
    public IReadOnlyList<VariationAxis> VariationAxes { get; init; } = [];

    public IReadOnlyList<string> NamedInstances { get; init; } = [];

    // --- File facts ---
    public long FileLength { get; init; }
    public DateTime LastWriteUtc { get; init; }

    /// <summary>Filled in on demand by the Info tab; hashing a 30 MB font is not free.</summary>
    public string? Sha256 { get; init; }

    public bool IsVariable => VariationAxes.Count > 0;
}
