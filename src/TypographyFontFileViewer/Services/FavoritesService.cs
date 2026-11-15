using System.IO;
using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Services;

/// <summary>What happened when a star was toggled.</summary>
/// <param name="Succeeded">False leaves the star as it was.</param>
/// <param name="Error">Why not, in words meant for the user.</param>
/// <param name="Path">The file added or removed, when there was one.</param>
public readonly record struct FavoriteResult(bool Succeeded, string? Error, string? Path)
{
    public static FavoriteResult Ok(string? path = null) => new(true, null, path);

    public static FavoriteResult Failed(string error) => new(false, error, null);
}

/// <summary>
/// The <c>Favorite Fonts</c> folder beside the exe, and the index that makes it quick.
/// </summary>
/// <remarks>
/// <para>
/// Favouriting copies the font file into a folder next to the app, so a favourite is a
/// real font you can open, copy or install, not a bookmark that breaks when the original
/// moves. Un-favouriting sends that copy to the Recycle Bin, because it may be the only
/// copy left.
/// </para>
/// <para>
/// <b>The files in the folder are the truth.</b> The index in the settings file only
/// makes lookups fast. The user may drop fonts into the folder or delete them by hand,
/// and <see cref="ReconcileAsync"/> brings the index back in line on startup rather than
/// assuming it was right.
/// </para>
/// <para>
/// Identity is the SHA-256 of the file's contents, never its name. Two unrelated fonts
/// are often both called Roboto.ttf, and the same font renamed is still the same font.
/// </para>
/// </remarks>
public sealed class FavoritesService
{
    private readonly HashService _hashes;
    private readonly IFileRecycler _recycler;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Keyed by hash, which is what "is this font a favourite?" actually asks.
    private readonly Dictionary<string, FavoriteEntry> _byHash = new(StringComparer.OrdinalIgnoreCase);

    // Every size in the index, so a file whose size matches nothing is never hashed.
    private readonly HashSet<long> _sizes = [];

    public FavoritesService(
        string favoritesDirectory,
        HashService hashes,
        IFileRecycler recycler,
        SettingsService settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(favoritesDirectory);

        Directory = System.IO.Path.GetFullPath(favoritesDirectory);
        _hashes = hashes;
        _recycler = recycler;
        _settings = settings;
    }

    /// <summary>The <c>Favorite Fonts</c> folder. Created on the first favourite.</summary>
    public string Directory { get; }

    public int Count
    {
        get { lock (_byHash) return _byHash.Count; }
    }

    /// <summary>Raised after anything is added, removed or reconciled.</summary>
    public event EventHandler? Changed;

    /// <summary>The folder a given executable keeps its favourites in.</summary>
    public static string DefaultDirectoryFor(string executableDirectory)
        => System.IO.Path.Combine(executableDirectory, "Favorite Fonts");

    // --- Startup ---------------------------------------------------------------

    /// <summary>
    /// Brings the index into line with what is actually in the folder.
    /// </summary>
    /// <remarks>
    /// A font the user dropped in by hand gets hashed and indexed; an entry whose file
    /// has been deleted is dropped. After this the index and the folder agree, whatever
    /// happened between runs.
    /// </remarks>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var rebuilt = new Dictionary<string, FavoriteEntry>(StringComparer.OrdinalIgnoreCase);
            var byFileName = new Dictionary<string, FavoriteEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (FavoriteEntry entry in _settings.Current.Favorites)
                byFileName[entry.FileName] = entry;

            if (System.IO.Directory.Exists(Directory))
            {
                foreach (string path in SafeEnumerateFonts(Directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string fileName = System.IO.Path.GetFileName(path);
                    var info = new FileInfo(path);

                    // Trust the remembered hash only while the size still agrees.
                    if (byFileName.TryGetValue(fileName, out FavoriteEntry? known)
                        && known.Size == info.Length)
                    {
                        rebuilt[known.Sha256] = known;
                        continue;
                    }

                    string? hash = await _hashes.ComputeAsync(path, cancellationToken);
                    if (hash is null) continue;

                    rebuilt[hash] = new FavoriteEntry
                    {
                        FileName = fileName,
                        Sha256 = hash,
                        Size = info.Length,
                        Companions = FindCompanionsIn(Directory, fileName),
                        OriginalPath = known?.OriginalPath,
                        AddedUtc = known?.AddedUtc ?? info.CreationTimeUtc,
                    };
                }
            }

            ReplaceIndex(rebuilt);
            PersistIndex();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // --- Asking ----------------------------------------------------------------

    /// <summary>
    /// Is this font one of the favourites?
    /// </summary>
    /// <remarks>
    /// The size is checked first, which rules out nearly every file for nothing. Only a
    /// file whose size matches some favourite is ever read and hashed.
    /// </remarks>
    public async Task<bool> IsFavoriteAsync(
        string path, long length, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path)) return false;

