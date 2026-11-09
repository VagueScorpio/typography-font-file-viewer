using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Engine.Woff;

/// <summary>
/// A bounds-checked big-endian cursor over font bytes.
/// </summary>
/// <remarks>
/// Font files arrive from the internet and from strangers. Every read here is checked
/// against the end of the buffer and throws <see cref="FontLoadException"/> rather than
/// reading past it, so a truncated or hostile file produces an error card instead of an
/// access violation.
/// </remarks>
internal ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly string? _path;

    public ByteReader(ReadOnlySpan<byte> data, string? path = null)
    {
        _data = data;
        _path = path;
        Position = 0;
    }

    public int Position { get; set; }

    public int Length => _data.Length;

    public int Remaining => _data.Length - Position;

    public bool AtEnd => Position >= _data.Length;

    private void Require(int count)
    {
        if (count < 0 || Position > _data.Length - count)
            throw new FontLoadException(
                $"The font data ends unexpectedly: wanted {count} bytes at offset {Position}, " +
                $"but only {Math.Max(0, _data.Length - Position)} remain.")
            { Path = _path };
    }

    public byte ReadUInt8()
    {
        Require(1);
        return _data[Position++];
    }

    public ushort ReadUInt16()
    {
        Require(2);
        ushort value = (ushort)((_data[Position] << 8) | _data[Position + 1]);
        Position += 2;
        return value;
    }

    public short ReadInt16() => (short)ReadUInt16();

    public uint ReadUInt32()
    {
        Require(4);
        uint value = ((uint)_data[Position] << 24)
                   | ((uint)_data[Position + 1] << 16)
                   | ((uint)_data[Position + 2] << 8)
                   | _data[Position + 3];
        Position += 4;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Require(count);
        ReadOnlySpan<byte> slice = _data.Slice(Position, count);
        Position += count;
        return slice;
    }

    public void Skip(int count)
    {
        Require(count);
        Position += count;
    }

    /// <summary>
    /// Reads the variable-length unsigned integer WOFF 2.0 uses for table lengths.
    /// </summary>
    /// <remarks>
    /// Seven bits per byte, most significant first, high bit set on every byte but the
    /// last. The spec forbids leading zeros and values that overflow 32 bits, and both
    /// are rejected here: a file that encodes one is malformed, not merely unusual.
    /// </remarks>
    public uint ReadUIntBase128()
    {
        uint result = 0;

        for (int i = 0; i < 5; i++)
        {
            byte current = ReadUInt8();

            // A leading 0x80 would be a zero encoded the long way round.
            if (i == 0 && current == 0x80)
                throw new FontLoadException("Malformed length: UIntBase128 with a leading zero.")
                { Path = _path };

            if ((result & 0xFE000000u) != 0)
                throw new FontLoadException("Malformed length: UIntBase128 overflows 32 bits.")
                { Path = _path };

            result = (result << 7) | (uint)(current & 0x7F);

            if ((current & 0x80) == 0) return result;
        }

        throw new FontLoadException("Malformed length: UIntBase128 longer than five bytes.")
        { Path = _path };
    }

    /// <summary>
    /// Reads the short variable-length integer WOFF 2.0 uses for point counts and
    /// instruction lengths.
    /// </summary>
    /// <remarks>
    /// 0–252 fit in the lead byte. 253 means a two-byte value follows; 255 and 254 each
    /// mean one more byte, biased by 253 and 506, so the common small values stay a
    /// single byte.
    /// </remarks>
    public ushort Read255UInt16()
    {
        const byte WordCode = 253;
        const byte OneMoreByteCode1 = 255;
        const byte OneMoreByteCode2 = 254;
        const int LowestCode = 253;

        byte code = ReadUInt8();

        return code switch
        {
            WordCode => ReadUInt16(),
            OneMoreByteCode1 => (ushort)(ReadUInt8() + LowestCode),
            OneMoreByteCode2 => (ushort)(ReadUInt8() + LowestCode * 2),
            _ => code,
        };
    }

    /// <summary>Throws unless the next four bytes are the expected tag.</summary>
    public void ExpectTag(string tag)
    {
        uint actual = ReadUInt32();
        uint expected = SfntTag.FromString(tag);

        if (actual != expected)
            throw new FontLoadException(
                $"Expected the tag '{tag}' but found '{SfntTag.ToDisplayString(actual)}'.")
            { Path = _path };
    }
}

/// <summary>Four-character SFNT table tags, packed into a big-endian word.</summary>
internal static class SfntTag
{
    public static uint FromString(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length != 4) throw new ArgumentException("A tag is four characters.", nameof(tag));

        return ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];
    }

    /// <summary>Renders a tag for an error message, with unprintable bytes escaped.</summary>
    public static string ToDisplayString(uint tag)
    {
        Span<char> characters = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char character = (char)((tag >> (24 - i * 8)) & 0xFF);
            characters[i] = character is >= ' ' and <= '~' ? character : '?';
        }
        return new string(characters);
    }

    public static readonly uint Glyf = FromString("glyf");
    public static readonly uint Loca = FromString("loca");
    public static readonly uint Head = FromString("head");
    public static readonly uint Hmtx = FromString("hmtx");
    public static readonly uint Hhea = FromString("hhea");
    public static readonly uint Maxp = FromString("maxp");
}
