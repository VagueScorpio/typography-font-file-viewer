using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypographyFontFileViewer.Core;

namespace TypographyFontFileViewer.Services;

/// <summary>
/// Everything the app remembers between runs.
/// </summary>
/// <remarks>
/// Phase 5 fills in the favourites index; the window bounds, last folder, preview text
/// and the rest arrive with Phase 7. Unknown fields are kept on load, so a file written
/// by a later version is not quietly stripped by an earlier one.
/// </remarks>
public sealed class AppSettings
{
    /// <summary>How many folders the File menu remembers.</summary>
    public const int MaximumRecentFolders = 10;

    // --- Window ---------------------------------------------------------------

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // --- Where the user was ---------------------------------------------------

    public string? LastFolder { get; set; }
    public string? LastSelectedFile { get; set; }
    public List<string> RecentFolders { get; set; } = [];

    // --- How they had it set up -----------------------------------------------

    public string? PreviewText { get; set; }
    public double PreviewPointSize { get; set; } = 48;
    public double GallerySize { get; set; } = 28;
    public int GlyphCellSize { get; set; } = 64;
    public bool IncludeSubfolders { get; set; }
    public SortOrder SortOrder { get; set; } = SortOrder.FileName;
    public bool DarkPreview { get; set; }
    public ViewModels.ViewMode SelectedView { get; set; } = ViewModels.ViewMode.SingleFont;
    public ViewModels.SingleFontPanel SelectedPanel { get; set; } = ViewModels.SingleFontPanel.Preview;

    /// <summary>The favourites index. The folder is the truth; this is the fast path.</summary>
    public List<FavoriteEntry> Favorites { get; set; } = [];

    /// <summary>Anything this version does not know about, preserved on round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// Reads and writes <c>TypographyFontFileViewer.json</c> beside the exe.
/// </summary>
/// <remarks>
/// Writes go to a temporary file and are then moved into place, so a crash or a full
/// disk midway through leaves the previous settings intact rather than a half-written
/// file. A file that will not parse is renamed rather than deleted, because it is the
/// user's data and it may be worth looking at.
/// </remarks>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Enums as names, so the file stays readable and a reordered enum does not
        // silently change what someone's saved settings mean.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    public SettingsService(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = filePath;
    }

    /// <summary>The settings file this instance reads and writes.</summary>
    public string FilePath { get; }

    public AppSettings Current { get; private set; } = new();

    /// <summary>Set when the last load or save could not be completed, for the status bar.</summary>
    public string? LastProblem { get; private set; }

    /// <summary>
    /// Loads the settings, falling back to defaults if the file is missing or unreadable.
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            LastProblem = null;

            try
            {
                if (!File.Exists(FilePath))
                {
                    Current = new AppSettings();
                    return;
                }

                string json = File.ReadAllText(FilePath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                       ?? new AppSettings();
            }
            catch (JsonException)
            {
                Current = new AppSettings();
                LastProblem = QuarantineCorruptFile();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Current = new AppSettings();
                LastProblem = $"Settings could not be read: {exception.Message}";
            }
        }
    }

    /// <summary>Writes the settings out, atomically.</summary>
    /// <returns>True when the file is on disk; false leaves <see cref="LastProblem"/> set.</returns>
    public bool Save()
    {
        lock (_gate)
        {
            string temporary = FilePath + ".tmp";

            try
            {
                string? folder = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                File.WriteAllText(temporary, JsonSerializer.Serialize(Current, SerializerOptions));

                // Move over the old file in one step; a reader never sees a partial write.
                File.Move(temporary, FilePath, overwrite: true);

                LastProblem = null;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LastProblem = $"Settings could not be saved to {FilePath}: {exception.Message}";

                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { /* nothing more to do about it */ }
                catch (UnauthorizedAccessException) { }

                return false;
            }
        }
    }

    /// <summary>Moves an unparseable settings file aside and says so.</summary>
    private string QuarantineCorruptFile()
    {
        string bad = FilePath + ".bad";

        try
        {
            File.Move(FilePath, bad, overwrite: true);
            return $"Settings file was unreadable and has been renamed to {Path.GetFileName(bad)}. "
                 + "Starting with defaults.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Settings file is unreadable and could not be renamed: {exception.Message}. "
                 + "Starting with defaults.";
        }
    }

    /// <summary>The settings file that belongs beside a given executable.</summary>
    public static string DefaultPathFor(string executableDirectory)
        => Path.Combine(executableDirectory, "TypographyFontFileViewer.json");
}
