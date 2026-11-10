using System.Windows.Media.Imaging;
using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Services;

/// <summary>
/// Identifies one rendered gallery row. Everything that changes the pixels is in here,
/// so a hit is always the right image.
/// </summary>
public readonly record struct GalleryRenderKey(
    FaceKey Face, string Text, int PixelSize, bool DarkBackground);

/// <summary>
/// Keeps recently rendered gallery rows so scrolling back up is instant.
/// </summary>
/// <remarks>
/// <para>
/// Re-rendering a row costs a trip to the engine thread; remembering one costs its
/// pixels. A few hundred rows of a single line of text is a few tens of megabytes, which
/// is a good trade against re-rendering every row on every scroll.
/// </para>
/// <para>
/// Bounded two ways — a row count and a byte budget — because one 2,000-pixel-wide row
/// at 72 pt is worth many short ones, and a count alone would not notice.
/// </para>
/// </remarks>
public sealed class RenderedBitmapCache
{
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;

    private readonly LinkedList<(GalleryRenderKey Key, BitmapSource Bitmap, long Bytes)> _order = new();
    private readonly Dictionary<GalleryRenderKey,
        LinkedListNode<(GalleryRenderKey Key, BitmapSource Bitmap, long Bytes)>> _byKey = [];

    private readonly Lock _gate = new();

    public RenderedBitmapCache(int maximumEntries = 300, long maximumBytes = 96L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        _maximumEntries = maximumEntries;
        _maximumBytes = maximumBytes;
    }

    public int Count
    {
        get { lock (_gate) return _byKey.Count; }
    }

    public long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    private long _bytes;

    /// <summary>Returns a cached bitmap, moving it to the front of the queue.</summary>
    public BitmapSource? TryGet(GalleryRenderKey key)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out var node))
            {
                Misses++;
                return null;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            Hits++;
            return node.Value.Bitmap;
        }
    }

    /// <summary>
    /// Stores a bitmap, evicting the least recently used rows to stay inside both limits.
    /// </summary>
    /// <remarks>
    /// The bitmap must already be frozen: the cache is shared between the renders that
    /// fill it and the UI that draws from it.
    /// </remarks>
    public void Add(GalleryRenderKey key, BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (!bitmap.IsFrozen)
            throw new ArgumentException("Cached bitmaps must be frozen.", nameof(bitmap));

        long bytes = EstimateBytes(bitmap);

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
                var oldest = _order.Last;
                if (oldest is null) break;

                _order.RemoveLast();
                _byKey.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Bytes;
            }
        }
    }

    /// <summary>Forgets every row drawn from a particular file.</summary>
    public void InvalidatePath(string path)
    {
        lock (_gate)
        {
            var doomed = _byKey.Keys
                .Where(key => string.Equals(key.Face.Path, path, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (GalleryRenderKey key in doomed) RemoveLocked(key);
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

    private void RemoveLocked(GalleryRenderKey key)
    {
        if (!_byKey.Remove(key, out var node)) return;

        _order.Remove(node);
        _bytes -= node.Value.Bytes;
    }

    private static long EstimateBytes(BitmapSource bitmap)
        => (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
}
