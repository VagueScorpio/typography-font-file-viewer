namespace TypographyFontFileViewer.ViewModels;

/// <summary>
/// The two ways of looking at a folder of fonts.
/// </summary>
/// <remarks>
/// These are not tabs of the same thing; they are two answers to different questions.
/// "What does this font look like?" is one font, large, with text you control.
/// "Which of these fonts do I want?" is all of them at once, small enough to compare.
/// Phase 6's Glyphs and Info belong inside <see cref="SingleFont"/>, since both are
/// about the one font in hand.
/// </remarks>
public enum ViewMode
{
    /// <summary>One font, large, cycled through with the keyboard.</summary>
    SingleFont = 0,

    /// <summary>Every font in the folder, each drawn in its own face.</summary>
    Gallery = 1,
}
