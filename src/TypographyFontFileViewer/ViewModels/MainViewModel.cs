using System.Collections.ObjectModel;
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

    public MainViewModel(RenderService renderService)
    {
        _renderService = renderService;
        Preview = new PreviewViewModel(renderService);
        Gallery = new GalleryViewModel(renderService, Preview);
    }

    public PreviewViewModel Preview { get; }

    public GalleryViewModel Gallery { get; }

    public ObservableRangeCollection<FontItemViewModel> Fonts { get; } = [];

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

    [RelayCommand]
    private void ShowView(string? mode)
    {
        if (Enum.TryParse(mode, out ViewMode parsed)) CurrentView = parsed;
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

            string summary = Fonts.Count == 1 ? "1 font" : $"{Fonts.Count:N0} fonts";
            return BrokenCount > 0 ? $"{summary} · {BrokenCount} unreadable" : summary;
        }
    }

    /// <summary>"Font 4 of 128", for the status bar.</summary>
    public string SelectedPosition
    {
        get
        {
            if (SelectedFont is null || Fonts.Count == 0) return string.Empty;

            int index = Fonts.IndexOf(SelectedFont);
            return index < 0 ? string.Empty : $"Font {index + 1:N0} of {Fonts.Count:N0}";
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

        Preview.Face = selected is null || selected.IsBroken ? null : selected.Key;
        Preview.ErrorMessage = selected?.Error;

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
                folder, includeSubfolders: false, cancellationToken);

            BrokenCount = 0;

            // One notification, not one per font: a thousand incremental adds would be
            // a thousand layout passes in every list bound to this collection.
            Fonts.ReplaceAll(files.Select(file => new FontItemViewModel(file)));

            CurrentFolder = folder;

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
            OnPropertyChanged(nameof(FolderSummary));
            OnPropertyChanged(nameof(SelectedPosition));
            OnPropertyChanged(nameof(StatusText));
        }

        Debug.WriteLine($"Listed {Fonts.Count} fonts in {timer.ElapsedMilliseconds} ms");
        StartMetadataScan();
    }

    /// <summary>
    /// Reads every file's metadata in the background, at low priority.
    /// </summary>
    /// <remarks>
    /// The selected font is read first so its name is right immediately; the rest fill in
    /// behind it. Collections expand here, because how many faces a file holds is not
    /// knowable until it has been opened.
    /// </remarks>
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
                OnPropertyChanged(nameof(FolderSummary));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>Reads one file's faces. Returns how many extra rows were inserted.</summary>
    private async Task<int> LoadMetadataAsync(
        FontItemViewModel item, JobPriority priority, CancellationToken cancellationToken)
    {
        FaceSummary[] summaries = await _renderService.ReadSummariesAsync(
            item.File.Path, priority, cancellationToken);

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

        OnPropertyChanged(nameof(SelectedPosition));
        OnPropertyChanged(nameof(StatusText));
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

    // --- Navigation ------------------------------------------------------------

    [RelayCommand]
    private void NextFont() => MoveSelection(1);

    [RelayCommand]
    private void PreviousFont() => MoveSelection(-1);

    [RelayCommand]
    private void FirstFont()
    {
        if (Fonts.Count > 0) SelectedFont = Fonts[0];
    }

    [RelayCommand]
    private void LastFont()
    {
        if (Fonts.Count > 0) SelectedFont = Fonts[^1];
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
        if (Fonts.Count == 0) return;

        int index = SelectedFont is null ? -1 : Fonts.IndexOf(SelectedFont);
        int next = Math.Clamp(index + delta, 0, Fonts.Count - 1);

        if (index < 0) next = delta > 0 ? 0 : Fonts.Count - 1;
        if (next != index) SelectedFont = Fonts[next];
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
        Gallery.Dispose();
        Preview.Dispose();
    }
}
