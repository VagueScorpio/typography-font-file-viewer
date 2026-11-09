using System.IO;
using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Services;

/// <summary>
/// Lists the font files in a folder.
/// </summary>
/// <remarks>
/// This is what turns "the user opened one font" into "here is the whole folder"
/// (requirement R2). It touches directory entries only — no file is opened and nothing
/// is decoded — so a folder of two thousand fonts produces a list immediately and the
/// names fill in afterwards from the background metadata pass.
/// </remarks>
public sealed class FolderScanner
{
    /// <summary>A folder with more files than this is truncated rather than hung on.</summary>
    public const int MaximumFiles = 50_000;

    /// <summary>
    /// Finds every supported font in <paramref name="folder"/>.
    /// </summary>
    /// <param name="folder">The folder to list.</param>
    /// <param name="includeSubfolders">Whether to descend into subfolders.</param>
    public Task<IReadOnlyList<FontFile>> ScanAsync(
        string folder, bool includeSubfolders, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);

        // Directory enumeration is blocking and can be slow on a network share, so it
        // belongs off the UI thread.
        return Task.Run<IReadOnlyList<FontFile>>(
            () => Scan(folder, includeSubfolders, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<FontFile> Scan(
        string folder, bool includeSubfolders, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder)) return [];

        var options = new EnumerationOptions
        {
            // A folder the user cannot read is skipped, not fatal: one locked
            // subdirectory must not lose them the rest of the scan.
            IgnoreInaccessible = true,
            RecurseSubdirectories = includeSubfolders,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false,
        };

        var fonts = new List<FontFile>(256);

        // Companions are collected per folder so a Type 1 font can be matched with its
        // metrics without stat-ing the disk again for every candidate.
        var companionsByFolder = new Dictionary<string, Dictionary<string, List<string>>>(
            StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(folder, "*", options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var candidates = new List<string>(256);

        foreach (string path in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(path);
            string extension = Path.GetExtension(fileName);

            if (FormatSniffer.IsCompanionExtension(extension))
            {
                RecordCompanion(companionsByFolder, path);
                continue;
            }

            if (!FormatSniffer.IsSupportedFileName(fileName)) continue;

            candidates.Add(path);
            if (candidates.Count >= MaximumFiles) break;
        }

        foreach (string path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;   // vanished or locked between enumeration and stat
            }

            FontFormat format = SniffWithoutReadingMuch(path, info.Length);

            fonts.Add(new FontFile
            {
                Path = path,
                Format = format,
                Length = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                Companions = format == FontFormat.Type1
                    ? FindCompanions(companionsByFolder, path)
                    : [],
            });
        }

        fonts.Sort(static (left, right) =>
            NaturalFileNameComparer.Instance.Compare(left.FileName, right.FileName));

        return fonts;
    }

    /// <summary>
    /// Identifies a file from its first bytes, falling back to the extension.
    /// </summary>
    /// <remarks>
    /// Only the header is read, and a failure is not fatal: a file that cannot be opened
    /// here still gets a row, and reports its real problem when something tries to
    /// render it.
    /// </remarks>
    private static FontFormat SniffWithoutReadingMuch(string path, long length)
    {
        if (length == 0) return FontFormat.Unknown;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.SequentialScan);

            Span<byte> header = stackalloc byte[FormatSniffer.HeaderBytes];
            int read = stream.Read(header);

            return FormatSniffer.Sniff(header[..read], Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return FormatSniffer.Sniff([], Path.GetFileName(path));
        }
    }

    private static void RecordCompanion(
        Dictionary<string, Dictionary<string, List<string>>> companionsByFolder, string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder)) return;

        string baseName = Path.GetFileNameWithoutExtension(path);

        if (!companionsByFolder.TryGetValue(folder, out Dictionary<string, List<string>>? byBaseName))
        {
            byBaseName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            companionsByFolder[folder] = byBaseName;
        }

        if (!byBaseName.TryGetValue(baseName, out List<string>? list))
        {
            list = [];
            byBaseName[baseName] = list;
        }

        list.Add(path);
    }

    private static IReadOnlyList<string> FindCompanions(
        Dictionary<string, Dictionary<string, List<string>>> companionsByFolder, string fontPath)
    {
        string? folder = Path.GetDirectoryName(fontPath);
        if (string.IsNullOrEmpty(folder)) return [];

        if (!companionsByFolder.TryGetValue(folder, out Dictionary<string, List<string>>? byBaseName))
            return [];

        string baseName = Path.GetFileNameWithoutExtension(fontPath);
        if (!byBaseName.TryGetValue(baseName, out List<string>? companions)) return [];

        // .afm before .pfm: it carries kerning pairs, which .pfm does not.
        return [.. companions.OrderBy(
            path => Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".afm" => 0,
                ".pfm" => 1,
                _ => 2,
            })];
    }
}
