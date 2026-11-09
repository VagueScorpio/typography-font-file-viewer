using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Engine;

namespace TypographyFontFileViewer.Services;

/// <summary>
/// The engine's public face. Everything is async and cancellable; nothing native, and no
/// WPF type, crosses this boundary in either direction.
/// </summary>
/// <remarks>
/// The UI hands over a request and gets back plain data — a BGRA buffer, a list of
/// summaries, a record of metadata. It never sees a face, a glyph slot or a library
/// handle, which is what keeps the single-threaded engine safe behind a normal
/// multi-threaded UI.
/// </remarks>
public sealed class RenderService : IDisposable
{
    private readonly FontEngine _engine;
    private bool _disposed;

    public RenderService(int maximumCachedFaces = 64, long maximumCachedBytes = 256L * 1024 * 1024)
        => _engine = new FontEngine(maximumCachedFaces, maximumCachedBytes);

    /// <summary>Completes when the engine is up, or faults if FreeType would not start.</summary>
    public Task Ready => _engine.Ready;

    /// <summary>The FreeType version in use; valid once <see cref="Ready"/> has completed.</summary>
    public string NativeVersion => _engine.NativeVersion;

    /// <summary>Open faces, bytes held, and hit/miss counts, for the status bar.</summary>
    public (int Count, long Bytes, int Hits, int Misses, int Evictions) CacheStatistics
        => _engine.CacheStatistics;

