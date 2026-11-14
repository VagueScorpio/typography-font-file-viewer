using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;

namespace TypographyFontFileViewer.ViewModels;

/// <summary>One visual row of the glyph grid.</summary>
/// <remarks>
/// The grid is built as rows of cells rather than one long wrapping list because WPF
/// virtualizes a stack of rows and does not virtualize a wrap panel. A CJK font with
/// sixty thousand glyphs has to scroll, and only the rows on screen can afford to exist.
/// </remarks>
public sealed class GlyphRowViewModel(IReadOnlyList<GlyphEntry> cells)
{
    public IReadOnlyList<GlyphEntry> Cells { get; } = cells;
}

/// <summary>
/// The Glyphs panel: every character the font maps, and optionally the glyphs no
/// character reaches.
/// </summary>
public sealed partial class GlyphMapViewModel : ObservableObject, IDisposable
{
    public const int MinimumCellSize = 32;
    public const int MaximumCellSize = 128;

    /// <summary>Space around each cell, so neighbouring glyphs do not touch.</summary>
    public const int CellGap = 4;

    private readonly RenderService _renderService;
    private readonly PreviewViewModel _preview;

    private IReadOnlyList<GlyphEntry> _glyphs = [];
    private CancellationTokenSource? _loading;
    private bool _disposed;

    public GlyphMapViewModel(RenderService renderService, PreviewViewModel preview)
    {
        _renderService = renderService;
        _preview = preview;
        _preview.PropertyChanged += OnPreviewChanged;
    }

    public GlyphCellCache Cache { get; } = new();

    public ObservableRangeCollection<GlyphRowViewModel> Rows { get; } = [];

