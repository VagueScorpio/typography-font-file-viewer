using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Engine.Woff;

/// <summary>A rebuilt <c>glyf</c> table and the <c>loca</c> index that goes with it.</summary>
internal sealed class ReconstructedGlyf
{
    public required byte[] Glyf { get; init; }
    public required byte[] Loca { get; init; }

    /// <summary>Always 1: this reconstruction writes the long <c>loca</c> format.</summary>
    public required short IndexToLocFormat { get; init; }

    public required ushort NumGlyphs { get; init; }

    /// <summary>Each glyph's xMin, which a transformed <c>hmtx</c> needs to rebuild its lsb array.</summary>
    public required short[] XMin { get; init; }
}

/// <summary>
/// Rebuilds the <c>glyf</c> and <c>loca</c> tables from the transformed form WOFF 2.0
/// stores them in.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of WOFF 2.0 that does real work. Rather than simply compressing the
/// outline table, the format takes it apart into seven parallel streams — contour
/// counts, point counts, flags, coordinates, composites, bounding boxes and hinting
/// instructions — so that like data sits next to like and Brotli has something to chew
/// on. Point coordinates are further packed by a 128-entry triplet code that squeezes
/// the common small deltas into a single byte.
/// </para>
/// <para>
/// Everything here is bounds-checked. The streams come from a compressed file that may
/// be damaged or deliberately malformed, and a count read out of one stream is used to
/// index another.
/// </para>
/// </remarks>
internal static class Woff2GlyfReconstructor
{
    private const int TransformHeaderSize = 36;

    /// <summary>Set in optionFlags when a trailing overlap-simple bitmap is present.</summary>
    private const ushort OverlapSimpleBitmapFlag = 0x0001;

    // Composite glyph component flags, from the OpenType specification.
    private const int ArgsAreWords = 0x0001;
    private const int HaveScale = 0x0008;
    private const int MoreComponents = 0x0020;
    private const int HaveXAndYScale = 0x0040;
    private const int HaveTwoByTwo = 0x0080;
    private const int HaveInstructions = 0x0100;

    // Simple glyph point flags.
    private const byte OnCurvePoint = 0x01;
    private const byte OverlapSimple = 0x40;

