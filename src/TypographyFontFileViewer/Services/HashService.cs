using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace TypographyFontFileViewer.Services;

/// <summary>
/// SHA-256 of a file's contents, remembered so the same file is not hashed twice.
/// </summary>
/// <remarks>
/// <para>
/// This is how the app tells one font from another. Names are no use: two different
/// fonts are often both called Roboto.ttf, and the same font renamed is still the same
/// font. Hashing the contents answers the question the favourites star is really asking.
/// </para>
/// <para>
/// Results are cached per path, length and last-write time, so a file that changes on
/// disk is hashed again rather than remembered wrongly.
/// </para>
/// </remarks>
public sealed class HashService
{
    private readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified), string> _cache
        = new();

    /// <summary>Files larger than this are refused rather than read whole.</summary>
    public const long MaximumBytes = 512L * 1024 * 1024;

    public int Count => _cache.Count;

    /// <summary>Hashes a file, or returns the hash already computed for it.</summary>
    /// <returns>Lower-case hex, or null if the file could not be read.</returns>
    public async Task<string?> ComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes) return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var key = (path, info.Length, info.LastWriteTimeUtc);
        if (_cache.TryGetValue(key, out string? cached)) return cached;

        try
        {
            // Shared like every other read in this app, so hashing never locks a font.
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
            string hex = Convert.ToHexStringLower(digest);

            _cache[key] = hex;
            return hex;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Forgets a file, so the next request reads it again.</summary>
    public void Invalidate(string path)
    {
        foreach (var key in _cache.Keys)
            if (string.Equals(key.Path, path, StringComparison.OrdinalIgnoreCase))
                _cache.TryRemove(key, out _);
    }

    public void Clear() => _cache.Clear();
}