    /// <summary>
    /// Reads one summary per face in a file. A collection yields several; a broken file
    /// yields a single summary carrying the error.
    /// </summary>
    /// <remarks>
    /// This never throws for a bad font. The font list has to be able to show every file
    /// in a folder, including the ones that will not open.
    /// </remarks>
    public Task<FaceSummary[]> ReadSummariesAsync(
        string path, JobPriority priority = JobPriority.Low, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return _engine.EnqueueAsync(context =>
        {
            FontFormat format = FontFormat.Unknown;
            try
            {
                byte[] bytes = LoadedFace.ReadWithoutLocking(path);
                format = FontEngineContext.SniffFormat(path, bytes);
                cancellationToken.ThrowIfCancellationRequested();

                int faceCount = Math.Max(1, context.ReadFaceCount(bytes));
                var summaries = new FaceSummary[faceCount];

                for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    summaries[faceIndex] = ReadOneSummary(context, path, bytes, format, faceIndex, faceCount);
                }

                return summaries;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return [FaceSummary.Broken(path, format, Describe(exception))];
            }
        }, priority, cancellationToken);
    }

    private static FaceSummary ReadOneSummary(
        FontEngineContext context, string path, byte[] bytes,
        FontFormat format, int faceIndex, int faceCount)
    {
        try
        {
            var key = FaceKey.For(path, faceIndex);
            LoadedFace face = context.LoadFace(key, bytes, format);
            return MetadataReader.ReadSummary(face, format) with { FaceCount = faceCount };
        }
        catch (Exception exception)
        {
            // One unreadable face in a collection must not hide the others.
            return FaceSummary.Broken(path, format, Describe(exception)) with
            {
                FaceIndex = faceIndex,
                FaceCount = faceCount,
            };
        }
    }

    /// <summary>
    /// Renders text through one face.
    /// </summary>
    /// <param name="key">Which face, and the file state it was built from.</param>
    /// <param name="text">What to draw. Line breaks are honoured.</param>
    /// <param name="pointSize">Size in points.</param>
    /// <param name="dpi">Pixels per inch of the monitor the window is on.</param>
    /// <param name="maxWidthPx">Wrap width in pixels; 0 or less to never wrap.</param>
    public Task<RenderedText> RenderTextAsync(
        FaceKey key,
        string text,
        double pointSize,
        double dpi,
        int maxWidthPx,
        RenderStyle style,
        JobPriority priority = JobPriority.High,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pointSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dpi, 0);

        int pixelSize = ToPixelSize(pointSize, dpi);

        return _engine.EnqueueAsync(context =>
        {
            LoadedFace face = context.GetFace(key);
            face.SetPixelSize(pixelSize);
            cancellationToken.ThrowIfCancellationRequested();

            LaidOutText layout = TextLayout.Layout(face, text, maxWidthPx);
            cancellationToken.ThrowIfCancellationRequested();

            return context.RenderText(face, layout, style);
        }, priority, cancellationToken);
    }

    /// <summary>
    /// Renders glyph-map cells.
    /// </summary>
    /// <remarks>
    /// Takes <see cref="GlyphEntry"/> rather than bare code points so the Glyphs tab can
    /// also show glyphs that no code point reaches — a font's alternates and small caps
    /// are often unmapped, and they are exactly what the glyph map is for.
    /// </remarks>
    public Task<GlyphCellBitmap[]> RenderGlyphCellsAsync(
        FaceKey key,
        IReadOnlyList<GlyphEntry> glyphs,
        int cellPx,
        RenderStyle style,
        JobPriority priority = JobPriority.Low,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(glyphs);

        return _engine.EnqueueAsync(context =>
        {
            LoadedFace face = context.GetFace(key);

            // Leave room inside the cell so glyphs do not touch their neighbours.
            face.SetPixelSize(Math.Max(1, cellPx * 3 / 4));

            var cells = new GlyphCellBitmap[glyphs.Count];
            for (int i = 0; i < glyphs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cells[i] = context.RenderCell(
                    face, glyphs[i].GlyphIndex, glyphs[i].Codepoint, cellPx, style);
            }

            return cells;
        }, priority, cancellationToken);
    }

    /// <summary>Lists every mapped character, and optionally the unmapped glyphs too.</summary>
    public Task<IReadOnlyList<GlyphEntry>> ListGlyphsAsync(
        FaceKey key,
        bool includeUnmapped = false,
        JobPriority priority = JobPriority.Low,
        CancellationToken cancellationToken = default)
        => _engine.EnqueueAsync(context =>
        {
            LoadedFace face = context.GetFace(key);
            cancellationToken.ThrowIfCancellationRequested();
            return MetadataReader.ListGlyphs(face, includeUnmapped);
        }, priority, cancellationToken);

    /// <summary>Reads everything the Info tab shows for one face.</summary>
    public Task<FontInfo> ReadInfoAsync(
        FaceKey key,
        JobPriority priority = JobPriority.Low,
        CancellationToken cancellationToken = default)
        => _engine.EnqueueAsync(context =>
        {
            LoadedFace face = context.GetFace(key);
            cancellationToken.ThrowIfCancellationRequested();
            return context.ReadInfo(face);
        }, priority, cancellationToken);

    /// <summary>
    /// The sizes a bitmap-only font actually has, for snapping the size slider. Empty
    /// for scalable fonts, which take any size.
    /// </summary>
    public Task<IReadOnlyList<int>> ReadFixedSizesAsync(
        FaceKey key, CancellationToken cancellationToken = default)
        => _engine.EnqueueAsync<IReadOnlyList<int>>(
            context => context.GetFace(key).FixedSizes, JobPriority.High, cancellationToken);

    /// <summary>
    /// Forgets every cached face for a file. Call after the file is known to have
    /// changed on disk, so a stale render can never be served.
    /// </summary>
    public Task<int> InvalidateAsync(string path, CancellationToken cancellationToken = default)
        => _engine.EnqueueAsync(
            context => context.Cache.InvalidatePath(path), JobPriority.High, cancellationToken);

    /// <summary>Points to pixels at a given DPI, as the blueprint defines it.</summary>
    public static int ToPixelSize(double pointSize, double dpi)
        => Math.Clamp((int)Math.Round(pointSize * dpi / 72.0), 1, 4096);

    private static string Describe(Exception exception) => exception switch
    {
        FontLoadException fontLoad => fontLoad.Message,
        System.IO.FileNotFoundException => "The file no longer exists.",
        System.IO.DirectoryNotFoundException => "The folder no longer exists.",
        UnauthorizedAccessException => "Access to this file was denied.",
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.Dispose();
    }
}