    /// <summary>Rebuilds <c>glyf</c> and <c>loca</c> from a transformed glyf table.</summary>
    /// <exception cref="FontLoadException">The transformed data is malformed.</exception>
    public static ReconstructedGlyf Reconstruct(ReadOnlySpan<byte> transformed, string? path = null)
    {
        var header = new ByteReader(transformed, path);

        header.Skip(2);                                   // reserved
        ushort optionFlags = header.ReadUInt16();
        ushort numGlyphs = header.ReadUInt16();
        header.ReadUInt16();                              // indexFormat; this rebuild always writes long

        uint nContourSize = header.ReadUInt32();
        uint nPointsSize = header.ReadUInt32();
        uint flagSize = header.ReadUInt32();
        uint glyphSize = header.ReadUInt32();
        uint compositeSize = header.ReadUInt32();
        uint bboxSize = header.ReadUInt32();
        uint instructionSize = header.ReadUInt32();

        long declared = (long)nContourSize + nPointsSize + flagSize + glyphSize
                      + compositeSize + bboxSize + instructionSize;

        if (declared > transformed.Length - TransformHeaderSize)
            throw new FontLoadException(
                $"The transformed glyf streams claim {declared} bytes but only "
                + $"{transformed.Length - TransformHeaderSize} are present.")
            { Path = path };

        // Carve the seven streams out back to back, in the order the header lists them.
        int nContourAt = TransformHeaderSize;
        int nPointsAt = nContourAt + (int)nContourSize;
        int flagAt = nPointsAt + (int)nPointsSize;
        int glyphAt = flagAt + (int)flagSize;
        int compositeAt = glyphAt + (int)glyphSize;
        int bboxAt = compositeAt + (int)compositeSize;
        int instructionAt = bboxAt + (int)bboxSize;
        int cursor = instructionAt + (int)instructionSize;

        var nContourStream = new ByteReader(transformed.Slice(nContourAt, (int)nContourSize), path);
        var nPointsStream = new ByteReader(transformed.Slice(nPointsAt, (int)nPointsSize), path);
        var flagStream = new ByteReader(transformed.Slice(flagAt, (int)flagSize), path);
        var glyphStream = new ByteReader(transformed.Slice(glyphAt, (int)glyphSize), path);
        var compositeStream = new ByteReader(transformed.Slice(compositeAt, (int)compositeSize), path);
        var bboxStream = new ByteReader(transformed.Slice(bboxAt, (int)bboxSize), path);
        var instructionStream = new ByteReader(transformed.Slice(instructionAt, (int)instructionSize), path);

        // The bbox stream opens with one bit per glyph saying whether that glyph carries
        // an explicit bounding box; the boxes themselves follow.
        //
        // That bitmap is padded to a four-byte boundary, not merely to a whole byte. The
        // difference only shows up for glyph counts that are not a multiple of 32, and
        // then only as a one-byte shift in every bounding box that follows — which looks
        // like plausible-but-wrong composite glyph positions rather than an obvious
        // failure. Inter, at 2,933 glyphs, needs 368 bytes here; rounding to a byte would
        // give 367.
        int bitmapBytes = ((numGlyphs + 31) / 32) * 4;
        if (bboxSize < (uint)bitmapBytes)
            throw new FontLoadException("The bounding-box stream is too small for its bitmap.")
            { Path = path };

        byte[] bboxBitmap = bboxStream.ReadBytes(bitmapBytes).ToArray();

        ReadOnlySpan<byte> overlapBitmap = default;
        if ((optionFlags & OverlapSimpleBitmapFlag) != 0)
        {
            int overlapBytes = (numGlyphs + 7) / 8;
            if (cursor + overlapBytes > transformed.Length)
                throw new FontLoadException("The overlap-simple bitmap runs past the end of the table.")
                { Path = path };
            overlapBitmap = transformed.Slice(cursor, overlapBytes);
        }

        var glyf = new System.IO.MemoryStream(Math.Max(1024, numGlyphs * 64));
        uint[] locaOffsets = new uint[numGlyphs + 1];
        short[] xMins = new short[numGlyphs];

        for (int glyphIndex = 0; glyphIndex < numGlyphs; glyphIndex++)
        {
            locaOffsets[glyphIndex] = (uint)glyf.Length;

            bool hasExplicitBbox = (bboxBitmap[glyphIndex >> 3] & (0x80 >> (glyphIndex & 7))) != 0;
            short numberOfContours = nContourStream.ReadInt16();

            byte[] glyphData;
            if (numberOfContours == 0)
            {
                // An empty glyph, such as the space: no outline, no entry at all.
                if (hasExplicitBbox)
                    throw new FontLoadException(
                        $"Glyph {glyphIndex} is empty but claims a bounding box.")
                    { Path = path };

                glyphData = [];
            }
            else if (numberOfContours < 0)
            {
                glyphData = ReadComposite(
                    glyphIndex, hasExplicitBbox, ref compositeStream, ref bboxStream,
                    ref glyphStream, ref instructionStream, out short xMin, path);
                xMins[glyphIndex] = xMin;
            }
            else
            {
                bool overlaps = overlapBitmap.Length > 0
                    && (overlapBitmap[glyphIndex >> 3] & (0x80 >> (glyphIndex & 7))) != 0;

                glyphData = ReadSimple(
                    glyphIndex, numberOfContours, hasExplicitBbox, overlaps,
                    ref nPointsStream, ref flagStream, ref glyphStream,
                    ref bboxStream, ref instructionStream, out short xMin, path);
                xMins[glyphIndex] = xMin;
            }

            glyf.Write(glyphData, 0, glyphData.Length);

            // Glyphs are kept 4-byte aligned, which every offset below then is too.
            int padding = SfntWriter.Align4(glyphData.Length) - glyphData.Length;
            for (int i = 0; i < padding; i++) glyf.WriteByte(0);

            if (glyf.Length > SfntWriter.MaximumFontSize)
                throw new FontLoadException("The rebuilt glyf table grew implausibly large.")
                { Path = path };
        }

        locaOffsets[numGlyphs] = (uint)glyf.Length;

        // The long loca format, always: it cannot overflow, and it sidesteps the short
        // format's requirement that every offset be even.
        byte[] loca = new byte[(numGlyphs + 1) * 4];
        for (int i = 0; i <= numGlyphs; i++)
            SfntWriter.WriteUInt32(loca, i * 4, locaOffsets[i]);

        return new ReconstructedGlyf
        {
            Glyf = glyf.ToArray(),
            Loca = loca,
            IndexToLocFormat = 1,
            NumGlyphs = numGlyphs,
            XMin = xMins,
        };
    }