    /// <summary>Bumped when every visible cell must be drawn again.</summary>
    [ObservableProperty]
    public partial int Epoch { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CellPitch))]
    public partial int CellSize { get; set; } = 64;

    /// <summary>How much horizontal room one cell takes, including its gap.</summary>
    public int CellPitch => CellSize + CellGap * 2;

    [ObservableProperty]
    public partial bool ShowUnmapped { get; set; }

    [ObservableProperty]
    public partial string JumpTo { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>The glyph whose details are being shown, or null when the overlay is shut.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    public partial GlyphDetail? Detail { get; set; }

    [ObservableProperty]
    public partial BitmapSource? DetailImage { get; set; }

    public bool IsDetailOpen => Detail is not null;

    /// <summary>Set by the view: how many cells fit across the panel.</summary>
    [ObservableProperty]
    public partial int Columns { get; set; } = 8;

    public bool DarkBackground => _preview.DarkBackground;

    public System.Windows.Media.Brush Background => _preview.PreviewBackground;

    private double Dpi => _preview.Dpi <= 0 ? 96 : _preview.Dpi;

    private RenderStyle Style => DarkBackground ? RenderStyle.Dark : RenderStyle.Light;

    /// <summary>The face being shown. Setting it reloads the grid.</summary>
    public FaceKey? Face
    {
        get;
        set
        {
            if (field.Equals(value)) return;

            field = value;
            OnPropertyChanged();
            _ = ReloadAsync();
        }
    }

    partial void OnCellSizeChanged(int value)
    {
        Rechunk();
        Redraw();
    }

    partial void OnColumnsChanged(int value) => Rechunk();

    partial void OnShowUnmappedChanged(bool value) => _ = ReloadAsync();

    private void OnPreviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewViewModel.DarkBackground))
        {
            OnPropertyChanged(nameof(DarkBackground));
            OnPropertyChanged(nameof(Background));
            Redraw();
        }
        else if (e.PropertyName is nameof(PreviewViewModel.Dpi))
        {
            Redraw();
        }
    }

    public void Redraw() => Epoch++;

    public void AdjustCellSize(int delta)
        => CellSize = Math.Clamp(CellSize + delta, MinimumCellSize, MaximumCellSize);

    // --- Loading ---------------------------------------------------------------

    /// <summary>Reads the font's glyph list and lays it out in rows.</summary>
    public async Task ReloadAsync()
    {
        if (_disposed) return;

        CancellationTokenSource request = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _loading, request);
        // Cancelled but not disposed: another thread may still be holding this token
        // source, and disposing one out from under an in-flight request is how a
        // superseded render turns into an ObjectDisposedException. It has nothing
        // unmanaged to release, so letting the collector have it is enough.
        previous?.Cancel();

        CancellationToken cancellationToken = request.Token;

        if (Face is not { } face)
        {
            _glyphs = [];
            Rows.ReplaceAll([]);
            Status = string.Empty;
            return;
        }

        try
        {
            IsLoading = true;

            _glyphs = await _renderService.ListGlyphsAsync(
                face, ShowUnmapped, JobPriority.High, cancellationToken);

            if (cancellationToken.IsCancellationRequested) return;

            int mapped = _glyphs.Count(glyph => glyph.IsMapped);
            Status = ShowUnmapped
                ? $"{mapped:N0} mapped, {_glyphs.Count - mapped:N0} unmapped"
                : $"{mapped:N0} characters";

            Rechunk();
        }
        catch (OperationCanceledException)
        {
            // Another font was selected; its own load owns the grid now.
        }
        catch (FontLoadException exception)
        {
            _glyphs = [];
            Rows.ReplaceAll([]);
            Status = exception.Message;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsLoading = false;
        }
    }

    /// <summary>
    /// Re-slices the glyph list into rows of the current width.
    /// </summary>
    /// <remarks>
    /// Called whenever the panel is resized or the cells change size. Sixty thousand
    /// glyphs become a few thousand row objects, which is cheap; the cells inside a row
    /// are only realised when that row scrolls into view.
    /// </remarks>
    public void Rechunk()
    {
        int columns = Math.Max(1, Columns);

        if (_glyphs.Count == 0)
        {
            Rows.ReplaceAll([]);
            return;
        }

        var rows = new List<GlyphRowViewModel>((_glyphs.Count / columns) + 1);

        for (int start = 0; start < _glyphs.Count; start += columns)
        {
            int length = Math.Min(columns, _glyphs.Count - start);
            rows.Add(new GlyphRowViewModel([.. _glyphs.Skip(start).Take(length)]));
        }

        Rows.ReplaceAll(rows);
    }

    // --- Cells -----------------------------------------------------------------

    /// <summary>Renders one cell, from the cache when possible.</summary>
    public async Task<BitmapSource?> RenderCellAsync(GlyphEntry glyph, CancellationToken cancellationToken)
    {
        if (_disposed || Face is not { } face) return null;

        int cellPixels = (int)Math.Round(CellSize * Dpi / 96.0);
        var key = new GlyphCellKey(face, glyph.GlyphIndex, cellPixels, DarkBackground);

        BitmapSource? cached = Cache.TryGet(key);
        if (cached is not null) return cached;

        GlyphCellBitmap[] cells = await _renderService.RenderGlyphCellsAsync(
            face, [glyph], cellPixels, Style, JobPriority.Low, cancellationToken);

        if (cancellationToken.IsCancellationRequested || cells.Length == 0) return null;

        GlyphCellBitmap cell = cells[0];
        if (cell.Width <= 0 || cell.Height <= 0) return null;

        var bitmap = BitmapSource.Create(
            cell.Width, cell.Height, Dpi, Dpi,
            System.Windows.Media.PixelFormats.Bgra32, null, cell.Bgra, cell.Stride);
        bitmap.Freeze();

        Cache.Add(key, bitmap);
        return bitmap;
    }

    // --- Interaction -----------------------------------------------------------

    /// <summary>Copies a glyph's character, which is what the map is usually for.</summary>
    [RelayCommand]
    public void CopyCharacter(GlyphEntry glyph)
    {
        if (!glyph.IsMapped) return;

        try
        {
            System.Windows.Clipboard.SetText(char.ConvertFromUtf32(glyph.Codepoint));
            Status = $"Copied {TextLayoutLabel(glyph)}";
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or ArgumentOutOfRangeException)
        {
            Status = "That character could not be copied.";
        }
    }

    private static string TextLayoutLabel(GlyphEntry glyph)
        => glyph.IsMapped
            ? "U+" + glyph.Codepoint.ToString(
                  glyph.Codepoint > 0xFFFF ? "X6" : "X4", CultureInfo.InvariantCulture)
            : $"#{glyph.GlyphIndex}";

    /// <summary>Opens the detail overlay for one glyph.</summary>
    [RelayCommand]
    public async Task OpenDetail(GlyphEntry glyph)
    {
        if (Face is not { } face) return;

        try
        {
            Detail = await _renderService.ReadGlyphDetailAsync(face, glyph);

            int large = (int)Math.Round(180 * Dpi / 96.0);
            GlyphCellBitmap[] cells = await _renderService.RenderGlyphCellsAsync(
                face, [glyph], large, Style, JobPriority.High);

            if (cells.Length > 0 && cells[0].Width > 0)
            {
                var bitmap = BitmapSource.Create(
                    cells[0].Width, cells[0].Height, Dpi, Dpi,
                    System.Windows.Media.PixelFormats.Bgra32, null, cells[0].Bgra, cells[0].Stride);
                bitmap.Freeze();
                DetailImage = bitmap;
            }
            else
            {
                DetailImage = null;
            }
        }
        catch (Exception exception) when (exception is FontLoadException or OperationCanceledException)
        {
            Detail = null;
            DetailImage = null;
        }
    }

    [RelayCommand]
    public void CloseDetail()
    {
        Detail = null;
        DetailImage = null;
    }

    /// <summary>
    /// Finds a character from what the user typed in the jump box.
    /// </summary>
    /// <remarks>
    /// Accepts the three things someone is likely to have to hand: a formal code point
    /// (<c>U+00E9</c>), bare hex (<c>e9</c>), or the character itself pasted in.
    /// </remarks>
    public int ParseJumpTarget(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;

        string trimmed = text.Trim();

        if (trimmed.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];

        if (int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsed)
            && parsed is >= 0 and <= 0x10FFFF)
            return parsed;

        // Not hex, so take it as the character itself.
        try
        {
            return char.ConvertToUtf32(trimmed, 0);
        }
        catch (Exception exception) when (exception is ArgumentException or IndexOutOfRangeException)
        {
            return -1;
        }
    }

    /// <summary>Returns the row holding a code point, or -1 when the font has no such character.</summary>
    public int FindRowFor(int codepoint)
    {
        if (codepoint < 0 || _glyphs.Count == 0) return -1;

        for (int i = 0; i < _glyphs.Count; i++)
            if (_glyphs[i].Codepoint == codepoint)
                return i / Math.Max(1, Columns);

        return -1;
    }

    /// <summary>Works out where to scroll to for whatever is in the jump box.</summary>
    public int ResolveJumpRow()
    {
        int codepoint = ParseJumpTarget(JumpTo);
        if (codepoint < 0)
        {
            Status = "Type a code point like U+00E9, or paste the character itself.";
            return -1;
        }

        int row = FindRowFor(codepoint);
        Status = row < 0
            ? $"This font has no U+{codepoint:X4}."
            : $"Jumped to U+{codepoint:X4}.";

        return row;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _preview.PropertyChanged -= OnPreviewChanged;
        _loading?.Cancel();
        _loading?.Dispose();
        Cache.Clear();
    }
}
