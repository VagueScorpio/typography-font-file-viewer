using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Engine;

/// <summary>
/// Keeps the most recently used faces open so scrolling back and forth stays instant.
/// </summary>
/// <remarks>
/// <para>
/// Least-recently-used, bounded two ways: a face count and a byte budget, whichever is
/// reached first. A single 30 MB CJK font should not be able to push out sixty small
/// ones, and sixty small ones should not add up to a gigabyte.
/// </para>
/// <para>
/// Not thread-safe, and deliberately so: only the engine thread ever touches it, which
/// is what makes the native objects inside safe to use at all.
/// </para>
/// </remarks>
internal sealed class FaceCache : IDisposable
{
    private readonly int _maximumFaces;
    private readonly long _maximumBytes;

    // Most recently used at the front.
    private readonly LinkedList<LoadedFace> _order = new();
    private readonly Dictionary<FaceKey, LinkedListNode<LoadedFace>> _byKey = [];

    private long _bytes;
    private bool _disposed;

    public FaceCache(int maximumFaces = 64, long maximumBytes = 256L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFaces, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        _maximumFaces = maximumFaces;
        _maximumBytes = maximumBytes;
    }

    public int Count => _byKey.Count;

    public long Bytes => _bytes;

    /// <summary>How many lookups were served without reopening the font.</summary>
    public int Hits { get; private set; }

    public int Misses { get; private set; }

    public int Evictions { get; private set; }

    /// <summary>
    /// Returns the cached face for <paramref name="key"/>, or null. A hit moves the face
    /// to the front of the queue.
    /// </summary>
    public LoadedFace? TryGet(FaceKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_byKey.TryGetValue(key, out LinkedListNode<LoadedFace>? node))
        {
            Misses++;
            return null;
        }

        _order.Remove(node);
        _order.AddFirst(node);
        Hits++;
        return node.Value;
    }

    /// <summary>
    /// Adds a freshly opened face, evicting as needed. If the key is already present the
    /// incoming face is disposed and the existing one kept, so no caller can end up
    /// holding a face the cache has quietly replaced.
    /// </summary>
    public LoadedFace Add(LoadedFace face)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(face);

        if (_byKey.TryGetValue(face.Key, out LinkedListNode<LoadedFace>? existing))
        {
            face.Dispose();
            _order.Remove(existing);
            _order.AddFirst(existing);
            return existing.Value;
        }

        LinkedListNode<LoadedFace> node = _order.AddFirst(face);
        _byKey[face.Key] = node;
        _bytes += face.ByteCost;

        Trim();
        return face;
    }

    /// <summary>
    /// Drops every face whose file is this path, whatever the face index. Used when a
    /// file is known to have changed, so a stale face is never handed back.
    /// </summary>
    public int InvalidatePath(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<FaceKey> doomed = [];
        foreach (FaceKey key in _byKey.Keys)
            if (string.Equals(key.Path, path, StringComparison.OrdinalIgnoreCase))
                doomed.Add(key);

        foreach (FaceKey key in doomed) Remove(key);
        return doomed.Count;
    }

    public bool Remove(FaceKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_byKey.Remove(key, out LinkedListNode<LoadedFace>? node)) return false;

        _order.Remove(node);
        _bytes -= node.Value.ByteCost;
        node.Value.Dispose();
        return true;
    }

    public void Clear()
    {
        foreach (LinkedListNode<LoadedFace> node in _byKey.Values)
            node.Value.Dispose();

        _byKey.Clear();
        _order.Clear();
        _bytes = 0;
    }

    private void Trim()
    {
        while ((_byKey.Count > _maximumFaces || _bytes > _maximumBytes) && _order.Count > 1)
        {
            LinkedListNode<LoadedFace>? oldest = _order.Last;
            if (oldest is null) return;

            _order.RemoveLast();
            _byKey.Remove(oldest.Value.Key);
            _bytes -= oldest.Value.ByteCost;
            oldest.Value.Dispose();
            Evictions++;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }
}
