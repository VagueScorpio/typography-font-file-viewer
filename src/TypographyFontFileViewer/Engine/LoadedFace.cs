using System.IO;
using System.Runtime.InteropServices;
using FreeTypeSharp;
using HarfBuzzSharp;
using TypographyFontFileViewer.Core;
using static FreeTypeSharp.FT;

namespace TypographyFontFileViewer.Engine;

/// <summary>
/// One open face: the font bytes, pinned, plus the FreeType and HarfBuzz objects that
/// point into them.
/// </summary>
/// <remarks>
/// <para>
/// The bytes are read in full and pinned, and the face is created with
/// <c>FT_New_Memory_Face</c>, so the app never holds a handle to a font file. That is
/// what lets the user delete, rename or un-favourite a font that is on screen.
/// </para>
/// <para>
/// Only the engine thread may touch an instance of this class. FreeType and HarfBuzz
/// objects are not safe to use from several threads at once, which is the reason the
/// engine is single-threaded at all.
/// </para>
/// </remarks>
internal sealed unsafe class LoadedFace : IDisposable
{
    private const uint FT_OPEN_MEMORY = 0x1;

    /// <summary>Roughly what FreeType allocates per face on top of the file bytes.</summary>
    private const long NativeOverheadEstimate = 64 * 1024;

    private GCHandle _pin;
    private readonly List<GCHandle> _companionPins = [];
    private FT_FaceRec_* _face;
    private Blob? _blob;
    private Face? _hbFace;
    private Font? _hbFont;
    private bool _disposed;

    private LoadedFace(FaceKey key, FontFormat format, byte[] bytes)
    {
        Key = key;
        Format = format;
        Bytes = bytes;
    }

    public FaceKey Key { get; }

    public FontFormat Format { get; }

    public byte[] Bytes { get; }

    /// <summary>The FreeType face. Valid until <see cref="Dispose"/>.</summary>
    public FT_FaceRec_* Face => _face;

    /// <summary>The HarfBuzz font, or null for formats HarfBuzz cannot shape.</summary>
    public Font? HbFont => _hbFont;

    /// <summary>How many faces the file holds.</summary>
    public int FaceCount { get; private set; } = 1;

    /// <summary>Pixel sizes a bitmap-only font actually has; empty when scalable.</summary>
    public IReadOnlyList<int> FixedSizes { get; private set; } = [];

    public bool IsScalable { get; private set; }

    public bool HasKerning { get; private set; }

    public bool HasGlyphNames { get; private set; }

    public bool HasColor { get; private set; }

    public bool IsVariable { get; private set; }

    public bool IsFixedWidth { get; private set; }

    /// <summary>Which companion metric files were attached, for the Info tab.</summary>
    public IReadOnlyList<string> AttachedCompanions { get; private set; } = [];

    /// <summary>Approximate memory this face accounts for, against the cache budget.</summary>
    public long ByteCost => Bytes.LongLength + NativeOverheadEstimate;

    /// <summary>The pixel size currently selected, or 0 if none has been set yet.</summary>
    public int CurrentPixelSize { get; private set; }

