using FreeTypeSharp;
using TypographyFontFileViewer.Core;
using static FreeTypeSharp.FT;

namespace TypographyFontFileViewer.Engine;

/// <summary>
/// Draws laid-out glyphs into a BGRA buffer.
/// </summary>
/// <remarks>
/// Handles every pixel format FreeType produces: 8-bit coverage from outlines, 1-bit
/// from bitmap fonts, the 2- and 4-bit forms some BDF and PCF files use, and
/// premultiplied BGRA from colour fonts such as the emoji face.
/// </remarks>
internal static unsafe class GlyphRasterizer
{
    /// <summary>One glyph's pixels, copied out of FreeType's shared slot.</summary>
    private readonly record struct GlyphBitmap(
        byte[] Pixels, int Width, int Height, int Left, int Top, bool IsColor);

    /// <summary>
    /// Renders a laid-out string.
    /// </summary>
    /// <param name="library">The engine thread's FreeType library, for bitmap conversion.</param>
    /// <param name="face">The face the text was laid out through, at the right size.</param>
    /// <param name="layout">Glyph positions from <see cref="TextLayout"/>.</param>
    /// <param name="style">Text and background colours.</param>
    public static RenderedText Render(
        FT_LibraryRec_* library, LoadedFace face, LaidOutText layout, RenderStyle style)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(layout);

        int padding = Math.Max(2, face.CurrentPixelSize / 8);

        // Pass 1: rasterize every glyph and find where the ink actually falls.
        var bitmaps = new List<(GlyphBitmap Bitmap, int X, int Y)>(layout.Glyphs.Count);
        int inkLeft = int.MaxValue, inkRight = int.MinValue;
        int inkTop = int.MaxValue, inkBottom = int.MinValue;

        foreach (PositionedGlyph glyph in layout.Glyphs)
        {
            GlyphBitmap? rendered = RenderGlyph(library, face, glyph.GlyphIndex);
            if (rendered is not { } bitmap) continue;

            int x = TextLayout.Round26(glyph.PenX26) + bitmap.Left;
            int y = TextLayout.Round26(glyph.PenY26) - bitmap.Top;

            bitmaps.Add((bitmap, x, y));
            inkLeft = Math.Min(inkLeft, x);
            inkRight = Math.Max(inkRight, x + bitmap.Width);
            inkTop = Math.Min(inkTop, y);
            inkBottom = Math.Max(inkBottom, y + bitmap.Height);
        }

        // The canvas covers the line boxes as well as the ink, so a preview does not
        // jump around as the text changes, and tall glyphs are never clipped.
        int boxLeft = 0;
        int boxRight = Math.Max(1, layout.AdvanceWidth);
        int boxTop = -layout.Ascent;
        int boxBottom = (layout.LineCount - 1) * layout.LineHeight + layout.Descent;

        if (bitmaps.Count > 0)
        {
            boxLeft = Math.Min(boxLeft, inkLeft);
            boxRight = Math.Max(boxRight, inkRight);
            boxTop = Math.Min(boxTop, inkTop);
            boxBottom = Math.Max(boxBottom, inkBottom);
        }

        int width = Math.Max(1, boxRight - boxLeft) + 2 * padding;
        int height = Math.Max(1, boxBottom - boxTop) + 2 * padding;

        // Keep a runaway font from asking for a gigabyte of canvas.
        const int MaximumDimension = 16384;
        width = Math.Min(width, MaximumDimension);
        height = Math.Min(height, MaximumDimension);

        int originX = padding - boxLeft;
        int originY = padding - boxTop;
        int stride = width * 4;
        byte[] pixels = FillBackground(width, height, style.Background);

        // Pass 2: composite.
        foreach ((GlyphBitmap bitmap, int x, int y) in bitmaps)
            Composite(pixels, width, height, stride, bitmap, originX + x, originY + y, style.Foreground);