        lock (_byHash)
        {
            if (_byHash.Count == 0) return false;
            if (!_sizes.Contains(length)) return false;
        }

        string? hash = await _hashes.ComputeAsync(path, cancellationToken);
        if (hash is null) return false;

        lock (_byHash) return _byHash.ContainsKey(hash);
    }

    /// <summary>True when the given folder is the favourites folder itself.</summary>
    public bool IsFavoritesFolder(string? folder)
        => !string.IsNullOrEmpty(folder)
        && string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)),
            System.IO.Path.TrimEndingDirectorySeparator(Directory),
            StringComparison.OrdinalIgnoreCase);

    // --- Adding ----------------------------------------------------------------

    /// <summary>
    /// Copies a font into the favourites folder.
    /// </summary>
    /// <remarks>
    /// Copying goes to a temporary name first and is then moved into place, so an
    /// interrupted copy never leaves a half-written font looking like a real one. A
    /// Type 1 font brings its metric files, renamed to match. Favouriting any face of a
    /// collection copies the whole file, so every face in it is starred.
    /// </remarks>
    public async Task<FavoriteResult> AddAsync(FontFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        string? hash = await _hashes.ComputeAsync(file.Path, cancellationToken);
        if (hash is null)
            return FavoriteResult.Failed($"{file.FileName} could not be read.");

        lock (_byHash)
        {
            if (_byHash.ContainsKey(hash)) return FavoriteResult.Ok();   // already a favourite
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? problem = EnsureDirectoryWritable();
            if (problem is not null) return FavoriteResult.Failed(problem);

            string targetName = ChooseTargetName(file.FileName, hash);
            string targetPath = System.IO.Path.Combine(Directory, targetName);

            string? copyError = CopyIntoPlace(file.Path, targetPath);
            if (copyError is not null) return FavoriteResult.Failed(copyError);

            List<string> companions = file.Format == FontFormat.Type1
                ? CopyCompanions(file, targetName)
                : [];

            var entry = new FavoriteEntry
            {
                FileName = targetName,
                Sha256 = hash,
                Size = file.Length,
                Companions = companions,
                OriginalPath = file.Path,
                AddedUtc = DateTime.UtcNow,
            };

            lock (_byHash)
            {
                _byHash[hash] = entry;
                _sizes.Add(entry.Size);
            }

            PersistIndex();
            return FavoriteResult.Ok(targetPath);
        }
        finally
        {
            _gate.Release();
        }
        // Changed is raised by the caller-facing wrapper below.
    }

    // --- Removing --------------------------------------------------------------

    /// <summary>
    /// Sends a favourite's copy to the Recycle Bin and forgets it.
    /// </summary>
    /// <remarks>
    /// Only ever touches files inside the favourites folder. The font the user
    /// favourited from is never the thing deleted, and the check below is deliberate
    /// rather than implied: this is the one place in the app that removes a file.
    /// </remarks>
    public async Task<FavoriteResult> RemoveAsync(
        string path, long length, CancellationToken cancellationToken = default)
    {
        string? hash = await _hashes.ComputeAsync(path, cancellationToken);
        if (hash is null)
            return FavoriteResult.Failed($"{System.IO.Path.GetFileName(path)} could not be read.");

        FavoriteEntry? entry;
        lock (_byHash)
        {
            if (!_byHash.TryGetValue(hash, out entry)) return FavoriteResult.Ok();
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var doomed = new List<string> { entry!.FileName };
            doomed.AddRange(entry.Companions);

            foreach (string name in doomed)
            {
                string target = System.IO.Path.Combine(Directory, name);

                // The invariant that makes this safe to call.
                if (!IsInsideFavorites(target))
                    return FavoriteResult.Failed(
                        $"Refusing to delete {target}: it is outside the favourites folder.");

                string? error = _recycler.Recycle(target);
                if (error is not null)
                    return FavoriteResult.Failed($"Could not remove {name}: {error}");

                _hashes.Invalidate(target);
            }

            lock (_byHash)
            {
                _byHash.Remove(hash);
                RebuildSizes();
            }

            PersistIndex();
            return FavoriteResult.Ok(System.IO.Path.Combine(Directory, entry.FileName));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds or removes, whichever the font is not already.</summary>
    public async Task<FavoriteResult> ToggleAsync(
        FontFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        bool starred = await IsFavoriteAsync(file.Path, file.Length, cancellationToken);

        FavoriteResult result = starred
            ? await RemoveAsync(file.Path, file.Length, cancellationToken)
            : await AddAsync(file, cancellationToken);

        if (result.Succeeded) Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    // --- The folder ------------------------------------------------------------

    /// <summary>
    /// Makes sure the folder exists and can be written to.
    /// </summary>
    /// <remarks>
    /// An app sitting in Program Files cannot write beside itself. The blueprint is
    /// explicit that this must be said plainly rather than quietly redirected somewhere
    /// else: a favourite the user cannot find is worse than one they were told about.
    /// </remarks>
    public string? EnsureDirectoryWritable()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            // Creating the folder can succeed where writing into it does not, so prove it.
            string probe = System.IO.Path.Combine(Directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Can't write to 'Favorite Fonts' next to the app: {Directory}. "
                 + $"Move the app to a folder you can write to. ({exception.Message})";
        }
    }

    public bool IsWritable => EnsureDirectoryWritable() is null;

    // --- Internals -------------------------------------------------------------

    /// <summary>
    /// True only for paths that really sit inside the favourites folder.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so it can be tested directly. Reconciling rebuilds
    /// the index from the folder listing, so an entry's file name is always a bare name
    /// and this guard should never fire in normal use — which is exactly why it is worth
    /// having and worth testing on its own.
    /// </remarks>
    internal bool IsInsideFavorites(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string root = System.IO.Path.TrimEndingDirectorySeparator(Directory)
                    + System.IO.Path.DirectorySeparatorChar;

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks a name inside the folder, stepping aside when one is taken by another font.
    /// </summary>
    private string ChooseTargetName(string preferred, string hash)
    {
        string baseName = System.IO.Path.GetFileNameWithoutExtension(preferred);
        string extension = System.IO.Path.GetExtension(preferred);

        string candidate = preferred;
        int suffix = 2;

        while (File.Exists(System.IO.Path.Combine(Directory, candidate)))
        {
            // A file of the same name that is the same font would already have matched
            // on its hash, so anything still here is a different font wanting the name.
            candidate = $"{baseName} ({suffix}){extension}";
            suffix++;

            if (suffix > 1000)
                return $"{baseName} ({hash[..8]}){extension}";
        }

        return candidate;
    }

    private static string? CopyIntoPlace(string source, string targetPath)
    {
        string temporary = targetPath + $".part-{Guid.NewGuid():N}";

        try
        {
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, targetPath, overwrite: false);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leave nothing half-written behind.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return $"Could not copy {System.IO.Path.GetFileName(source)}: {exception.Message}";
        }
    }

    /// <summary>Copies a Type 1 font's metrics, renamed to match their new font.</summary>
    private List<string> CopyCompanions(FontFile file, string targetName)
    {
        var copied = new List<string>();
        string targetBase = System.IO.Path.GetFileNameWithoutExtension(targetName);

        foreach (string companion in file.Companions)
        {
            string name = targetBase + System.IO.Path.GetExtension(companion);
            string target = System.IO.Path.Combine(Directory, name);

            if (CopyIntoPlace(companion, target) is null) copied.Add(name);
        }

        return copied;
    }

    private static IReadOnlyList<string> FindCompanionsIn(string folder, string fontFileName)
    {
        string baseName = System.IO.Path.GetFileNameWithoutExtension(fontFileName);
        var found = new List<string>();

        foreach (string extension in new[] { ".afm", ".pfm", ".inf" })
        {
            string candidate = baseName + extension;
            if (File.Exists(System.IO.Path.Combine(folder, candidate))) found.Add(candidate);
        }

        return found;
    }

    private static IEnumerable<string> SafeEnumerateFonts(string folder)
    {
        IEnumerable<string> entries;
        try
        {
            entries = System.IO.Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string path in entries)
            if (FormatSniffer.IsSupportedFileName(System.IO.Path.GetFileName(path)))
                yield return path;
    }

    private void ReplaceIndex(Dictionary<string, FavoriteEntry> rebuilt)
    {
        lock (_byHash)
        {
            _byHash.Clear();
            foreach ((string hash, FavoriteEntry entry) in rebuilt) _byHash[hash] = entry;
            RebuildSizes();
        }
    }

    private void RebuildSizes()
    {
        _sizes.Clear();
        foreach (FavoriteEntry entry in _byHash.Values) _sizes.Add(entry.Size);
    }

    private void PersistIndex()
    {
        lock (_byHash)
        {
            _settings.Current.Favorites = [.. _byHash.Values.OrderBy(
                entry => entry.FileName, StringComparer.OrdinalIgnoreCase)];
        }

        _settings.Save();
    }
}
