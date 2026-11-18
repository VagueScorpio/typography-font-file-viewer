using System.Windows;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.ViewModels;

namespace TypographyFontFileViewer.Controls;

/// <summary>One cell of the glyph map: a single glyph in a square.</summary>
public sealed class GlyphCellImage : SelfRenderingImage
{
    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
        nameof(Map), typeof(GlyphMapViewModel), typeof(GlyphCellImage),
        new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(GlyphEntry?), typeof(GlyphCellImage),
        new PropertyMetadata(null, OnInputChanged));

    /// <summary>Changes when every cell must be drawn again — new size or colours.</summary>
    public static readonly DependencyProperty EpochProperty = DependencyProperty.Register(
        nameof(Epoch), typeof(int), typeof(GlyphCellImage),
        new PropertyMetadata(0, OnInputChanged));

    public GlyphCellImage()
        : base(HorizontalAlignment.Center, VerticalAlignment.Center) { }

    public GlyphMapViewModel? Map
    {
        get => (GlyphMapViewModel?)GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public GlyphEntry? Glyph
    {
        get => (GlyphEntry?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public int Epoch
    {
        get => (int)GetValue(EpochProperty);
        set => SetValue(EpochProperty, value);
    }

    protected override void QueueRender()
    {
        if (Map is not { } map || Glyph is not { } glyph)
        {
            Clear();
            return;
        }

        Start(
            token => map.RenderCellAsync(glyph, token),
            () => Glyph is { } current && current.Equals(glyph));
    }
}