    // --- Simple glyphs ---------------------------------------------------------

    private static byte[] ReadSimple(
        int glyphIndex, short numberOfContours, bool hasExplicitBbox, bool overlaps,
        ref ByteReader nPointsStream, ref ByteReader flagStream, ref ByteReader glyphStream,
        ref ByteReader bboxStream, ref ByteReader instructionStream,
        out short xMin, string? path)
    {
        // Point counts are per contour; the end-point indices follow from the running total.
        ushort[] endPoints = new ushort[numberOfContours];
        int totalPoints = 0;

        for (int contour = 0; contour < numberOfContours; contour++)
        {
            int pointsInContour = nPointsStream.Read255UInt16();
            totalPoints += pointsInContour;

            if (totalPoints > ushort.MaxValue)
                throw new FontLoadException(
                    $"Glyph {glyphIndex} declares more than 65535 points.")
                { Path = path };

            endPoints[contour] = (ushort)(totalPoints - 1);
        }

        var deltaX = new short[totalPoints];
        var deltaY = new short[totalPoints];
        var onCurve = new bool[totalPoints];

        int x = 0, y = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        for (int i = 0; i < totalPoints; i++)
        {
            (int dx, int dy, bool isOnCurve) = DecodeTriplet(ref flagStream, ref glyphStream);

            deltaX[i] = ClampToInt16(dx, glyphIndex, path);
            deltaY[i] = ClampToInt16(dy, glyphIndex, path);
            onCurve[i] = isOnCurve;

            x += dx;
            y += dy;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        short bboxXMin, bboxYMin, bboxXMax, bboxYMax;
        if (hasExplicitBbox)
        {
            bboxXMin = bboxStream.ReadInt16();
            bboxYMin = bboxStream.ReadInt16();
            bboxXMax = bboxStream.ReadInt16();
            bboxYMax = bboxStream.ReadInt16();
        }
        else if (totalPoints == 0)
        {
            bboxXMin = bboxYMin = bboxXMax = bboxYMax = 0;
        }
        else
        {
            // No explicit box means it is exactly the extent of the points.
            bboxXMin = ClampToInt16(minX, glyphIndex, path);
            bboxYMin = ClampToInt16(minY, glyphIndex, path);
            bboxXMax = ClampToInt16(maxX, glyphIndex, path);
            bboxYMax = ClampToInt16(maxY, glyphIndex, path);
        }

        xMin = bboxXMin;

        ushort instructionLength = glyphStream.Read255UInt16();
        ReadOnlySpan<byte> instructions = instructionStream.ReadBytes(instructionLength);

        // Write the glyph in the plain, uncompressed form: one flag byte per point and
        // 16-bit deltas throughout. Re-deriving the shortest encoding would save bytes
        // in a buffer that only FreeType ever reads.
        int size = 10                              // header and bounding box
                 + numberOfContours * 2            // end points
                 + 2 + instructionLength           // hinting
                 + totalPoints                     // flags
                 + totalPoints * 4;                // x and y, 16 bits each

        byte[] glyph = new byte[size];
        int offset = 0;

        SfntWriter.WriteInt16(glyph, offset, numberOfContours); offset += 2;
        SfntWriter.WriteInt16(glyph, offset, bboxXMin); offset += 2;
        SfntWriter.WriteInt16(glyph, offset, bboxYMin); offset += 2;
        SfntWriter.WriteInt16(glyph, offset, bboxXMax); offset += 2;
        SfntWriter.WriteInt16(glyph, offset, bboxYMax); offset += 2;

        foreach (ushort endPoint in endPoints)
        {
            SfntWriter.WriteUInt16(glyph, offset, endPoint);
            offset += 2;
        }

        SfntWriter.WriteUInt16(glyph, offset, instructionLength); offset += 2;
        instructions.CopyTo(glyph.AsSpan(offset)); offset += instructionLength;

        for (int i = 0; i < totalPoints; i++)
        {
            byte flag = onCurve[i] ? OnCurvePoint : (byte)0;

            // The overlap flag lives on the first point of the glyph.
            if (i == 0 && overlaps) flag |= OverlapSimple;

            glyph[offset++] = flag;
        }

        for (int i = 0; i < totalPoints; i++)
        {
            SfntWriter.WriteInt16(glyph, offset, deltaX[i]);
            offset += 2;
        }

        for (int i = 0; i < totalPoints; i++)
        {
            SfntWriter.WriteInt16(glyph, offset, deltaY[i]);
            offset += 2;
        }

        return glyph;
    }

    /// <summary>
    /// Decodes one point from the flag and coordinate streams.
    /// </summary>
    /// <remarks>
    /// The flag's top bit says whether the point is off-curve; the low seven bits select
    /// one of 128 encodings, which between them cover a delta needing anywhere from one
    /// to four further bytes. The ranges below are the specification's table, written
    /// out as the arithmetic that generates it.
    /// </remarks>
    private static (int DeltaX, int DeltaY, bool OnCurve) DecodeTriplet(
        ref ByteReader flagStream, ref ByteReader glyphStream)
    {
        byte flagByte = flagStream.ReadUInt8();
        bool onCurve = (flagByte & 0x80) == 0;
        int code = flagByte & 0x7F;

        int dx, dy;

        if (code < 10)
        {
            // Purely vertical, 1 further byte.
            dx = 0;
            dy = WithSign(code, ((code & 14) << 7) + glyphStream.ReadUInt8());
        }
        else if (code < 20)
        {
            // Purely horizontal, 1 further byte.
            dx = WithSign(code, (((code - 10) & 14) << 7) + glyphStream.ReadUInt8());
            dy = 0;
        }
        else if (code < 84)
        {
            // Both small, packed into 1 further byte.
            int b0 = code - 20;
            int b1 = glyphStream.ReadUInt8();
            dx = WithSign(code, 1 + (b0 & 0x30) + (b1 >> 4));
            dy = WithSign(code >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F));
        }
        else if (code < 120)
        {
            // Both medium, 2 further bytes.
            int b0 = code - 84;
            int b1 = glyphStream.ReadUInt8();
            int b2 = glyphStream.ReadUInt8();
            dx = WithSign(code, 1 + ((b0 / 12) << 8) + b1);
            dy = WithSign(code >> 1, 1 + (((b0 % 12) >> 2) << 8) + b2);
        }
        else if (code < 124)
        {
            // Both large, 3 further bytes shared between them.
            int b1 = glyphStream.ReadUInt8();
            int b2 = glyphStream.ReadUInt8();
            int b3 = glyphStream.ReadUInt8();
            dx = WithSign(code, (b1 << 4) + (b2 >> 4));
            dy = WithSign(code >> 1, ((b2 & 0x0F) << 8) + b3);
        }
        else
        {
            // Both very large, 4 further bytes.
            int b1 = glyphStream.ReadUInt8();
            int b2 = glyphStream.ReadUInt8();
            int b3 = glyphStream.ReadUInt8();
            int b4 = glyphStream.ReadUInt8();
            dx = WithSign(code, (b1 << 8) + b2);
            dy = WithSign(code >> 1, (b3 << 8) + b4);
        }

        return (dx, dy, onCurve);
    }

