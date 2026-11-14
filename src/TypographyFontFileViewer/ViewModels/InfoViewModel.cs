using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.Services;

namespace TypographyFontFileViewer.ViewModels;

/// <summary>One line of the Info panel.</summary>
/// <param name="Label">The left column.</param>
/// <param name="Value">The right column.</param>
/// <param name="IsLink">True when the value is a URL the user can follow.</param>
public sealed record InfoRow(string Label, string Value, bool IsLink = false)
{
    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>
/// The Info panel: what the font says about itself.
/// </summary>
/// <remarks>
/// Rows are only added where the font actually supplies something, so the panel shows
/// what is there rather than a column of blanks. The SHA-256 is computed on request
/// rather than up front, because hashing a 30 MB CJK font to fill in a line nobody
/// asked for would be rude.
/// </remarks>
public sealed partial class InfoViewModel : ObservableObject
{
    private readonly RenderService _renderService;
    private readonly HashService _hashes;
    private CancellationTokenSource? _pending;

    public InfoViewModel(RenderService renderService, HashService hashes)
    {
        _renderService = renderService;
        _hashes = hashes;
    }

    public ObservableRangeCollection<InfoRow> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComputeHash))]
    public partial string? Sha256 { get; set; }

    /// <summary>True while the hash has not been asked for yet.</summary>
    public bool CanComputeHash => string.IsNullOrEmpty(Sha256);

    [ObservableProperty]
    public partial bool IsHashing { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    private FontInfo? _info;

    /// <summary>The face being described. Setting it reloads the panel.</summary>
    public FaceKey? Face
    {
        get;
        set
        {
            if (field.Equals(value)) return;

            field = value;
            OnPropertyChanged();
            Sha256 = null;
            _ = ReloadAsync();
        }
    }

    public async Task ReloadAsync()
    {
        CancellationTokenSource request = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _pending, request);
        // Cancelled but not disposed: another thread may still be holding this token
        // source, and disposing one out from under an in-flight request is how a
        // superseded render turns into an ObjectDisposedException. It has nothing
        // unmanaged to release, so letting the collector have it is enough.
        previous?.Cancel();

        CancellationToken cancellationToken = request.Token;

        if (Face is not { } face)
        {
            Rows.ReplaceAll([]);
            _info = null;
            Error = null;
            return;
        }

        try
        {
            IsLoading = true;
            Error = null;

            _info = await _renderService.ReadInfoAsync(face, JobPriority.High, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            Rows.ReplaceAll(Build(_info, Sha256));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
        catch (FontLoadException exception)
        {
            _info = null;
            Rows.ReplaceAll([]);
            Error = exception.Message;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsLoading = false;
        }
    }

    /// <summary>Hashes the file, which the panel does only when asked.</summary>
    [RelayCommand]
    private async Task ComputeHash()
    {
        if (Face is not { } face || IsHashing) return;

        try
        {
            IsHashing = true;
            Sha256 = await _hashes.ComputeAsync(face.Path) ?? "could not be read";

            if (_info is not null) Rows.ReplaceAll(Build(_info, Sha256));
        }
        finally
        {
            IsHashing = false;
        }
    }

    /// <summary>Copies the whole panel as plain text.</summary>
    [RelayCommand]
    private void CopyAll()
    {
        if (Rows.Count == 0) return;

        var text = new StringBuilder();
        int width = Rows.Max(row => row.Label.Length);

        foreach (InfoRow row in Rows)
            text.AppendLine($"{row.Label.PadRight(width)}  {row.Value}");

        try
        {
            System.Windows.Clipboard.SetText(text.ToString());
            Status = $"Copied {Rows.Count} lines.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "The clipboard was busy; nothing was copied.";
        }
    }

    /// <summary>
    /// Turns a <see cref="FontInfo"/> into the rows the panel shows.
    /// </summary>
    /// <remarks>
    /// Public and static so it can be tested without a window: what belongs in the panel
    /// and how each value reads is the interesting part, not the binding.
    /// </remarks>
    public static IReadOnlyList<InfoRow> Build(FontInfo info, string? sha256 = null)
    {
        ArgumentNullException.ThrowIfNull(info);

        var rows = new List<InfoRow>(32);

        void Add(string label, string? value, bool isLink = false)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new InfoRow(label, value.Trim(), isLink));
        }

        Add("Family", info.Family);
        Add("Subfamily", info.Subfamily);
        Add("Full name", info.FullName);
        Add("PostScript name", info.PostScriptName);
        Add("Version", info.Version);
        Add("Unique ID", info.UniqueId);

        Add("Format", Describe(info));
        Add("Glyphs", info.GlyphCount > 0 ? $"{info.GlyphCount:N0}" : null);
        Add("Mapped characters", info.MappedCharacterCount > 0
            ? $"{info.MappedCharacterCount:N0}"
            : null);
        Add("Character maps", info.CharmapCount > 0 ? info.CharmapCount.ToString() : null);
        Add("Units per em", info.UnitsPerEm > 0 ? info.UnitsPerEm.ToString() : null);

        if (info.Ascender != 0 || info.Descender != 0)
            Add("Ascender / descender", $"{info.Ascender} / {info.Descender}");

        Add("Line height", info.LineHeight != 0 ? info.LineHeight.ToString() : null);

        if (info.FixedSizes.Count > 0)
            Add("Fixed sizes", string.Join(", ", info.FixedSizes) + " px");

        Add("Scalable", info.IsScalable ? "yes" : "no");
        Add("Kerning", info.HasKerning ? "yes" : "no");
        Add("Glyph names", info.HasGlyphNames ? "yes" : "no");
        Add("Colour glyphs", info.HasColor ? "yes" : "no");
        Add("Fixed width", info.IsFixedWidth ? "yes" : "no");
        Add("Embedding", DescribeEmbedding(info.Embedding));

        if (info.VariationAxes.Count > 0)
        {
            Add("Variable", $"{info.VariationAxes.Count} axes");
            foreach (VariationAxis axis in info.VariationAxes)
                Add($"  {axis.Tag}", $"{axis.Name}: {axis.Minimum:0.##} to {axis.Maximum:0.##}, "
                                   + $"default {axis.Default:0.##}");

            if (info.NamedInstances.Count > 0)
                Add("Named instances", string.Join(", ", info.NamedInstances));
        }

        Add("Designer", info.Designer);
        Add("Manufacturer", info.Manufacturer);
        Add("Copyright", info.Copyright);
        Add("Trademark", info.Trademark);
        Add("Description", info.Description);
        Add("Licence", info.License);
        Add("Licence URL", info.LicenseUrl, isLink: true);
        Add("Vendor URL", info.VendorUrl, isLink: true);
        Add("Designer URL", info.DesignerUrl, isLink: true);

        Add("File", info.Path);
        Add("File size", $"{info.FileLength:N0} bytes");
        Add("Modified", info.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

        if (info.FaceCount > 1) Add("Face", $"{info.FaceIndex + 1} of {info.FaceCount}");

        Add("SHA-256", sha256);

        return rows;
    }

    private static string Describe(FontInfo info)
    {
        string outlines = string.IsNullOrWhiteSpace(info.OutlineFormat) ? "unknown" : info.OutlineFormat;

        // The container and the outlines inside it are different facts, and for a web
        // font both are worth saying: "WOFF2 containing CFF" tells the whole story.
        return info.Container switch
        {
            FontFormat.Woff or FontFormat.Woff2 or FontFormat.Collection
                => $"{info.Container.Badge()} containing {outlines}",
            _ => outlines,
        };
    }

    private static string DescribeEmbedding(EmbeddingPermission permission) => permission switch
    {
        EmbeddingPermission.Installable => "Installable — no restriction",
        EmbeddingPermission.Restricted => "Restricted — embedding not permitted",
        EmbeddingPermission.PreviewAndPrint => "Preview and print only",
        EmbeddingPermission.Editable => "Editable — embedding permitted",
        _ => "not stated",
    };
}
