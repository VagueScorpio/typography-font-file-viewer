namespace TypographyFontFileViewer.Core;

/// <summary>
/// One font in the <c>Favorite Fonts</c> folder, as the index remembers it.
/// </summary>
/// <remarks>
/// The index is a cache, not the truth. The files in the folder are the truth: the user
/// may drop fonts in or delete them by hand, and the app reconciles on startup rather
/// than assuming the index is right.
/// </remarks>
public sealed record FavoriteEntry
{
    /// <summary>The file's name inside the favourites folder, which may be renamed on collision.</summary>
    public required string FileName { get; init; }

    /// <summary>SHA-256 of the file's contents, lower-case hex. This is the identity.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Size in bytes, used to rule out non-matches without hashing them.</summary>
    public required long Size { get; init; }

    /// <summary>Companion metric files copied alongside a Type 1 font.</summary>
    public IReadOnlyList<string> Companions { get; init; } = [];

    /// <summary>Where it was favourited from, for the user's reference only.</summary>
    public string? OriginalPath { get; init; }

    public DateTime AddedUtc { get; init; } = DateTime.UtcNow;
}
