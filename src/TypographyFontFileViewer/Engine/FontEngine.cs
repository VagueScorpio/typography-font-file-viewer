using System.Collections.Concurrent;
using System.IO;
using FreeTypeSharp;
using TypographyFontFileViewer.Core;
using static FreeTypeSharp.FT;

namespace TypographyFontFileViewer.Engine;

/// <summary>
/// What a job is handed when it runs: the library, the cache, and a way to get a face.
/// </summary>
internal sealed unsafe class FontEngineContext
{
    private readonly FT_LibraryRec_* _library;

    internal FontEngineContext(FT_LibraryRec_* library, FaceCache cache)
    {
        _library = library;
        Cache = cache;
    }

    public FaceCache Cache { get; }

    // The library pointer deliberately stays inside this class. Callers above the engine
    // work through the wrappers below, so no file outside Engine/ has to be unsafe.

    /// <summary>How many faces a font file holds.</summary>
    public int ReadFaceCount(byte[] bytes) => LoadedFace.ReadFaceCount(_library, bytes);

    /// <summary>Rasterizes laid-out text into a BGRA buffer.</summary>
    public RenderedText RenderText(LoadedFace face, LaidOutText layout, RenderStyle style)
        => GlyphRasterizer.Render(_library, face, layout, style);

    /// <summary>Rasterizes one glyph-map cell.</summary>
    public GlyphCellBitmap RenderCell(
        LoadedFace face, uint glyphIndex, int codepoint, int cellPx, RenderStyle style)
        => GlyphRasterizer.RenderCell(_library, face, glyphIndex, codepoint, cellPx, style);

    /// <summary>Reads the full metadata record for an open face.</summary>
    public FontInfo ReadInfo(LoadedFace face)
        => MetadataReader.ReadInfo(_library, face, face.Format);

    /// <summary>
    /// Returns an open face for <paramref name="key"/>, from the cache when possible and
    /// by reading the file when not.
    /// </summary>
    /// <exception cref="FontLoadException">The file is unreadable or not a font.</exception>
    public LoadedFace GetFace(FaceKey key)
    {
        LoadedFace? cached = Cache.TryGet(key);
        if (cached is not null) return cached;

        (byte[] sfnt, FontFormat format) = ReadAndNormalize(key.Path);
        return LoadFace(key, sfnt, format);
    }

    /// <summary>
    /// Reads a font file and returns SFNT bytes plus the container it arrived in.
    /// </summary>
    /// <remarks>
    /// A WOFF or WOFF2 file is decoded here, so everything downstream sees plain SFNT.
    /// The container format is still reported, because that is what the list badge and
    /// the Info tab show.
    /// </remarks>
    public static (byte[] Sfnt, FontFormat Container) ReadAndNormalize(string path)
    {
        byte[] raw = LoadedFace.ReadWithoutLocking(path);
        FontFormat format = SniffFormat(path, raw);
        return (FormatNormalizer.ToSfnt(raw, format, path), format);
    }

    /// <summary>
    /// Opens a face from SFNT bytes already in hand, adding it to the cache.
    /// </summary>
    /// <remarks>
    /// The bytes must already have been through <see cref="FormatNormalizer"/>; a WOFF
    /// file handed straight to FreeType would either fail or, worse, open with whatever
    /// partial support the native library happens to have.
    /// </remarks>
    public LoadedFace LoadFace(FaceKey key, byte[] bytes, FontFormat format)
    {
        LoadedFace? cached = Cache.TryGet(key);
        if (cached is not null) return cached;

        IReadOnlyList<string> companions = format == FontFormat.Type1
            ? FindType1Companions(key.Path)
            : [];

        LoadedFace face = LoadedFace.Load(_library, key, bytes, format, companions);
        return Cache.Add(face);
    }

    /// <summary>Identifies a file from its first bytes, falling back to the name.</summary>
    public static FontFormat SniffFormat(string path, byte[] bytes)
    {
        int headerLength = Math.Min(FormatSniffer.HeaderBytes, bytes.Length);
        return FormatSniffer.Sniff(bytes.AsSpan(0, headerLength), Path.GetFileName(path));
    }

    /// <summary>
    /// Finds the .afm / .pfm metrics that belong to a Type 1 font.
    /// </summary>
    /// <remarks>
    /// Without these a Type 1 font renders, but with no kerning and often the wrong
    /// advance widths, because the outlines and the metrics live in separate files.
    /// </remarks>
    public static IReadOnlyList<string> FindType1Companions(string fontPath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(fontPath);
            if (string.IsNullOrEmpty(directory)) return [];

            string baseName = Path.GetFileNameWithoutExtension(fontPath);
            var found = new List<string>(2);

            // .afm first: it carries kerning pairs, which .pfm does not.
            foreach (string extension in new[] { ".afm", ".pfm" })
            {
                string candidate = Path.Combine(directory, baseName + extension);
                if (File.Exists(candidate)) found.Add(candidate);
            }

            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}

/// <summary>
/// The one thread that owns every FreeType and HarfBuzz object in the process.
/// </summary>
/// <remarks>
/// <para>
/// Neither library is safe to call from several threads at once, so rather than lock
/// around them, all work queues at a single counter. Two queues feed it: High for what
/// the user is looking at, Low for gallery rows and background metadata. High is always
/// drained first, so a thousand queued gallery renders cannot delay the selected font's
/// preview.
/// </para>
/// <para>
/// Jobs carry a cancellation token and are checked twice — when they reach the front of
/// the queue and again inside the job — so work abandoned by fast scrolling costs almost
/// nothing.
/// </para>
/// </remarks>
internal sealed unsafe class FontEngine : IDisposable
{
    private readonly Thread _thread;
    private readonly ConcurrentQueue<EngineJob> _highPriority = new();
    private readonly ConcurrentQueue<EngineJob> _lowPriority = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly CancellationTokenSource _shutdown = new();

    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly int _maximumFaces;
    private readonly long _maximumBytes;

