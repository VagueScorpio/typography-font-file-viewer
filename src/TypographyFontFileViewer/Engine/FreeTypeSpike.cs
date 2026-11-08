using System.IO;
using System.Runtime.InteropServices;
using FreeTypeSharp;
using HarfBuzzSharp;
using TypographyFontFileViewer.Core;
using static FreeTypeSharp.FT;
using HbBuffer = HarfBuzzSharp.Buffer;

namespace TypographyFontFileViewer.Engine;

/// <summary>
/// A BGRA image produced by the engine, ready for the UI thread to wrap in a
/// <c>BitmapSource</c>.
/// </summary>
/// <param name="Bgra">Pixels, 4 bytes per pixel, premultiplied-free straight BGRA.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Stride">Bytes per row (<c>Width * 4</c>).</param>
/// <param name="GlyphCount">How many glyphs the shaper produced.</param>
public readonly record struct RenderedBitmap(byte[] Bgra, int Width, int Height, int Stride, int GlyphCount);

/// <summary>
/// Phase 0 de-risking spike: prove that FreeType decodes a font, HarfBuzz shapes a
/// string through it, and the result rasterizes to a BGRA buffer the UI can show —
/// all from a single self-contained exe with no fonts installed and no file locks.
/// </summary>
/// <remarks>
/// This is deliberately a straight line with no caching, no engine thread and no
/// format normalisation. Phase 1 replaces it with <c>FontEngine</c>, <c>FaceCache</c>,
/// <c>TextLayout</c> and <c>GlyphRasterizer</c>; the one thing it establishes for good
/// is the shape of the boundary — bytes in, BGRA out, no native handles escaping.
/// </remarks>
public static unsafe class FreeTypeSpike
{
    /// <summary>Renders <paramref name="text"/> through the font at <paramref name="fontPath"/>.</summary>
    /// <param name="fontPath">Any font FreeType can open; the spike is exercised with TrueType.</param>
    /// <param name="text">The string to shape and draw.</param>
    /// <param name="pixelSize">Em size in pixels (point size times DPI over 72).</param>
    /// <param name="foreground">Text colour as 0xAARRGGBB.</param>
    /// <param name="background">Background colour as 0xAARRGGBB.</param>
    /// <exception cref="FontLoadException">The file is unreadable, or FreeType rejected it.</exception>
    public static RenderedBitmap Render(
        string fontPath,
        string text,
        int pixelSize,
        uint foreground = 0xFF1A1A1Au,
        uint background = 0xFFFFFFFFu)
    {
        ArgumentException.ThrowIfNullOrEmpty(fontPath);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pixelSize, 4096);

        byte[] fontBytes = ReadWithoutLocking(fontPath);
        if (fontBytes.Length == 0)
            throw new FontLoadException("The file is empty.") { Path = fontPath };

        // The buffer must stay pinned for as long as the FT_Face and the HarfBuzz
        // blob point into it: both hold the raw address, not a copy.
        GCHandle pin = GCHandle.Alloc(fontBytes, GCHandleType.Pinned);
        FT_LibraryRec_* library = null;
        FT_FaceRec_* face = null;
        Blob? blob = null;
        Face? hbFace = null;
        Font? hbFont = null;
        HbBuffer? buffer = null;