    /// <summary>Bit 0 of the (possibly shifted) code carries the sign.</summary>
    private static int WithSign(int code, int value) => (code & 1) != 0 ? value : -value;

    // --- Composite glyphs ------------------------------------------------------

    private static byte[] ReadComposite(
        int glyphIndex, bool hasExplicitBbox,
        ref ByteReader compositeStream, ref ByteReader bboxStream,
        ref ByteReader glyphStream, ref ByteReader instructionStream,
        out short xMin, string? path)
    {
        // A composite glyph has no points of its own, so its box cannot be derived.
        if (!hasExplicitBbox)
            throw new FontLoadException(
                $"Composite glyph {glyphIndex} has no bounding box, which the format requires.")
            { Path = path };

        short bboxXMin = bboxStream.ReadInt16();
        short bboxYMin = bboxStream.ReadInt16();
        short bboxXMax = bboxStream.ReadInt16();
        short bboxYMax = bboxStream.ReadInt16();
        xMin = bboxXMin;

        int start = compositeStream.Position;
        bool haveInstructions = MeasureComposite(ref compositeStream, glyphIndex, path);
        int componentBytes = compositeStream.Position - start;

        compositeStream.Position = start;
        ReadOnlySpan<byte> components = compositeStream.ReadBytes(componentBytes);

        ushort instructionLength = 0;
        ReadOnlySpan<byte> instructions = default;
        if (haveInstructions)
        {
            instructionLength = glyphStream.Read255UInt16();
            instructions = instructionStream.ReadBytes(instructionLength);
        }

        int size = 10 + componentBytes + (haveInstructions ? 2 + instructionLength : 0);
        byte[] glyph = new byte[size];
        int offset = 0;

        SfntWriter.WriteInt16(glyph, offset, -1); offset += 2;      // composite marker
        SfntWriter.WriteInt16(glyph, offset, bboxXMin); offset += 2;
        SfntWriter.WriteInt16(glyph, offset, bboxYMin); offset += 2;
        SfntWriter.WriteInt16(glyph, offset, bboxXMax); offset += 2;
        SfntWriter.WriteInt16(glyph, offset, bboxYMax); offset += 2;

        components.CopyTo(glyph.AsSpan(offset)); offset += componentBytes;

        if (haveInstructions)
        {
            SfntWriter.WriteUInt16(glyph, offset, instructionLength); offset += 2;
            instructions.CopyTo(glyph.AsSpan(offset));
        }

        return glyph;
    }

