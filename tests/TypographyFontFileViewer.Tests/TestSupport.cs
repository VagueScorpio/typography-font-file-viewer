using System.IO;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>
/// A suite that works in a temporary folder of its own, removed when it finishes.
/// </summary>
/// <remarks>
/// Eleven suites each wrote out the same field, constructor and <c>Dispose</c>. The
/// prefix goes into the folder name so a directory left behind by a crashed run says
/// which suite left it.
/// </remarks>
public abstract class TempFolderTest : IDisposable
{
    protected TempFolderTest(string prefix)
    {
        Root = Path.Combine(Path.GetTempPath(), $"tffv-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    /// <summary>This suite's own folder, created before each test and deleted after it.</summary>
    protected string Root { get; }

    /// <summary>The runner's token, so a wedged test can actually be cancelled.</summary>
    protected static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    /// <summary>Makes a subfolder of <see cref="Root"/> and returns its path.</summary>
    protected string Folder(string name)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Copies named corpus fonts into <paramref name="destination"/>, skipping the test
    /// when one of them has not been built.
    /// </summary>
    protected static void SeedCorpusFonts(string destination, params string[] names)
    {
        string corpus = TestFonts.RequireCorpus();

        foreach (string name in names)
        {
            string source = Path.Combine(corpus, name);
            if (!File.Exists(source)) Assert.Skip($"{name} is not in the corpus.");

            File.Copy(source, Path.Combine(destination, name), overwrite: true);
        }
    }

    public void Dispose()
    {
        // A test that left a handle open should not fail the run over it; the temporary
        // folder can wait for the operating system to clear it.
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (IOException) { }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Helpers for suites that do not need a folder of their own.</summary>
internal static class Tests
{
    /// <summary>The runner's token, so a wedged test can actually be cancelled.</summary>
    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// How many dark pixels a BGRA buffer holds, which is how these tests ask "was
    /// anything actually drawn?".
    /// </summary>
    public static int CountInkPixels(byte[] bgra)
    {
        int ink = 0;
        for (int i = 0; i < bgra.Length; i += 4)
            if (bgra[i] < 200) ink++;

        return ink;
    }
}

/// <summary>
/// A temporary file that removes itself at the end of the <c>using</c> block.
/// </summary>
/// <remarks>
/// The malformed-font tests write a damaged file, open it and delete it again, a dozen
/// times over. Written by hand that is four lines of try/finally around one line of
/// test; as a disposable it is one line.
/// </remarks>
internal sealed class TempFile : IDisposable
{
    private TempFile(string path) => Path = path;

    public string Path { get; }

    public static TempFile With(string extension, byte[] contents)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tffv-{Guid.NewGuid():N}{extension}");

        File.WriteAllBytes(path, contents);
        return new TempFile(path);
    }

    public void Dispose()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch (IOException) { }
    }
}