    /// <summary>
    /// Opens one face out of <paramref name="bytes"/>.
    /// </summary>
    /// <param name="library">The engine thread's FreeType library.</param>
    /// <param name="key">Identifies the file and which face inside it.</param>
    /// <param name="bytes">The whole font file. Taken over by this instance.</param>
    /// <param name="format">What the sniffer decided the file is.</param>
    /// <param name="companionPaths">Type 1 metric files to attach, if any.</param>
    /// <exception cref="FontLoadException">FreeType rejected the data.</exception>
    public static LoadedFace Load(
        FT_LibraryRec_* library,
        FaceKey key,
        byte[] bytes,
        FontFormat format,
        IReadOnlyList<string>? companionPaths = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            throw new FontLoadException("The file is empty.") { Path = key.Path };

        var loaded = new LoadedFace(key, format, bytes);
        try
        {
            loaded._pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            byte* fontBase = (byte*)loaded._pin.AddrOfPinnedObject();

            FT_FaceRec_* face;
            FT_Error error = FT_New_Memory_Face(
                library, fontBase, (nint)bytes.Length, (nint)key.FaceIndex, &face);

            if (error != FT_Error.FT_Err_Ok)
                throw new FontLoadException(DescribeError(error, key.FaceIndex)) { Path = key.Path };

            loaded._face = face;
            loaded.ReadFaceFlags();

            if (companionPaths is { Count: > 0 })
                loaded.AttachCompanions(companionPaths);

            if (loaded.IsSfntBacked())
                loaded.CreateHarfBuzzFont(fontBase, bytes.Length, key.FaceIndex);

            return loaded;
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads how many faces a file holds, without keeping anything open.
    /// </summary>
    /// <remarks>
    /// FreeType reports the count on any successfully opened face, so face 0 is opened
    /// and immediately closed. A file that will not open at all reports 0.
    /// </remarks>
    public static int ReadFaceCount(FT_LibraryRec_* library, byte[] bytes)
    {
        if (bytes.Length == 0) return 0;

        GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        FT_FaceRec_* face = null;
        try
        {
            FT_Error error = FT_New_Memory_Face(
                library, (byte*)pin.AddrOfPinnedObject(), (nint)bytes.Length, (nint)0, &face);
            if (error != FT_Error.FT_Err_Ok) return 0;

            return (int)Math.Clamp((long)face->num_faces, 0, int.MaxValue);
        }
        finally
        {
            if (face != null) FT_Done_Face(face);
            pin.Free();
        }
    }

    /// <summary>
    /// Selects a size for rendering. Scalable fonts take any size; bitmap fonts snap to
    /// the nearest strike they actually have.
    /// </summary>
    /// <returns>The pixel size really in effect, which may differ for bitmap fonts.</returns>
    /// <exception cref="FontLoadException">No usable size could be selected.</exception>
    public int SetPixelSize(int pixelSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pixelSize, 4096);

        if (IsScalable)
        {
            FT_Error error = FT_Set_Pixel_Sizes(_face, 0, (uint)pixelSize);
            if (error != FT_Error.FT_Err_Ok)
                throw new FontLoadException($"This font rejected a size of {pixelSize} px ({error}).")
                { Path = Key.Path };

            CurrentPixelSize = pixelSize;
            _hbFont?.SetScale(pixelSize * 64, pixelSize * 64);
            return pixelSize;
        }

        // Bitmap font: pick the strike closest to what was asked for.
        int strikeIndex = NearestStrikeIndex(pixelSize);
        if (strikeIndex < 0)
            throw new FontLoadException("This bitmap font has no usable sizes.") { Path = Key.Path };

        FT_Error selectError = FT_Select_Size(_face, strikeIndex);
        if (selectError != FT_Error.FT_Err_Ok)
            throw new FontLoadException($"Could not select a bitmap strike ({selectError}).")
            { Path = Key.Path };

        CurrentPixelSize = FixedSizes[strikeIndex];
        _hbFont?.SetScale(CurrentPixelSize * 64, CurrentPixelSize * 64);
        return CurrentPixelSize;
    }

    /// <summary>The strike nearest <paramref name="pixelSize"/>, or -1 when there are none.</summary>
    private int NearestStrikeIndex(int pixelSize)
    {
        if (FixedSizes.Count == 0) return -1;

        int best = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < FixedSizes.Count; i++)
        {
            int distance = Math.Abs(FixedSizes[i] - pixelSize);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    private void ReadFaceFlags()
    {
        long flags = (long)_face->face_flags;
        IsScalable = (flags & (long)FT_FACE_FLAG.FT_FACE_FLAG_SCALABLE) != 0;
        HasKerning = (flags & (long)FT_FACE_FLAG.FT_FACE_FLAG_KERNING) != 0;
        HasGlyphNames = (flags & (long)FT_FACE_FLAG.FT_FACE_FLAG_GLYPH_NAMES) != 0;
        HasColor = (flags & (long)FT_FACE_FLAG.FT_FACE_FLAG_COLOR) != 0;
        IsFixedWidth = (flags & (long)FT_FACE_FLAG.FT_FACE_FLAG_FIXED_WIDTH) != 0;
        IsVariable = (flags & (long)FT_FACE_FLAG.FT_FACE_FLAG_MULTIPLE_MASTERS) != 0;

        FaceCount = (int)Math.Clamp((long)_face->num_faces, 1, int.MaxValue);

        int strikeCount = _face->num_fixed_sizes;
        if (strikeCount > 0 && _face->available_sizes != null)
        {
            var sizes = new int[strikeCount];
            for (int i = 0; i < strikeCount; i++)
            {
                // y_ppem is 26.6 fixed point; height is a plain pixel count.
                FT_Bitmap_Size_ strike = _face->available_sizes[i];
                int ppem = (int)(((long)strike.y_ppem + 32) >> 6);
                sizes[i] = ppem > 0 ? ppem : strike.height;
            }
            FixedSizes = sizes;
        }
    }

    private bool IsSfntBacked()
        => ((long)_face->face_flags & (long)FT_FACE_FLAG.FT_FACE_FLAG_SFNT) != 0;

    private void CreateHarfBuzzFont(byte* fontBase, int length, int faceIndex)
    {
        try
        {
            _blob = new Blob((nint)fontBase, length, MemoryMode.ReadOnly);
            _hbFace = new Face(_blob, faceIndex);
            _hbFont = new Font(_hbFace);
        }
        catch (Exception)
        {
            // Shaping is an upgrade, not a requirement. If HarfBuzz will not take this
            // font, TextLayout falls back to FreeType's own cmap and kerning.
            _hbFont?.Dispose();
            _hbFace?.Dispose();
            _blob?.Dispose();
            _hbFont = null;
            _hbFace = null;
            _blob = null;
        }
    }

    /// <summary>
    /// Attaches .afm / .pfm metrics to a Type 1 font so it kerns correctly.
    /// </summary>
    /// <remarks>
    /// The companion is attached from memory rather than by path: it keeps the no-lock
    /// rule, and it sidesteps FreeType's byte-oriented path handling for names that are
    /// not plain ASCII.
    /// </remarks>
    private void AttachCompanions(IReadOnlyList<string> companionPaths)
    {
        var attached = new List<string>();

        foreach (string companionPath in companionPaths)
        {
            byte[] companionBytes;
            try
            {
                companionBytes = ReadWithoutLocking(companionPath);
            }
            catch (FontLoadException)
            {
                continue;   // a missing or unreadable companion is not fatal
            }

            if (companionBytes.Length == 0) continue;

            var companionPin = GCHandle.Alloc(companionBytes, GCHandleType.Pinned);
            _companionPins.Add(companionPin);

            var args = new FT_Open_Args_
            {
                flags = FT_OPEN_MEMORY,
                memory_base = (byte*)companionPin.AddrOfPinnedObject(),
                memory_size = (nint)companionBytes.Length,
            };

            if (FT_Attach_Stream(_face, &args) == FT_Error.FT_Err_Ok)
                attached.Add(Path.GetFileName(companionPath));
        }

        AttachedCompanions = attached;
    }

    /// <summary>
    /// Reads a file whole, sharing it with every other writer and deleter, so the app
    /// never locks a font.
    /// </summary>
    public static byte[] ReadWithoutLocking(string path, long maximumBytes = 512L * 1024 * 1024)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.SequentialScan);

            long length = stream.Length;
            if (length > maximumBytes)
                throw new FontLoadException(
                    $"The file is too large to be a font ({length / (1024 * 1024)} MB).")
                { Path = path };

            byte[] bytes = new byte[length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new FontLoadException($"The file could not be read: {ex.Message}", ex) { Path = path };
        }
    }

    private static string DescribeError(FT_Error error, int faceIndex) => error switch
    {
        FT_Error.FT_Err_Unknown_File_Format => "This is not a font file FreeType recognises.",
        FT_Error.FT_Err_Invalid_Argument when faceIndex > 0 => $"This file has no face {faceIndex}.",
        FT_Error.FT_Err_Cannot_Open_Resource => "The font data could not be opened.",
        FT_Error.FT_Err_Invalid_Stream_Operation => "The font data is truncated or malformed.",
        _ => $"FreeType could not open this font ({error}).",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Reverse of acquisition: HarfBuzz lets go of the bytes, then FreeType, and only
        // then are the bytes unpinned. Anything else frees memory still being read.
        _hbFont?.Dispose();
        _hbFace?.Dispose();
        _blob?.Dispose();
        _hbFont = null;
        _hbFace = null;
        _blob = null;

        if (_face != null)
        {
            FT_Done_Face(_face);
            _face = null;
        }

        foreach (GCHandle companionPin in _companionPins)
            if (companionPin.IsAllocated) companionPin.Free();
        _companionPins.Clear();

        if (_pin.IsAllocated) _pin.Free();
    }
}
