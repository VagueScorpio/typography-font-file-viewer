using System.Windows.Media.Imaging;

namespace TypographyFontFileViewer.Services;

/// <summary>
/// Keeps recently rendered bitmaps so scrolling back is instant.
/// </summary>
/// <remarks>
/// <para>
/// Bounded two ways — a count and a byte budget — because one wide row at 72 pt is
/// worth many small ones, and a count alone would not notice.
/// </para>
/// <para>
/// Shared between the gallery, whose entries are whole rows of text, and the glyph map,
/// whose entries are single cells. Both want the same behaviour: remember what was just
/// on screen, forget the rest, and never grow without limit.
/// </para>
/// </remarks>
public class BitmapLruCache<TKey> where TKey : notnull
{
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;
    private readonly Func<TKey, string>? _pathOf;

    private readonly LinkedList<(TKey Key, BitmapSource Bitmap, long Bytes)> _order = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, BitmapSource Bitmap, long Bytes)>> _byKey = [];

    private readonly Lock _gate = new();
    private long _bytes;

    /// <param name="pathOf">
    /// How to find the file a key was rendered from, so <see cref="InvalidatePath"/> can
    /// work. Omit it when the cache has nothing to do with files.
    /// </param>
    public BitmapLruCache(int maximumEntries, long maximumBytes, Func<TKey, string>? pathOf = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        _maximumEntries = maximumEntries;
        _maximumBytes = maximumBytes;
        _pathOf = pathOf;
    }

    public int Count
    {
        get { lock (_gate) return _byKey.Count; }
    }

    public long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    /// <summary>How many lookups were served without rendering again.</summary>
    public int Hits { get; private set; }

    /// <summary>Returns a cached bitmap, moving it to the front of the queue.</summary>
    public BitmapSource? TryGet(TKey key)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out var node)) return null;

            _order.Remove(node);
            _order.AddFirst(node);
            Hits++;
            return node.Value.Bitmap;
        }
    }

    /// <summary>Stores a bitmap, evicting the least recently used to stay inside both limits.</summary>
    /// <remarks>
    /// The bitmap must already be frozen: the cache is shared between the renders that
    /// fill it and the UI that draws from it.
    /// </remarks>
    public void Add(TKey key, BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (!bitmap.IsFrozen)
            throw new ArgumentException("Cached bitmaps must be frozen.", nameof(bitmap));

        long bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;

        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _order.AddFirst(existing);
                return;
            }

            var node = _order.AddFirst((key, bitmap, bytes));
            _byKey[key] = node;
            _bytes += bytes;

            while ((_byKey.Count > _maximumEntries || _bytes > _maximumBytes) && _order.Count > 1)
            {
                // The loop condition guarantees a last node.
                LinkedListNode<(TKey Key, BitmapSource Bitmap, long Bytes)> oldest = _order.Last!;

                _order.RemoveLast();
                _byKey.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Bytes;
            }
        }
    }

    /// <summary>Forgets everything rendered from a particular file.</summary>
    public void InvalidatePath(string path)
    {
        if (_pathOf is null) return;

        lock (_gate)
        {
            var doomed = _byKey.Keys
                .Where(key => string.Equals(_pathOf(key), path, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (TKey key in doomed)
            {
                if (!_byKey.Remove(key, out var node)) continue;

                _order.Remove(node);
                _bytes -= node.Value.Bytes;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _order.Clear();
            _byKey.Clear();
            _bytes = 0;
        }
    }
}