        try
        {
            FT_Error err = FT_Init_FreeType(&library);
            if (err != FT_Error.FT_Err_Ok)
                throw new FontLoadException($"FreeType could not initialise ({err}).") { Path = fontPath };

            byte* fontBase = (byte*)pin.AddrOfPinnedObject();

            err = FT_New_Memory_Face(library, fontBase, (nint)fontBytes.Length, (nint)0, &face);
            if (err != FT_Error.FT_Err_Ok)
                throw new FontLoadException($"FreeType could not open this font ({err}).") { Path = fontPath };

            err = FT_Set_Pixel_Sizes(face, 0, (uint)pixelSize);
            if (err != FT_Error.FT_Err_Ok)
                throw new FontLoadException($"This font has no size {pixelSize} px ({err}).") { Path = fontPath };

            // HarfBuzz reads the same bytes to shape the string: kerning, ligatures
            // and the correct glyph ids, rather than a naive character-to-glyph map.
            blob = new Blob((nint)fontBase, fontBytes.Length, MemoryMode.ReadOnly);
            hbFace = new Face(blob, 0);
            hbFont = new Font(hbFace);
            hbFont.SetScale(pixelSize * 64, pixelSize * 64);

            buffer = new HbBuffer();
            buffer.AddUtf16(text);
            buffer.GuessSegmentProperties();
            hbFont.Shape(buffer);

            ReadOnlySpan<GlyphInfo> infos = buffer.GetGlyphInfoSpan();
            ReadOnlySpan<GlyphPosition> positions = buffer.GetGlyphPositionSpan();

            // Pass 1: rasterize each glyph and record where its ink lands, relative to
            // the pen on the baseline. Coverage is copied out because FreeType reuses
            // one glyph slot for every glyph.
            var inked = new List<InkedGlyph>(infos.Length);
            int penX = 0, advance = 0;
            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;

            for (int i = 0; i < infos.Length; i++)
            {
                uint glyphIndex = infos[i].Codepoint;   // after shaping this is a glyph id, not a character
                GlyphPosition pos = positions[i];

                if (FT_Load_Glyph(face, glyphIndex, FT_LOAD.FT_LOAD_DEFAULT) == FT_Error.FT_Err_Ok)
                {
                    FT_GlyphSlotRec_* slot = face->glyph;
                    if (FT_Render_Glyph(slot, FT_Render_Mode_.FT_RENDER_MODE_NORMAL) == FT_Error.FT_Err_Ok)
                    {
                        int w = (int)slot->bitmap.width;
                        int h = (int)slot->bitmap.rows;
                        if (w > 0 && h > 0)
                        {
                            byte[] coverage = ExtractCoverage(in slot->bitmap, fontPath);

                            // Screen coordinates run downwards; HarfBuzz offsets run upwards.
                            int left = penX + Round26Dot6(pos.XOffset) + slot->bitmap_left;
                            int top = -Round26Dot6(pos.YOffset) - slot->bitmap_top;

                            inked.Add(new InkedGlyph(coverage, w, h, left, top));
                            minX = Math.Min(minX, left);
                            maxX = Math.Max(maxX, left + w);
                            minY = Math.Min(minY, top);
                            maxY = Math.Max(maxY, top + h);
                        }
                    }
                }

                penX += Round26Dot6(pos.XAdvance);
                advance = penX;
            }

            int padding = Math.Max(2, pixelSize / 8);

            // A string of only blanks still deserves a correctly sized, empty image.
            if (inked.Count == 0)
            {
                int blankWidth = Math.Max(1, advance) + 2 * padding;
                int blankHeight = pixelSize + 2 * padding;
                return new RenderedBitmap(
                    FillBackground(blankWidth, blankHeight, background),
                    blankWidth, blankHeight, blankWidth * 4, infos.Length);
            }

            int width = (maxX - minX) + 2 * padding;
            int height = (maxY - minY) + 2 * padding;
            int originX = padding - minX;
            int originY = padding - minY;
            int stride = width * 4;

            // Pass 2: composite coverage over the background.
            byte[] pixels = FillBackground(width, height, background);
            byte fgB = (byte)(foreground & 0xFF);
            byte fgG = (byte)((foreground >> 8) & 0xFF);
            byte fgR = (byte)((foreground >> 16) & 0xFF);

            foreach (InkedGlyph glyph in inked)
            {
                for (int y = 0; y < glyph.Height; y++)
                {
                    int targetY = originY + glyph.Top + y;
                    if (targetY < 0 || targetY >= height) continue;

                    int rowStart = y * glyph.Width;
                    for (int x = 0; x < glyph.Width; x++)
                    {
                        byte alpha = glyph.Coverage[rowStart + x];
                        if (alpha == 0) continue;

                        int targetX = originX + glyph.Left + x;
                        if (targetX < 0 || targetX >= width) continue;

                        int p = targetY * stride + targetX * 4;
                        pixels[p + 0] = Blend(pixels[p + 0], fgB, alpha);
                        pixels[p + 1] = Blend(pixels[p + 1], fgG, alpha);
                        pixels[p + 2] = Blend(pixels[p + 2], fgR, alpha);
                        pixels[p + 3] = 0xFF;
                    }
                }
            }

            return new RenderedBitmap(pixels, width, height, stride, infos.Length);
        }
        finally
        {
            // Release in the reverse of acquisition: HarfBuzz first, then FreeType,
            // and only then unpin the bytes both of them were reading.
            buffer?.Dispose();
            hbFont?.Dispose();
            hbFace?.Dispose();
            blob?.Dispose();
            if (face != null) FT_Done_Face(face);
            if (library != null) FT_Done_FreeType(library);
            pin.Free();
        }
    }

    /// <summary>The family and style FreeType reports, used to prove the face really opened.</summary>
    public static (string Family, string Style, long GlyphCount) ReadFaceSummary(string fontPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fontPath);

        byte[] fontBytes = ReadWithoutLocking(fontPath);
        GCHandle pin = GCHandle.Alloc(fontBytes, GCHandleType.Pinned);
        FT_LibraryRec_* library = null;
        FT_FaceRec_* face = null;
        try
        {
            if (FT_Init_FreeType(&library) != FT_Error.FT_Err_Ok)
                throw new FontLoadException("FreeType could not initialise.") { Path = fontPath };

            FT_Error err = FT_New_Memory_Face(
                library, (byte*)pin.AddrOfPinnedObject(), (nint)fontBytes.Length, (nint)0, &face);
            if (err != FT_Error.FT_Err_Ok)
                throw new FontLoadException($"FreeType could not open this font ({err}).") { Path = fontPath };

            string family = Marshal.PtrToStringAnsi((nint)face->family_name) ?? string.Empty;
            string style = Marshal.PtrToStringAnsi((nint)face->style_name) ?? string.Empty;
            return (family, style, face->num_glyphs);
        }
        finally
        {
            if (face != null) FT_Done_Face(face);
            if (library != null) FT_Done_FreeType(library);
            pin.Free();
        }
    }

    /// <summary>
    /// Reads the whole file into memory, sharing it with every other writer and deleter.
    /// </summary>
    /// <remarks>
    /// This is the decision that makes un-favouriting a font that is currently on screen
    /// work: the app never holds a handle to a font file past this call.
    /// </remarks>
    private static byte[] ReadWithoutLocking(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,  // one read of the whole file; no streaming buffer needed
                FileOptions.SequentialScan);

            long length = stream.Length;
            if (length > 256L * 1024 * 1024)
                throw new FontLoadException($"The file is too large to be a font ({length / (1024 * 1024)} MB).") { Path = path };

            byte[] bytes = new byte[length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new FontLoadException($"The file could not be read: {ex.Message}", ex) { Path = path };
        }
    }

    /// <summary>Copies one rendered glyph out of FreeType's shared slot as 8-bit coverage.</summary>
    private static byte[] ExtractCoverage(in FT_Bitmap_ bitmap, string path)
    {
        int width = (int)bitmap.width;
        int rows = (int)bitmap.rows;
        byte[] coverage = new byte[width * rows];

        // A negative pitch means the rows are stored bottom-up.
        int pitch = bitmap.pitch;
        bool bottomUp = pitch < 0;
        int absPitch = Math.Abs(pitch);
        byte* start = bitmap.buffer;
        if (bottomUp) start += (long)absPitch * (rows - 1);

        switch (bitmap.pixel_mode)
        {
            case FT_Pixel_Mode_.FT_PIXEL_MODE_GRAY:
                for (int y = 0; y < rows; y++)
                {
                    byte* row = start + (bottomUp ? -(long)absPitch * y : (long)absPitch * y);
                    new ReadOnlySpan<byte>(row, width).CopyTo(coverage.AsSpan(y * width, width));
                }
                break;

            case FT_Pixel_Mode_.FT_PIXEL_MODE_MONO:
                // 1 bit per pixel, most significant bit leftmost. Bitmap fonts land here.
                for (int y = 0; y < rows; y++)
                {
                    byte* row = start + (bottomUp ? -(long)absPitch * y : (long)absPitch * y);
                    for (int x = 0; x < width; x++)
                    {
                        int bit = (row[x >> 3] >> (7 - (x & 7))) & 1;
                        coverage[y * width + x] = bit != 0 ? (byte)0xFF : (byte)0x00;
                    }
                }
                break;

            default:
                // GRAY2/GRAY4/LCD/BGRA arrive in Phase 1 with the real rasterizer.
                throw new FontLoadException(
                    $"Pixel mode {bitmap.pixel_mode} is not handled yet.") { Path = path };
        }

        return coverage;
    }

    private static byte[] FillBackground(int width, int height, uint argb)
    {
        byte[] pixels = new byte[width * height * 4];
        byte b = (byte)(argb & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte a = (byte)((argb >> 24) & 0xFF);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }
        return pixels;
    }

    /// <summary>Source over destination at the given 8-bit coverage.</summary>
    private static byte Blend(byte destination, byte source, byte alpha)
        => (byte)((source * alpha + destination * (255 - alpha) + 127) / 255);

    /// <summary>26.6 fixed point to whole pixels, rounded rather than truncated.</summary>
    private static int Round26Dot6(int value) => (value + 32) >> 6;

    private readonly record struct InkedGlyph(byte[] Coverage, int Width, int Height, int Left, int Top);
}
