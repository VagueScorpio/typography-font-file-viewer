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

/// <summary>
/// The panels inside the single-font view. All three describe the one selected font.
/// </summary>
/// <remarks>
/// The order is the order of the strip, and the values are its tab indices. Glyphs and
/// Info arrive in Phase 6; until then the strip shows Preview alone and selecting either
/// of the others falls back to it.
/// </remarks>
public enum SingleFontPanel
{
    /// <summary>The font drawn at a size you choose, with text you choose.</summary>
    Preview = 0,

    /// <summary>Every character the font maps. Phase 6.</summary>
    Glyphs = 1,

    /// <summary>What the font says about itself. Phase 6.</summary>
    Info = 2,
}
