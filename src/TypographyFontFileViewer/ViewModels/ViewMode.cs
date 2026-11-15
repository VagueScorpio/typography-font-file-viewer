namespace TypographyFontFileViewer.ViewModels;

/// <summary>
/// The two ways of looking at a folder of fonts.
/// </summary>
/// <remarks>
/// These are not tabs of the same thing; they are two answers to different questions.
/// "What does this font look like?" is one font, large, with text you control.
/// "Which of these fonts do I want?" is all of them at once, small enough to compare.
/// Glyphs and Info belong inside <see cref="SingleFont"/>, since both are about the one
/// font in hand.
/// </remarks>
public enum ViewMode
{
    /// <summary>One font, large, cycled through with the keyboard.</summary>
    SingleFont = 0,

    /// <summary>Every font in the folder, each drawn in its own face.</summary>
    Gallery = 1,
}

/// <summary>
/// The panels inside the single-font view. All three describe the one selected font.
/// </summary>
/// <remarks>
/// The order is the order of the strip, and the values are its tab indices.
/// </remarks>
public enum SingleFontPanel
{
    /// <summary>The font drawn at a size you choose, with text you choose.</summary>
    Preview = 0,

    /// <summary>Every character the font maps.</summary>
    Glyphs = 1,

    /// <summary>What the font says about itself.</summary>
    Info = 2,
}