    private volatile bool _disposed;

    public FontEngine(int maximumCachedFaces = 64, long maximumCachedBytes = 256L * 1024 * 1024)
    {
        _maximumFaces = maximumCachedFaces;
        _maximumBytes = maximumCachedBytes;

        _thread = new Thread(Run)
        {
            Name = "FontEngine",
            IsBackground = true,
        };
        _thread.Start();
    }

    /// <summary>Completes once FreeType has initialised, or faults if it could not.</summary>
    public Task Ready => _ready.Task;

    /// <summary>The FreeType version in use, available after <see cref="Ready"/>.</summary>
    public string NativeVersion { get; private set; } = "unknown";

    /// <summary>Cache statistics, for diagnostics.</summary>
    public (int Count, long Bytes, int Hits, int Misses, int Evictions) CacheStatistics { get; private set; }

    /// <summary>
    /// Queues <paramref name="work"/> and returns a task for its result.
    /// </summary>
    public Task<TResult> EnqueueAsync<TResult>(
        Func<FontEngineContext, TResult> work,
        JobPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_disposed)
            return Task.FromException<TResult>(new ObjectDisposedException(nameof(FontEngine)));

        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<TResult>(cancellationToken);

        var job = new EngineJob<TResult>(work)
        {
            Priority = priority,
            CancellationToken = cancellationToken,
        };

        if (priority == JobPriority.High) _highPriority.Enqueue(job);
        else _lowPriority.Enqueue(job);

        _work.Release();
        return job.Task;
    }

    private void Run()
    {
        FT_LibraryRec_* library = null;
        FaceCache? cache = null;

        try
        {
            FT_Error error = FT_Init_FreeType(&library);
            if (error != FT_Error.FT_Err_Ok)
            {
                _ready.TrySetException(
                    new FontLoadException($"FreeType could not initialise ({error})."));
                return;
            }

            int major = 0, minor = 0, patch = 0;
            FT_Library_Version(library, &major, &minor, &patch);
            NativeVersion = $"{major}.{minor}.{patch}";

            cache = new FaceCache(_maximumFaces, _maximumBytes);
            var context = new FontEngineContext(library, cache);
            _ready.TrySetResult(true);

            Drain(context, cache);
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
        }
        finally
        {
            // Everything native is created on this thread and torn down on it, in order:
            // faces (with their HarfBuzz objects and pinned bytes) before the library.
            cache?.Dispose();
            if (library != null) FT_Done_FreeType(library);

            CancelPending();
        }
    }

    private void Drain(FontEngineContext context, FaceCache cache)
    {
        CancellationToken shutdownToken = _shutdown.Token;

        while (true)
        {
            try
            {
                _work.Wait(shutdownToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (shutdownToken.IsCancellationRequested) return;
            if (!TryDequeue(out EngineJob? job) || job is null) continue;

            if (job.CancellationToken.IsCancellationRequested)
            {
                job.Cancel();
                continue;
            }

            try
            {
                job.Execute(context);
            }
            catch (Exception exception)
            {
                // Execute already captures failures; this only catches a job type that
                // threw outside its own guard. The engine thread must never die.
                job.Fail(exception);
            }

            CacheStatistics = (cache.Count, cache.Bytes, cache.Hits, cache.Misses, cache.Evictions);
        }
    }

    /// <summary>High before low, always.</summary>
    private bool TryDequeue(out EngineJob? job)
    {
        if (_highPriority.TryDequeue(out EngineJob? high))
        {
            job = high;
            return true;
        }

        if (_lowPriority.TryDequeue(out EngineJob? low))
        {
            job = low;
            return true;
        }

        job = null;
        return false;
    }

    /// <summary>Fails every queued job so no caller is left awaiting forever.</summary>
    private void CancelPending()
    {
        var reason = new ObjectDisposedException(nameof(FontEngine));

        while (_highPriority.TryDequeue(out EngineJob? job)) job.Fail(reason);
        while (_lowPriority.TryDequeue(out EngineJob? job)) job.Fail(reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shutdown.Cancel();
        _work.Release();          // wake the thread so it sees the cancellation

        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            // A wedged engine thread is a bug, but refusing to exit the app over it
            // would be worse: the thread is a background thread and will be torn down.
            CancelPending();
        }

        _work.Dispose();
        _shutdown.Dispose();
    }
}
