using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace TypographyFontFileViewer.Services;

/// <summary>
/// Removes a file on the user's behalf.
/// </summary>
/// <remarks>
/// Behind an interface for one reason: a test suite that genuinely emptied files into
/// the user's Recycle Bin on every run would be a nuisance, and one that skipped the
/// real path entirely would not be testing what ships. Most tests use a fake; one
/// exercises the real thing, once.
/// </remarks>
public interface IFileRecycler
{
    /// <summary>Removes <paramref name="path"/>, recoverably where the platform allows.</summary>
    /// <returns>Null on success, or a message explaining why not.</returns>
    string? Recycle(string path);
}

/// <summary>
/// Sends files to the Recycle Bin rather than deleting them outright.
/// </summary>
/// <remarks>
/// Un-favouriting removes the app's copy of a font, and that copy may be the only one
/// the user still has — they may have favourited it from a memory card, a download that
/// has since been cleared, or a folder they have since tidied. The Recycle Bin is the
/// safety net that makes the star safe to toggle.
/// </remarks>
public sealed class RecycleBinFileRecycler : IFileRecycler
{
    public string? Recycle(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;    // already gone is the outcome we wanted

            FileSystem.DeleteFile(
                path,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin);

            return null;
        }
        catch (Exception exception) when (
            exception is IOException
                      or UnauthorizedAccessException
                      or OperationCanceledException
                      or NotSupportedException)
        {
            return exception.Message;
        }
    }
}
