using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;

namespace TypographyFontFileViewer.ViewModels;

/// <summary>
/// The window's state: which folder is open, what is in it, and what is selected.
/// </summary>
/// <remarks>
/// Opening a font opens its whole folder. The list appears from directory entries
/// alone, then a background pass reads each file's metadata to fill in family names and
/// to expand collections into one row per face.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly RenderService _renderService;
    private readonly FolderScanner _scanner = new();

    private CancellationTokenSource? _metadataScan;
    private bool _disposed;

    private readonly FavoritesService? _favorites;

    /// <param name="favorites">
    /// The favourites folder. Omitted in tests that are not about favourites, which is
    /// why the star commands check before acting rather than assuming.
    /// </param>
    public MainViewModel(RenderService renderService, FavoritesService? favorites = null)
    {
        _renderService = renderService;
        _favorites = favorites;
        Preview = new PreviewViewModel(renderService);
        Gallery = new GalleryViewModel(renderService, Preview);
        GlyphMap = new GlyphMapViewModel(renderService, Preview);
        Info = new InfoViewModel(renderService, new HashService());

        if (_favorites is not null)
            _favorites.Changed += (_, _) => _ = RefreshFavoriteFlagsAsync();

    }

    public PreviewViewModel Preview { get; }

    public GalleryViewModel Gallery { get; }

    public GlyphMapViewModel GlyphMap { get; }

    public InfoViewModel Info { get; }

    public ObservableRangeCollection<FontItemViewModel> Fonts { get; } = [];

    /// <summary>
    /// The rows actually on screen: <see cref="Fonts"/> with the search applied.
    /// </summary>
    /// <remarks>
    /// Both lists bind here, so filtering once filters the sidebar and the gallery
    /// together — which is what the blueprint means by the gallery showing "the current
    /// (filtered) list".
    ///
    /// Filtered here rather than through a WPF CollectionView on purpose. A collection
    /// view belongs to the thread that made it, which would tie this view model to a
    /// dispatcher and make every background continuation that touches the list a
    /// thread-affinity bug waiting to happen.
    /// </remarks>
    public ObservableRangeCollection<FontItemViewModel> VisibleFonts { get; } = [];

    /// <summary>What the user typed in the search box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    public partial string SearchText { get; set; } = string.Empty;

    public bool IsFiltered => !string.IsNullOrWhiteSpace(SearchText);

    [ObservableProperty]
    public partial bool IncludeSubfolders { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortedByFileName))]
    [NotifyPropertyChangedFor(nameof(IsSortedByFamily))]
    [NotifyPropertyChangedFor(nameof(IsSortedByFormat))]
    public partial SortOrder SortOrder { get; set; } = SortOrder.FileName;

    /// <summary>The last folders opened, most recent first.</summary>
    public ObservableRangeCollection<string> RecentFolders { get; } = [];

    partial void OnSearchTextChanged(string value) => RefreshFilter();

    partial void OnSortOrderChanged(SortOrder value) => ApplySort();

    /// <summary>
    /// Completes when the rescan triggered by the subfolder toggle has finished.
    /// </summary>
    /// <remarks>
    /// Nothing in the UI waits on this; toggling the box rescans in the background as it
    /// should. It exists so a test can tell "the wider scan has not finished" apart from
    /// "the wider scan found nothing".
    /// </remarks>
    public Task PendingRescan { get; private set; } = Task.CompletedTask;

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        if (!string.IsNullOrEmpty(CurrentFolder)) PendingRescan = RescanAsync();
    }

    /// <summary>Re-applies the search filter and rebuilds the visible list.</summary>
    public void RefreshFilter()
    {
        // Captured first: replacing the contents resets the bound lists, and a list
        // clearing its own selection writes null straight back here before this method
        // gets to look at it.
        FontItemViewModel? wanted = SelectedFont;

        VisibleFonts.ReplaceAll(Fonts.Where(Matches));
        NotifyListChanged();

        // Keep the selection where it still shows; otherwise move to the first match, so
        // a search always leaves something previewed rather than an empty panel.
        SelectedFont = wanted is not null && VisibleFonts.Contains(wanted)
            ? wanted
            : VisibleFonts.Count > 0 ? VisibleFonts[0] : null;
    }

    /// <summary>
    /// The three read-only properties that describe the list rather than one font, and
    /// so have to be re-read together whenever the list or the selection moves.
    /// </summary>
    private void NotifyListChanged()
    {
        OnPropertyChanged(nameof(FolderSummary));
        OnPropertyChanged(nameof(SelectedPosition));
        OnPropertyChanged(nameof(StatusText));
    }

    private bool Matches(FontItemViewModel font)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        string needle = SearchText.Trim();

        // File name, family and style, which between them are what anyone would type.
        return font.File.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || font.Family.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || font.Style.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-orders the list.
    /// </summary>
    /// <remarks>
    /// Sorting the underlying collection rather than the view, because family names
    /// arrive from the background pass long after the rows do and a view's sort
    /// descriptions would not notice them changing.
    /// </remarks>
    public void ApplySort()
    {
        if (Fonts.Count == 0) return;

        FontItemViewModel? selected = SelectedFont;

        IEnumerable<FontItemViewModel> ordered = SortOrder switch
        {
            SortOrder.FamilyName => Fonts
                .OrderBy(font => font.Family, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(font => font.Style, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(font => font.File.FileName, NaturalFileNameComparer.Instance),

            SortOrder.Format => Fonts
                .OrderBy(font => font.FormatBadge, StringComparer.OrdinalIgnoreCase)
                .ThenBy(font => font.File.FileName, NaturalFileNameComparer.Instance),

            _ => Fonts
                .OrderBy(font => font.File.FileName, NaturalFileNameComparer.Instance)
                .ThenBy(font => font.FaceIndex),
        };

        Fonts.ReplaceAll([.. ordered]);
        RefreshFilter();

        // ReplaceAll resets the collection, which clears the lists' selection.
        if (selected is not null && Fonts.Contains(selected)) SelectedFont = selected;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(SelectedPosition))]
    public partial FontItemViewModel? SelectedFont { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderSummary))]
    public partial string CurrentFolder { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderSummary))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderSummary))]
    public partial int BrokenCount { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>Which of the two views is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGallery))]
    [NotifyPropertyChangedFor(nameof(IsSingleFont))]
    public partial ViewMode CurrentView { get; set; } = ViewMode.SingleFont;

    public bool IsGallery => CurrentView == ViewMode.Gallery;

    public bool IsSingleFont => CurrentView == ViewMode.SingleFont;

    /// <summary>Which panel of the single-font view is showing.</summary>
    [ObservableProperty]
    public partial SingleFontPanel CurrentPanel { get; set; } = SingleFontPanel.Preview;

    /// <summary>How many panels the strip actually offers.</summary>
    public const int AvailablePanelCount = 3;

    [RelayCommand]
    private void ShowView(string? mode)
    {
        if (Enum.TryParse(mode, out ViewMode parsed)) CurrentView = parsed;
    }

    /// <summary>
    /// Switches to the single-font view and selects one of its panels.
    /// </summary>
    /// <remarks>
    /// This is what Ctrl+1, Ctrl+3 and Ctrl+4 do, so the numbers still run 1 to 4 across
    /// the whole window as they did when these were four flat tabs. A panel past the end
    /// of the strip falls back to Preview rather than leaving nothing selected.
    /// </remarks>
    [RelayCommand]
    private void ShowPanel(string? panel)
    {
        if (!Enum.TryParse(panel, out SingleFontPanel parsed)) return;

        CurrentView = ViewMode.SingleFont;
        CurrentPanel = (int)parsed < AvailablePanelCount ? parsed : SingleFontPanel.Preview;
    }

    /// <summary>Flips between the two views, for the keyboard.</summary>
    [RelayCommand]
    private void ToggleView()
        => CurrentView = CurrentView == ViewMode.Gallery ? ViewMode.SingleFont : ViewMode.Gallery;

    /// <summary>
    /// Opens one font large, which is what double-clicking a gallery row means.
    /// </summary>
    public void OpenInSingleView(FontItemViewModel? font)
    {
        if (font is not null) SelectedFont = font;
        CurrentView = ViewMode.SingleFont;
    }

    /// <summary>
    /// Resizes whichever view is showing, so Ctrl+= means the same thing in both.
    /// </summary>
    public void AdjustSize(double delta)
    {
        if (CurrentView == ViewMode.Gallery) Gallery.AdjustSize(delta);
        else Preview.AdjustSize(delta);
    }

    /// <summary>The line under the folder path: how many fonts, and how many are broken.</summary>
    public string FolderSummary
    {
        get
        {
            if (IsScanning) return "Scanning…";
            if (Fonts.Count == 0) return "No fonts here";

            // When a search is on, say how much of the folder is being hidden.
            string summary = IsFiltered
                ? $"{VisibleFonts.Count:N0} of {Fonts.Count:N0} fonts"
                : Fonts.Count == 1 ? "1 font" : $"{Fonts.Count:N0} fonts";

            int starred = Fonts.Count(font => font.IsFavorite);
            if (starred > 0) summary += $" · {starred} ★";

            return BrokenCount > 0 ? $"{summary} · {BrokenCount} unreadable" : summary;
        }
    }

    /// <summary>"Font 4 of 128", for the status bar.</summary>
    public string SelectedPosition
    {
        get
        {
            if (SelectedFont is null || VisibleFonts.Count == 0) return string.Empty;

            int index = VisibleFonts.IndexOf(SelectedFont);
            return index < 0 ? string.Empty : $"Font {index + 1:N0} of {VisibleFonts.Count:N0}";
        }
    }

    /// <summary>What the status bar shows about the selected font.</summary>
    public string StatusText
    {
        get
        {
            if (SelectedFont is null) return "Open a font or a folder to begin.";

            var parts = new List<string> { SelectedFont.File.FileName, SelectedFont.FormatBadge };

            if (SelectedFont.IsBroken)
                parts.Add(SelectedFont.Error!);
            else if (SelectedFont.GlyphCount > 0)
                parts.Add($"{SelectedFont.GlyphCount:N0} glyphs");

            string position = SelectedPosition;
            if (position.Length > 0) parts.Add(position);

            return string.Join(" · ", parts);
        }
    }

    partial void OnSelectedFontChanged(FontItemViewModel? value) => SyncPreviewToSelection();

    /// <summary>
    /// Points the preview at whatever is selected now.
    /// </summary>
    /// <remarks>
    /// Called both when the selection changes and when metadata arrives for the row that
    /// is already selected — a font is not known to be broken until it has been opened,
    /// and by then the preview may already be pointed at it.
    /// </remarks>
    private void SyncPreviewToSelection()
    {
        FontItemViewModel? selected = SelectedFont;

        FaceKey? face = selected is null || selected.IsBroken ? null : selected.Key;

        Preview.Face = face;
        Preview.ErrorMessage = selected?.Error;

        // All three panels describe the same font, which is the whole reason they live
        // inside one view.
        GlyphMap.Face = face;
        Info.Face = face;

        if (selected is not null && !selected.IsBroken) _ = LoadFixedSizesAsync(selected);
        else Preview.FixedSizes = [];
    }

    /// <summary>
    /// Opens a file or a folder. A file selects itself and loads its neighbours.
    /// </summary>
    public async Task OpenPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (Directory.Exists(path))
            {
                await LoadFolderAsync(path, selectFile: null, cancellationToken);
                return;
            }

            if (System.IO.File.Exists(path))
            {
                string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(folder))
                {
                    Message = $"Cannot work out which folder {path} is in.";
                    return;
                }

                await LoadFolderAsync(folder, Path.GetFullPath(path), cancellationToken);
                return;
            }

            Message = $"There is nothing at {path}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Message = exception.Message;
        }
    }

    /// <summary>Re-reads the current folder from disk, keeping the selection if it survives.</summary>
    public async Task RescanAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(CurrentFolder)) return;

        string? selected = SelectedFont?.File.Path;

        // Anything cached for this folder may be stale, so drop it before reloading —
        // both the open faces and the gallery's rendered rows.
        foreach (string path in Fonts.Select(font => font.File.Path).Distinct().ToList())
        {
            await _renderService.InvalidateAsync(path, cancellationToken);
            Gallery.Cache.InvalidatePath(path);
        }

        await LoadFolderAsync(CurrentFolder, selected, cancellationToken);
    }

    private async Task LoadFolderAsync(
        string folder, string? selectFile, CancellationToken cancellationToken)
    {
        CancelMetadataScan();

        IsScanning = true;
        Message = null;
        var timer = Stopwatch.StartNew();

        try
        {
            IReadOnlyList<FontFile> files = await _scanner.ScanAsync(
                folder, IncludeSubfolders, cancellationToken);

            BrokenCount = 0;

            // One notification, not one per font: a thousand incremental adds would be
            // a thousand layout passes in every list bound to this collection.
            Fonts.ReplaceAll(files.Select(file => new FontItemViewModel(file)));
            RefreshFilter();

            CurrentFolder = folder;
            RememberFolder(folder);

            SelectedFont = selectFile is not null
                ? Fonts.FirstOrDefault(font =>
                      string.Equals(font.File.Path, selectFile, StringComparison.OrdinalIgnoreCase))
                  ?? Fonts.FirstOrDefault()
                : Fonts.FirstOrDefault();

            if (Fonts.Count == 0)
                Message = $"No font files in {folder}.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsScanning = false;
            NotifyListChanged();
        }

        Debug.WriteLine($"Listed {Fonts.Count} fonts in {timer.ElapsedMilliseconds} ms");

        OnPropertyChanged(nameof(ViewingFavorites));
        StartMetadataScan();

        // Stars are worked out alongside the metadata rather than before it, so the list
        // appears at once and fills in from both directions.
        await RefreshFavoriteFlagsAsync(cancellationToken);
    }

    /// <summary>
    /// Completes when the background metadata pass for the current folder has finished.
    /// </summary>
    /// <remarks>
    /// Nothing in the UI waits on this — the whole point of the pass is that it happens
    /// behind a list the user can already use. It exists so a test can tell the
    /// difference between "the names have not arrived yet" and "the names are wrong".
    /// </remarks>
    public Task MetadataScanCompleted { get; private set; } = Task.CompletedTask;

    private void StartMetadataScan()
    {
        var scan = new CancellationTokenSource();
        _metadataScan = scan;
        MetadataScanCompleted = RunMetadataScanAsync(scan.Token);
    }

    /// <summary>
    /// Reads every file's metadata in the background, at low priority.
    /// </summary>
    /// <remarks>
    /// The selected font is read first so its name is right immediately; the rest fill in
    /// behind it. Collections expand here, because how many faces a file holds is not
    /// knowable until it has been opened.
    /// </remarks>
    private async Task RunMetadataScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Whatever is selected gets its real name first.
            if (SelectedFont is { MetadataLoaded: false } selected)
                await LoadMetadataAsync(selected, JobPriority.High, cancellationToken);

            for (int i = 0; i < Fonts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FontItemViewModel item = Fonts[i];
                if (item.MetadataLoaded) continue;

                int added = await LoadMetadataAsync(item, JobPriority.Low, cancellationToken);
                i += added;
            }
        }
        catch (OperationCanceledException)
        {
            // A new folder was opened; this scan is no longer wanted.
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                BrokenCount = Fonts.Count(font => font.IsBroken);

                // Family names only exist now, so a family sort could not run until now.
                if (SortOrder != SortOrder.FileName) ApplySort();

                RefreshFilter();
            }
        }
    }

    /// <summary>Reads one file's faces. Returns how many extra rows were inserted.</summary>
    private async Task<int> LoadMetadataAsync(
        FontItemViewModel item, JobPriority priority, CancellationToken cancellationToken)
    {
        // The sweep over the whole folder reads and releases; the selected font, read at
        // high priority, is about to be rendered and is worth keeping open.
        FaceSummary[] summaries = await _renderService.ReadSummariesAsync(
            item.File.Path, priority, cancellationToken,
            keepOpen: priority == JobPriority.High);

        if (summaries.Length == 0 || cancellationToken.IsCancellationRequested) return 0;

        item.Apply(summaries[0]);

        // If this is the row on screen, the preview may now know something it did not:
        // that the font is broken, or what fixed sizes it has.
        if (ReferenceEquals(item, SelectedFont)) SyncPreviewToSelection();

        if (summaries.Length == 1) return 0;

        // A collection: the one row becomes one row per face.
        int index = Fonts.IndexOf(item);
        if (index < 0) return 0;

        for (int face = 1; face < summaries.Length; face++)
        {
            var row = new FontItemViewModel(item.File, face, summaries.Length);
            row.Apply(summaries[face]);
            Fonts.Insert(index + face, row);
        }

        // The new faces have to reach the screen, which means through the filter.
        RefreshFilter();
        return summaries.Length - 1;
    }

    private async Task LoadFixedSizesAsync(FontItemViewModel item)
    {
        try
        {
            IReadOnlyList<int> sizes = await _renderService.ReadFixedSizesAsync(item.Key);
            if (SelectedFont == item) Preview.FixedSizes = sizes;
        }
        catch (Exception exception) when (exception is FontLoadException or OperationCanceledException)
        {
            if (SelectedFont == item) Preview.FixedSizes = [];
        }
    }

    // --- Opening ---------------------------------------------------------------

    /// <summary>
    /// Shows the Open dialogs. Set by the window, because picking a file is the one
    /// thing here that genuinely belongs to the user interface.
    /// </summary>
    public Func<string?>? PickFontFile { get; set; }

    public Func<string?>? PickFolder { get; set; }

    [RelayCommand]
    private async Task OpenFile()
    {
        string? path = PickFontFile?.Invoke();
        if (!string.IsNullOrEmpty(path)) await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task OpenFolder()
    {
        string? path = PickFolder?.Invoke();
        if (!string.IsNullOrEmpty(path)) await OpenPathAsync(path);
    }

    [RelayCommand]
    private Task Rescan() => RescanAsync();

    // --- Recent folders, search, settings ---------------------------------------

    /// <summary>Moves a folder to the top of the recent list, keeping the last ten.</summary>
    private void RememberFolder(string folder)
    {
        var updated = new List<string>(AppSettings.MaximumRecentFolders) { folder };

        foreach (string existing in RecentFolders)
        {
            if (updated.Count >= AppSettings.MaximumRecentFolders) break;
            if (!string.Equals(existing, folder, StringComparison.OrdinalIgnoreCase))
                updated.Add(existing);
        }

        RecentFolders.ReplaceAll(updated);
    }

    [RelayCommand]
    private async Task OpenRecent(string? folder)
    {
        if (!string.IsNullOrEmpty(folder)) await OpenPathAsync(folder);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ToggleSubfolders() => IncludeSubfolders = !IncludeSubfolders;

    [RelayCommand]
    private void SetSort(string? order)
    {
        if (Enum.TryParse(order, out SortOrder parsed)) SortOrder = parsed;
    }

    public bool IsSortedByFileName => SortOrder == SortOrder.FileName;
    public bool IsSortedByFamily => SortOrder == SortOrder.FamilyName;
    public bool IsSortedByFormat => SortOrder == SortOrder.Format;

    /// <summary>Puts the window back the way the user left it.</summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        IncludeSubfolders = settings.IncludeSubfolders;
        SortOrder = settings.SortOrder;

        if (!string.IsNullOrWhiteSpace(settings.PreviewText))
            Preview.PreviewText = settings.PreviewText;

        Preview.PointSize = Math.Clamp(
            settings.PreviewPointSize, PreviewViewModel.MinimumPointSize, PreviewViewModel.MaximumPointSize);
        Preview.DarkBackground = settings.DarkPreview;

        Gallery.GallerySize = Math.Clamp(
            settings.GallerySize, GalleryViewModel.MinimumSize, GalleryViewModel.MaximumSize);

        GlyphMap.CellSize = Math.Clamp(
            settings.GlyphCellSize, GlyphMapViewModel.MinimumCellSize, GlyphMapViewModel.MaximumCellSize);

        CurrentView = settings.SelectedView;
        CurrentPanel = (int)settings.SelectedPanel < AvailablePanelCount
            ? settings.SelectedPanel
            : SingleFontPanel.Preview;

        RecentFolders.ReplaceAll(settings.RecentFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Take(AppSettings.MaximumRecentFolders));
    }

    /// <summary>Records the current state for next time.</summary>
    public void CaptureSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.IncludeSubfolders = IncludeSubfolders;
        settings.SortOrder = SortOrder;
        settings.PreviewText = Preview.PreviewText;
        settings.PreviewPointSize = Preview.PointSize;
        settings.DarkPreview = Preview.DarkBackground;
        settings.GallerySize = Gallery.GallerySize;
        settings.GlyphCellSize = GlyphMap.CellSize;
        settings.SelectedView = CurrentView;
        settings.SelectedPanel = CurrentPanel;
        settings.LastFolder = string.IsNullOrEmpty(CurrentFolder) ? null : CurrentFolder;
        settings.LastSelectedFile = SelectedFont?.File.Path;
        settings.RecentFolders = [.. RecentFolders];
    }

    /// <summary>
    /// Reopens whatever was on screen last time.
    /// </summary>
    /// <remarks>
    /// A folder that has since been deleted or unplugged is not an error worth
    /// complaining about on startup; the app simply opens empty.
    /// </remarks>
    public async Task RestoreLastSessionAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.LastFolder)) return;
        if (!Directory.Exists(settings.LastFolder)) return;

        await LoadFolderAsync(settings.LastFolder, settings.LastSelectedFile, cancellationToken);
    }

    // --- Favourites ------------------------------------------------------------

    /// <summary>True when there is a favourites folder to work with.</summary>
    public bool FavoritesAvailable => _favorites is not null;

    /// <summary>How many fonts are in the favourites folder.</summary>
    public int FavoriteCount => _favorites?.Count ?? 0;

    /// <summary>True while the folder on screen is the favourites folder itself.</summary>
    public bool ViewingFavorites
        => _favorites is not null && _favorites.IsFavoritesFolder(CurrentFolder);

    /// <summary>
    /// Stars or un-stars the selected font.
    /// </summary>
    /// <remarks>
    /// Un-starring while the favourites folder is the one on screen removes the row as
    /// well, since the file it stood for has just gone to the Recycle Bin, and moves the
    /// selection to the next font so the user is not left on nothing.
    /// </remarks>
    [RelayCommand]
    private async Task ToggleFavorite()
    {
        if (_favorites is null || SelectedFont is not { } font) return;

        bool wasFavorite = font.IsFavorite;
        bool viewingFavorites = ViewingFavorites;
        int index = Fonts.IndexOf(font);

        FavoriteResult result = await _favorites.ToggleAsync(font.File);

        if (!result.Succeeded)
        {
            Message = result.Error;
            return;
        }

        Message = null;

        if (wasFavorite && viewingFavorites && index >= 0)
        {
            // The file is gone; so should the row be.
            Fonts.RemoveAt(index);
            RefreshFilter();

            SelectedFont = VisibleFonts.Count == 0
                ? null
                : VisibleFonts[Math.Min(index, VisibleFonts.Count - 1)];

            NotifyListChanged();
        }

        await RefreshFavoriteFlagsAsync();
    }

    /// <summary>Opens the favourites folder as the current folder.</summary>
    [RelayCommand]
    private async Task OpenFavorites()
    {
        if (_favorites is null) return;

        string? problem = _favorites.EnsureDirectoryWritable();
        if (problem is not null)
        {
            Message = problem;
            return;
        }

        await OpenPathAsync(_favorites.Directory);
    }

    /// <summary>Shows the favourites folder in Explorer.</summary>
    [RelayCommand]
    private void OpenFavoritesInExplorer()
    {
        if (_favorites is null) return;

        string? problem = _favorites.EnsureDirectoryWritable();
        if (problem is not null)
        {
            Message = problem;
            return;
        }

        RevealInExplorer(_favorites.Directory);
    }

    /// <summary>Shows a font in Explorer with the file itself selected.</summary>
    [RelayCommand]
    private void OpenContainingFolder(FontItemViewModel? font)
    {
        FontItemViewModel? target = font ?? SelectedFont;
        if (target is null) return;

        RevealInExplorer(target.File.Path, select: true);
    }

    [RelayCommand]
    private void CopyFilePath(FontItemViewModel? font)
        => CopyToClipboard((font ?? SelectedFont)?.File.Path);

    [RelayCommand]
    private void CopyFamilyName(FontItemViewModel? font)
    {
        FontItemViewModel? target = font ?? SelectedFont;
        CopyToClipboard(string.IsNullOrWhiteSpace(target?.Family)
            ? target?.File.FileName
            : target.Family);
    }

    private void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException)
        {
            // Another process can hold the clipboard open; saying so beats a crash.
            Message = $"The clipboard was busy: {exception.Message}";
        }
    }

    private void RevealInExplorer(string path, bool select = false)
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe")
            {
                // Explorer wants its path quoted, and /select, has no space after it.
                Arguments = select ? $"/select,\"{path}\"" : $"\"{path}\"",
                UseShellExecute = true,
            };

            Process.Start(start);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            Message = $"Could not open Explorer: {exception.Message}";
        }
    }

    /// <summary>
    /// Works out which rows deserve a star.
    /// </summary>
    /// <remarks>
    /// Inside the favourites folder every row is one by definition, which saves hashing
    /// the whole folder to learn what it already knows. Anywhere else, each file's size
    /// is checked against the index first and only a size that matches is ever hashed.
    /// </remarks>
    public async Task RefreshFavoriteFlagsAsync(CancellationToken cancellationToken = default)
    {
        if (_favorites is null) return;

        try
        {
            bool everythingIsFavorite = _favorites.IsFavoritesFolder(CurrentFolder);

            // One answer per file, since a collection's faces share one file.
            var byPath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (FontItemViewModel font in Fonts.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!byPath.TryGetValue(font.File.Path, out bool starred))
                {
                    starred = everythingIsFavorite
                           || await _favorites.IsFavoriteAsync(
                                  font.File.Path, font.File.Length, cancellationToken);

                    byPath[font.File.Path] = starred;
                }

                font.IsFavorite = starred;
            }

            OnPropertyChanged(nameof(FavoriteCount));
            OnPropertyChanged(nameof(FolderSummary));
        }
        catch (OperationCanceledException)
        {
            // A newer folder is being loaded; its own pass will set the stars.
        }
    }

    // --- Navigation ------------------------------------------------------------

    [RelayCommand]
    private void NextFont() => MoveSelection(1);

    [RelayCommand]
    private void PreviousFont() => MoveSelection(-1);

    [RelayCommand]
    private void FirstFont()
    {
        if (VisibleFonts.Count > 0) SelectedFont = VisibleFonts[0];
    }

    [RelayCommand]
    private void LastFont()
    {
        if (VisibleFonts.Count > 0) SelectedFont = VisibleFonts[^1];
    }

    /// <summary>
    /// Steps through the list, stopping at the ends rather than wrapping.
    /// </summary>
    /// <remarks>
    /// Wrapping would mean a held-down Ctrl+Down cycles for ever with no sign it has
    /// finished; stopping makes the end of the folder obvious.
    /// </remarks>
    private void MoveSelection(int delta)
    {
        // Walks what is on screen, not the whole folder: with a search active, Ctrl+Down
        // should step through the matches.
        if (VisibleFonts.Count == 0) return;

        int index = SelectedFont is null ? -1 : VisibleFonts.IndexOf(SelectedFont);
        int next = Math.Clamp(index + delta, 0, VisibleFonts.Count - 1);

        if (index < 0) next = delta > 0 ? 0 : VisibleFonts.Count - 1;
        if (next != index) SelectedFont = VisibleFonts[next];
    }

    private void CancelMetadataScan()
    {
        _metadataScan?.Cancel();
        _metadataScan?.Dispose();
        _metadataScan = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelMetadataScan();
        GlyphMap.Dispose();
        Gallery.Dispose();
        Preview.Dispose();
    }
}
