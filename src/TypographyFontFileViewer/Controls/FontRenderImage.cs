using System.Windows;
using TypographyFontFileViewer.Core;
using TypographyFontFileViewer.ViewModels;

namespace TypographyFontFileViewer.Controls;

/// <summary>One gallery row: a font drawn in its own face.</summary>
public sealed class FontRenderImage : SelfRenderingImage
{
    public static readonly DependencyProperty GalleryProperty = DependencyProperty.Register(
        nameof(Gallery), typeof(GalleryViewModel), typeof(FontRenderImage),
        new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty FaceProperty = DependencyProperty.Register(
        nameof(Face), typeof(FaceKey?), typeof(FontRenderImage),
        new PropertyMetadata(null, OnInputChanged));

    /// <summary>Changes whenever every row needs redrawing — new text, size or colours.</summary>
    public static readonly DependencyProperty EpochProperty = DependencyProperty.Register(
        nameof(Epoch), typeof(int), typeof(FontRenderImage),
        new PropertyMetadata(0, OnInputChanged));

    public FontRenderImage()
        : base(HorizontalAlignment.Left, VerticalAlignment.Center) { }

    public GalleryViewModel? Gallery
    {
        get => (GalleryViewModel?)GetValue(GalleryProperty);
        set => SetValue(GalleryProperty, value);
    }

    public FaceKey? Face
    {
        get => (FaceKey?)GetValue(FaceProperty);
        set => SetValue(FaceProperty, value);
    }

    public int Epoch
    {
        get => (int)GetValue(EpochProperty);
        set => SetValue(EpochProperty, value);
    }

    protected override void QueueRender()
    {
        if (Gallery is not { } gallery || Face is not { } face)
        {
            Clear();
            return;
        }

        Start(
            token => gallery.RenderRowAsync(face, token),
            () => Face is { } current && current.Equals(face));
    }
}
