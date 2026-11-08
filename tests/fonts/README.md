# Test fonts

This folder is **git-ignored**. Nothing in it is committed, so no font licenses
travel with the repository. Tests skip with a message when a file is missing.

Drop test fonts here, per blueprint Section 13.1:

| Need | Source |
|---|---|
| TTF / OTF / variable | Google Fonts (OFL) |
| WOFF / WOFF2 | Convert the above with `fonttools`, or fetch from the Google Fonts CSS API |
| Collections (TTC) | `C:\Windows\Fonts\cambria.ttc` — read in place, do not copy here |
| Large CJK | `C:\Windows\Fonts\msyh.ttc` — read in place |
| Color emoji | `C:\Windows\Fonts\seguiemj.ttf` — read in place |
| Windows bitmap (FON/FNT) | the `.fon` files in `C:\Windows\Fonts` — read in place |
| PostScript Type 1 | URW base-35 fonts from the Ghostscript project |
| BDF / PCF | Terminus or GNU Unifont |

For each WOFF test, keep the matching triple — `X.ttf`, `X.woff`, `X.woff2` — so
the decoders can be checked against the original glyph count, family name and
rendered outlines.
