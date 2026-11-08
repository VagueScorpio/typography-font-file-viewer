# Test fonts

This folder is **git-ignored**. Nothing in it is committed, so no font licences
travel with the repository. Tests skip with a message when a file is missing.

## Building it

From the repository root, once `tools/.venv` exists (see [tools/README.md](../../tools/README.md)):

```bat
tools\.venv\Scripts\python.exe tools\make-test-fonts.py
```

That fetches open-licensed source fonts, derives static instances and a
collection from the variable ones, and converts every SFNT font into matching
`.woff` and `.woff2`. Re-running is safe; it keeps what is already here.

## What the corpus covers

| Format | Files | Exercises |
|---|---|---|
| Variable TrueType | `Inter[opsz,wght].ttf`, `NotoSans[wdth,wght].ttf`, `NotoSerif[wdth,wght].ttf` | `FT_Get_MM_Var` axes, named instances |
| Static TrueType | `Inter-Regular/Bold.ttf`, `NotoSans-*`, `NotoSerif-*` | the ordinary path |
| OpenType, CFF outlines | `SourceSerif4-Regular.otf` | the non-TrueType outline format |
| Collection | `TestCollection.ttc` | one list entry per face, the TTC favourites rule |
| WOFF 1.0 / 2.0 | one pair per SFNT font | the managed decoders, against their own original |
| PostScript Type 1 | `NimbusRoman-Regular.pfa` | the FreeType fallback shaping path |
| X11 bitmap | `spleen-8x16.bdf`, `spleen-8x16.pcf`, `spleen-12x24.pcf` | fixed strikes, `FT_Select_Size` |
| Windows bitmap | `spleen-8x16.fon`, `spleen-12x24.fon` | `FT_Get_WinFNT_Header` |

Roughly 20 MB in all.

## Still to add by hand

- **`.dfont`** — a Mac data-fork suitcase. There is no open source to fetch and
  nothing on Windows produces one; copy one in from a macOS machine if you want
  that path covered.
- **Malformed files** for the hardening pass. The Phase 8 stress list wants
  zero-byte and truncated files renamed to `.ttf` and `.woff2`; the unit tests
  generate those themselves, in temp folders, rather than keeping them here.

## Read in place, never copied here

Windows system fonts stay in `C:\Windows\Fonts`:

| Need | File |
|---|---|
| Large collection | `cambria.ttc` |
| Large CJK font | `msyh.ttc` |
| Colour emoji (`FT_PIXEL_MODE_BGRA`) | `seguiemj.ttf` |
| Windows bitmap fonts | the `.fon` files |
