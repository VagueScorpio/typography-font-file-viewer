using System.IO;

namespace TypographyFontFileViewer.Core;

/// <summary>
/// Identifies one face inside one file, and the state of that file when it was read.
/// </summary>
/// <remarks>
/// Length and last-write time are part of the identity on purpose: if the file on disk
/// changes, the key changes, so a stale <c>FaceCache</c> entry can never be handed back
/// for a font the user has replaced.
/// </remarks>
public readonly record struct FaceKey(string Path, int FaceIndex, long Length, DateTime LastWriteUtc)
{
    /// <summary>Builds a key by stat-ing the file. Throws nothing but IO exceptions.</summary>
    public static FaceKey For(string path, int faceIndex)
    {
        var info = new FileInfo(path);
        return new FaceKey(path, faceIndex, info.Length, info.LastWriteTimeUtc);
    }

    public override string ToString() => FaceIndex == 0 ? Path : $"{Path}#{FaceIndex}";
}
