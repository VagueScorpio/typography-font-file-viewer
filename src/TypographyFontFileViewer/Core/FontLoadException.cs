namespace TypographyFontFileViewer.Core;

/// <summary>
/// Thrown when a font file cannot be read, decoded or rendered.
/// </summary>
/// <remarks>
/// Every failure path in the engine funnels into this one exception type so the UI
/// can show a per-file error card and keep the rest of the folder working
/// (requirement R10). A malformed font must never crash, hang or take down a scan.
/// </remarks>
public sealed class FontLoadException : Exception
{
    public FontLoadException(string message) : base(message) { }

    public FontLoadException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>The file the failure relates to, when one is known.</summary>
    public string? Path { get; init; }
}