        return new RenderedText
        {
            Bgra = pixels,
            Width = width,
            Height = height,
            GlyphCount = layout.Glyphs.Count,
            LineCount = layout.LineCount,
            HasMissingGlyphs = layout.HasMissingGlyphs,
        };
    }

    /// <summary>
    /// Renders one glyph centred in a square cell, for the glyph map.
    /// </summary>
    public static GlyphCellBitmap RenderCell(
        FT_LibraryRec_* library, LoadedFace face, uint glyphIndex, int codepoint,
        int cellPx, RenderStyle style)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellPx, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cellPx, 1024);

        int stride = cellPx * 4;
        byte[] pixels = FillBackground(cellPx, cellPx, style.Background);

        GlyphBitmap? rendered = RenderGlyph(library, face, glyphIndex);
        if (rendered is not { } bitmap || bitmap.Width == 0 || bitmap.Height == 0)
        {
            return new GlyphCellBitmap
            {
                GlyphIndex = glyphIndex,
                Codepoint = codepoint,
                Bgra = pixels,
                Width = cellPx,
                Height = cellPx,
                IsBlank = true,
            };
        }

        int x = (cellPx - bitmap.Width) / 2;
        int y = (cellPx - bitmap.Height) / 2;
        Composite(pixels, cellPx, cellPx, stride, bitmap, x, y, style.Foreground);

        return new GlyphCellBitmap
        {
            GlyphIndex = glyphIndex,
            Codepoint = codepoint,
            Bgra = pixels,
            Width = cellPx,
            Height = cellPx,
            IsBlank = false,
        };
    }

    // --- Rasterizing one glyph -------------------------------------------------

    private static GlyphBitmap? RenderGlyph(FT_LibraryRec_* library, LoadedFace face, uint glyphIndex)
    {
        // FT_LOAD_COLOR asks for the colour bitmap when the font has one; monochrome
        // fonts ignore it.
        if (FT_Load_Glyph(face.Face, glyphIndex, FT_LOAD.FT_LOAD_COLOR) != FT_Error.FT_Err_Ok)
            return null;

        FT_GlyphSlotRec_* slot = face.Face->glyph;

        if (slot->format != FT_Glyph_Format_.FT_GLYPH_FORMAT_BITMAP
            && FT_Render_Glyph(slot, FT_Render_Mode_.FT_RENDER_MODE_NORMAL) != FT_Error.FT_Err_Ok)
            return null;

        int width = (int)slot->bitmap.width;
        int height = (int)slot->bitmap.rows;
        if (width <= 0 || height <= 0) return null;

        return slot->bitmap.pixel_mode switch
        {
            FT_Pixel_Mode_.FT_PIXEL_MODE_BGRA
                => CopyColor(in slot->bitmap, slot->bitmap_left, slot->bitmap_top),

            FT_Pixel_Mode_.FT_PIXEL_MODE_GRAY
                => CopyGray(in slot->bitmap, slot->bitmap_left, slot->bitmap_top),

            // MONO, GRAY2 and GRAY4 are normalised through FreeType rather than
            // unpacked by hand: one code path, and it tracks the library's own rules.
            _ => ConvertThenCopy(library, in slot->bitmap, slot->bitmap_left, slot->bitmap_top),
        };
    }

    private static GlyphBitmap CopyGray(in FT_Bitmap_ bitmap, int left, int top)
    {
        int width = (int)bitmap.width;
        int height = (int)bitmap.rows;
        byte[] coverage = new byte[width * height];

        CopyRows(in bitmap, coverage, width, height, bytesPerPixel: 1);
        ScaleToFullRange(coverage, bitmap.num_grays);

        return new GlyphBitmap(coverage, width, height, left, top, IsColor: false);
    }

    private static GlyphBitmap CopyColor(in FT_Bitmap_ bitmap, int left, int top)
    {
        int width = (int)bitmap.width;
        int height = (int)bitmap.rows;
        byte[] bgra = new byte[width * height * 4];

        CopyRows(in bitmap, bgra, width, height, bytesPerPixel: 4);

        return new GlyphBitmap(bgra, width, height, left, top, IsColor: true);
    }

    private static GlyphBitmap? ConvertThenCopy(
        FT_LibraryRec_* library, in FT_Bitmap_ source, int left, int top)
    {
        FT_Bitmap_ target;
        FT_Bitmap_New(&target);
        try
        {
            fixed (FT_Bitmap_* sourcePointer = &source)
            {
                if (FT_Bitmap_Convert(library, sourcePointer, &target, 1) != FT_Error.FT_Err_Ok)
                    return null;
            }

            int width = (int)target.width;
            int height = (int)target.rows;
            if (width <= 0 || height <= 0) return null;

            byte[] coverage = new byte[width * height];
            CopyRows(in target, coverage, width, height, bytesPerPixel: 1);
            ScaleToFullRange(coverage, target.num_grays);

            return new GlyphBitmap(coverage, width, height, left, top, IsColor: false);
        }
        finally
        {
            FT_Bitmap_Done(library, &target);
        }
    }

    /// <summary>Copies rows out of FreeType's buffer, honouring a negative pitch.</summary>
    private static void CopyRows(
        in FT_Bitmap_ bitmap, byte[] destination, int width, int height, int bytesPerPixel)
    {
        int pitch = bitmap.pitch;
        int absolutePitch = Math.Abs(pitch);
        bool bottomUp = pitch < 0;

        byte* start = bitmap.buffer;
        if (bottomUp) start += (long)absolutePitch * (height - 1);

        int rowBytes = width * bytesPerPixel;
        for (int y = 0; y < height; y++)
        {
            byte* row = start + (bottomUp ? -(long)absolutePitch * y : (long)absolutePitch * y);
            new ReadOnlySpan<byte>(row, rowBytes).CopyTo(destination.AsSpan(y * rowBytes, rowBytes));
        }
    }

    /// <summary>
    /// Stretches coverage to 0–255. A converted 1-bit bitmap arrives as 0 and 1, a
    /// 4-bit one as 0–15; both must reach full black before being composited.
    /// </summary>
    private static void ScaleToFullRange(byte[] coverage, ushort numGrays)
    {
        int levels = numGrays;
        if (levels is <= 1 or >= 256) return;    // already 0–255, or nothing to scale

        int maximum = levels - 1;
        for (int i = 0; i < coverage.Length; i++)
            coverage[i] = (byte)((coverage[i] * 255 + maximum / 2) / maximum);
    }

    // --- Compositing -----------------------------------------------------------

    private static void Composite(
        byte[] pixels, int width, int height, int stride,
        GlyphBitmap glyph, int x, int y, uint foreground)
    {
        byte foregroundB = (byte)(foreground & 0xFF);
        byte foregroundG = (byte)((foreground >> 8) & 0xFF);
        byte foregroundR = (byte)((foreground >> 16) & 0xFF);

        for (int row = 0; row < glyph.Height; row++)
        {
            int targetY = y + row;
            if (targetY < 0 || targetY >= height) continue;

            for (int column = 0; column < glyph.Width; column++)
            {
                int targetX = x + column;
                if (targetX < 0 || targetX >= width) continue;

                int destination = targetY * stride + targetX * 4;

                if (glyph.IsColor)
                {
                    // Already premultiplied by FreeType: source over destination.
                    int source = (row * glyph.Width + column) * 4;
                    byte alpha = glyph.Pixels[source + 3];
                    if (alpha == 0) continue;

                    int inverse = 255 - alpha;
                    pixels[destination + 0] = (byte)(glyph.Pixels[source + 0] + pixels[destination + 0] * inverse / 255);
                    pixels[destination + 1] = (byte)(glyph.Pixels[source + 1] + pixels[destination + 1] * inverse / 255);
                    pixels[destination + 2] = (byte)(glyph.Pixels[source + 2] + pixels[destination + 2] * inverse / 255);
                    pixels[destination + 3] = 0xFF;
                }
                else
                {
                    byte alpha = glyph.Pixels[row * glyph.Width + column];
                    if (alpha == 0) continue;

                    pixels[destination + 0] = Blend(pixels[destination + 0], foregroundB, alpha);
                    pixels[destination + 1] = Blend(pixels[destination + 1], foregroundG, alpha);
                    pixels[destination + 2] = Blend(pixels[destination + 2], foregroundR, alpha);
                    pixels[destination + 3] = 0xFF;
                }
            }
        }
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

    private static byte Blend(byte destination, byte source, byte alpha)
        => (byte)((source * alpha + destination * (255 - alpha) + 127) / 255);
}
