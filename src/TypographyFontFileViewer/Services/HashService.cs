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
/// disk is hashed again rather than remembered wrongly. The cache is a bounded
/// least-recently-used one: hashing runs over every folder the user opens, and a table
/// that only ever grew would hold a key for every file they had ever browsed.
/// </para>
/// </remarks>
public sealed class HashService
{
    /// <summary>
    /// How many hashes to remember.
    /// </summary>
    /// <remarks>
    /// Entries here are small and all much of a size — a path, three fields and a
    /// 64-character string — so unlike the bitmap caches a count is a fair bound on its
    /// own. This covers two large folders at once for roughly half a megabyte.
    /// </remarks>
    public const int MaximumEntries = 4096;

    private readonly record struct Key(string Path, long Length, DateTime Modified);

    // Most recently used at the front, as in BitmapLruCache.
    private readonly LinkedList<(Key Key, string Hash)> _order = new();
    private readonly Dictionary<Key, LinkedListNode<(Key Key, string Hash)>> _byKey = [];
    private readonly Lock _gate = new();

    /// <summary>Files larger than this are refused rather than read whole.</summary>
    public const long MaximumBytes = 512L * 1024 * 1024;

    public int Count
    {
        get { lock (_gate) return _byKey.Count; }
    }

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

        var key = new Key(path, info.Length, info.LastWriteTimeUtc);
        if (Recall(key) is { } cached) return cached;

        try
        {
            // Shared like every other read in this app, so hashing never locks a font.
            // The lock is deliberately not held across this: two threads racing on one
            // file hash it twice and store the same answer, which is wasted work rather
            // than a wrong one.
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
            string hex = Convert.ToHexStringLower(digest);

            Remember(key, hex);
            return hex;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Returns a remembered hash, moving it to the front of the queue.</summary>
    private string? Recall(Key key)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out LinkedListNode<(Key, string)>? node)) return null;

            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Item2;
        }
    }

    /// <summary>Stores a hash, dropping the least recently used to stay inside the bound.</summary>
    private void Remember(Key key, string hash)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out LinkedListNode<(Key, string)>? existing))
            {
                _order.Remove(existing);
                _order.AddFirst(existing);
                return;
            }

            _byKey[key] = _order.AddFirst((key, hash));

            while (_byKey.Count > MaximumEntries)
            {
                // The count above guarantees a last node.
                LinkedListNode<(Key Key, string Hash)> oldest = _order.Last!;

                _order.RemoveLast();
                _byKey.Remove(oldest.Value.Key);
            }
        }
    }

    /// <summary>Forgets a file, so the next request reads it again.</summary>
    public void Invalidate(string path)
    {
        lock (_gate)
        {
            List<Key> doomed = [.. _byKey.Keys.Where(
                key => string.Equals(key.Path, path, StringComparison.OrdinalIgnoreCase))];

            foreach (Key key in doomed)
            {
                if (!_byKey.Remove(key, out LinkedListNode<(Key, string)>? node)) continue;
                _order.Remove(node);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byKey.Clear();
            _order.Clear();
        }
    }
}
