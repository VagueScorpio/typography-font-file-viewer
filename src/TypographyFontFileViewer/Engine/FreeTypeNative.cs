using System.Runtime.InteropServices;
using FreeTypeSharp;

namespace TypographyFontFileViewer.Engine;

/// <summary>
/// The FreeType entry points the app needs that FreeTypeSharp 3.1.0 does not bind.
/// </summary>
/// <remarks>
/// <para>
/// The generated binding covers rendering and the face record, but stops short of the
/// metadata accessors: the SFNT name table, Type 1 font info, the Windows FNT header,
/// BDF properties, the format string and the variation axes. All of them are exported
/// by the <c>freetype.dll</c> that FreeTypeSharp already ships, so they are declared
/// here against the same library name and resolve to the same loaded module.
/// </para>
/// <para>
/// Sizes matter here. This FreeType build uses a 64-bit <c>FT_Long</c> — verified
/// against a known font, where a mismatch would have produced nonsense for
/// units_per_EM and the name pointers — so <c>FT_Long</c>, <c>FT_ULong</c> and
/// <c>FT_Fixed</c> map to <see cref="nint"/> / <see cref="nuint"/>, exactly as
/// FreeTypeSharp's own structs do.
/// </para>
/// <para>
/// Where FreeType writes into a caller-allocated struct, this file deliberately passes
/// a generously oversized zeroed buffer and reads fields at fixed offsets instead of
/// declaring the struct. If a future FreeType grew one of those records, a declared
/// struct would be silently overflowed; an oversized buffer cannot be.
/// </para>
/// </remarks>
internal static unsafe class FreeTypeNative
{
    private const string Library = "freetype";

    // --- SFNT name table ---------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct FT_SfntName
    {
        public ushort platform_id;
        public ushort encoding_id;
        public ushort language_id;
        public ushort name_id;
        public byte* @string;      // not null-terminated; string_len bytes
        public uint string_len;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FT_Get_Sfnt_Name_Count(FT_FaceRec_* face);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern FT_Error FT_Get_Sfnt_Name(FT_FaceRec_* face, uint idx, FT_SfntName* aname);

    // --- PostScript Type 1 -------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct PS_FontInfoRec
    {
        public byte* version;
        public byte* notice;
        public byte* full_name;
        public byte* family_name;
        public byte* weight;
        public nint italic_angle;          // FT_Long
        public byte is_fixed_pitch;        // FT_Bool
        public short underline_position;
        public ushort underline_thickness;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern FT_Error FT_Get_PS_Font_Info(FT_FaceRec_* face, PS_FontInfoRec* afont_info);

    // --- Windows FNT / FON -------------------------------------------------------
    //
    // FT_WinFNT_HeaderRec layout, with an 8-byte FT_ULong:
    //   0  version            FT_UShort
    //   8  file_size          FT_ULong
    //  16  copyright[60]      FT_Byte
    //  76  file_type          FT_UShort
    //  78  nominal_point_size FT_UShort
    //  ... and roughly 100 further bytes this app does not read.

    internal const int WinFntBufferSize = 512;      // far larger than the record ever is
    internal const int WinFntCopyrightOffset = 16;
    internal const int WinFntCopyrightLength = 60;
    internal const int WinFntNominalPointSizeOffset = 78;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern FT_Error FT_Get_WinFNT_Header(FT_FaceRec_* face, byte* aheader);

    // --- BDF / PCF properties ----------------------------------------------------
    //
    // BDF_PropertyRec is a 4-byte type tag followed, after padding, by an 8-byte union
    // of { const char* atom; FT_Int32 integer; FT_UInt32 cardinal }.

    internal const int BdfPropertyBufferSize = 64;
    internal const int BdfPropertyValueOffset = 8;

    internal enum BdfPropertyType
    {
        None = 0,
        Atom = 1,
        Integer = 2,
        Cardinal = 3,
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern FT_Error FT_Get_BDF_Property(
        FT_FaceRec_* face,
        [MarshalAs(UnmanagedType.LPStr)] string prop_name,
        byte* aproperty);

    // --- Format string -----------------------------------------------------------

    /// <summary>Returns a static string such as "TrueType", "CFF", "Type 1", "BDF", "PCF".</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte* FT_Get_Font_Format(FT_FaceRec_* face);

    // --- Variable fonts ----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct FT_MM_Var
    {
        public uint num_axis;
        public uint num_designs;
        public uint num_namedstyles;
        // Sequential layout applies the same natural alignment C does, so the
        // pointers below land at offset 16 without an explicit padding field.
        public FT_Var_Axis* axis;
        public FT_Var_Named_Style* namedstyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FT_Var_Axis
    {
        public byte* name;
        public nint minimum;       // FT_Fixed, 16.16
        public nint def;
        public nint maximum;
        public nuint tag;          // FT_ULong, four packed characters
        public uint strid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FT_Var_Named_Style
    {
        public nint* coords;
        public uint strid;
        public uint psid;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern FT_Error FT_Get_MM_Var(FT_FaceRec_* face, FT_MM_Var** amaster);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FT_Done_MM_Var(FT_LibraryRec_* library, FT_MM_Var* amaster);

    // --- Helpers -----------------------------------------------------------------

    /// <summary>16.16 fixed point to double.</summary>
    internal static double FromFixed16(nint value) => (long)value / 65536.0;

    /// <summary>Unpacks a four-character tag stored as a big-endian word.</summary>
    internal static string TagToString(nuint tag)
    {
        ulong value = (ulong)tag;
        Span<char> characters =
        [
            (char)((value >> 24) & 0xFF),
            (char)((value >> 16) & 0xFF),
            (char)((value >> 8) & 0xFF),
            (char)(value & 0xFF),
        ];

        foreach (char character in characters)
            if (character is < ' ' or > '~') return string.Empty;

        return new string(characters).Trim();
    }

    /// <summary>Reads a NUL-terminated C string that FreeType owns.</summary>
    internal static string? ReadNativeString(byte* pointer)
        => pointer == null ? null : Marshal.PtrToStringAnsi((nint)pointer);
}