    /// <summary>
    /// Walks the component records to find where they end, and whether hinting follows.
    /// </summary>
    /// <remarks>
    /// The component data is copied through unchanged, but its length is not stored
    /// anywhere: it has to be derived by stepping over each record, whose size depends on
    /// its own flags.
    /// </remarks>
    private static bool MeasureComposite(ref ByteReader stream, int glyphIndex, string? path)
    {
        bool haveInstructions = false;
        int components = 0;
        int flags;

        do
        {
            if (++components > 1000)
                throw new FontLoadException(
                    $"Composite glyph {glyphIndex} has an implausible number of components.")
                { Path = path };

            flags = stream.ReadUInt16();
            stream.Skip(2);                                      // the component's glyph index

            stream.Skip((flags & ArgsAreWords) != 0 ? 4 : 2);    // placement arguments

            if ((flags & HaveScale) != 0) stream.Skip(2);
            else if ((flags & HaveXAndYScale) != 0) stream.Skip(4);
            else if ((flags & HaveTwoByTwo) != 0) stream.Skip(8);

            if ((flags & HaveInstructions) != 0) haveInstructions = true;
        }
        while ((flags & MoreComponents) != 0);

        return haveInstructions;
    }

    private static short ClampToInt16(int value, int glyphIndex, string? path)
    {
        if (value is < short.MinValue or > short.MaxValue)
            throw new FontLoadException(
                $"Glyph {glyphIndex} has a coordinate of {value}, outside the 16-bit range "
                + "a TrueType outline allows.")
            { Path = path };

        return (short)value;
    }
}
