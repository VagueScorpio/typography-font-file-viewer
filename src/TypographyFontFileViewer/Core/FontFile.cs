using System.IO;

namespace TypographyFontFileViewer.Core;

/// <summary>
/// One font file found on disk, before anything has been read out of it.
/// </summary>
/// <remarks>
/// The scanner produces these from directory entries alone, so a folder's worth of rows
/// can appear immediately. Family names, styles and face counts arrive later, from the
/// background metadata pass.
/// </remarks>
public sealed record FontFile
{
    public required string Path { get; init; }

    public required FontFormat Format { get; init; }

    public required long Length { get; init; }

    public required DateTime LastWriteUtc { get; init; }

    /// <summary>Matching .afm / .pfm / .inf files, for Type 1 fonts only.</summary>
    public IReadOnlyList<string> Companions { get; init; } = [];

    public string FileName => System.IO.Path.GetFileName(Path);

    public string? Folder => System.IO.Path.GetDirectoryName(Path);
}

/// <summary>
/// Orders file names the way a person reads them, so "Font2" comes before "Font10".
/// </summary>
/// <remarks>
/// Plain string ordering puts "Font10" first, because '1' sorts before '2'. Font
/// families are numbered often enough — weights, optical sizes, numbered variants —
/// that the wrong order is noticeable immediately.
/// </remarks>
public sealed class NaturalFileNameComparer : IComparer<string>
{
    public static NaturalFileNameComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        int i = 0, j = 0;

        while (i < left.Length && j < right.Length)
        {
            char a = left[i];
            char b = right[j];

            if (char.IsAsciiDigit(a) && char.IsAsciiDigit(b))
            {
                // Compare the runs of digits as numbers, not character by character.
                int startA = i, startB = j;
                while (i < left.Length && char.IsAsciiDigit(left[i])) i++;
                while (j < right.Length && char.IsAsciiDigit(right[j])) j++;

                ReadOnlySpan<char> numberA = left.AsSpan(startA, i - startA).TrimStart('0');
                ReadOnlySpan<char> numberB = right.AsSpan(startB, j - startB).TrimStart('0');

                if (numberA.Length != numberB.Length)
                    return numberA.Length - numberB.Length;

                int digits = numberA.SequenceCompareTo(numberB);
                if (digits != 0) return digits;

                continue;
            }

            int letters = char.ToUpperInvariant(a).CompareTo(char.ToUpperInvariant(b));
            if (letters != 0) return letters;

            i++;
            j++;
        }

        return (left.Length - i) - (right.Length - j);
    }
}
