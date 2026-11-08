# Simple Font Viewer

A lightweight, portable Windows font viewer that ships as **one stand-alone `.exe`**.
No installer, no .NET runtime to install, no registry writes. Copy the exe anywhere
and run it.

> **Status: pre-Phase 0.** This repository currently holds the blueprint, the build
> conventions and the folder outline. The application itself has not been built yet.
> See [Roadmap](#roadmap) and [the blueprint](docs/Simple-Font-Viewer-Blueprint-v1.0.md).

---

## What it will do

- **Open one font, get the whole folder.** Double-click any font file and every
  supported font in that folder loads into a scrollable list — no opening files
  one at a time.
- **All Fonts view.** Every font in the folder rendered at once, each in its own
  typeface.
- **Favorites.** Star a font and the file is *copied* into a `Favorite Fonts`
  folder next to the exe. Un-starring sends that copy to the Recycle Bin.
- **Custom preview text,** a 6–288 pt size slider and a waterfall of sizes.
- **Glyph / character map** with click-to-copy, virtualized for 60,000-glyph CJK fonts.
- **Font info panel** — family, style, version, designer, license, embedding
  permissions, glyph count, variable-font axes, SHA-256.
- **Search and filter** by name, with an optional subfolder scan.
- **Never crashes on a bad font.** A broken file gets an error card; the rest of
  the folder keeps working.

## Supported formats

| Extension(s) | Format |
|---|---|
| `.ttf` | TrueType |
| `.otf` | OpenType (CFF or TrueType outlines) |
| `.ttc`, `.otc` | TrueType / OpenType Collection (one list entry per face) |
| `.woff` | WOFF 1.0 (managed decoder to SFNT) |
| `.woff2` | WOFF 2.0 (managed decoder to SFNT) |
| `.pfb`, `.pfa` | PostScript Type 1 (`.afm` / `.pfm` metrics attached when present) |
| `.fon`, `.fnt` | Windows bitmap font (fixed sizes) |
| `.bdf` | X11 bitmap, text (fixed sizes) |
| `.pcf`, `.pcf.gz` | X11 bitmap, binary (fixed sizes) |
| `.dfont` | Mac data-fork suitcase |

Formats are detected by **magic bytes first, extension second**, so a `.ttf` that
is really CFF-based still opens. `.afm`, `.pfm` and `.inf` are treated as
companion files, not fonts. Not supported in v1.0: `.eot`, SVG fonts, BMFont
`.fnt` — the scanner skips these quietly.

## Keyboard and mouse

| Action | Shortcut |
|---|---|
| Open font file | `Ctrl+O` |
| Open folder | `Ctrl+Shift+O` |
| Next / previous font (works from anywhere, even in the text box) | `Ctrl+Down` / `Ctrl+Up` |
| Move through list (list focused) | `Up` `Down` `PgUp` `PgDn` `Home` `End` |
| Toggle favorite | `Ctrl+D` |
| Show favorites folder | `Ctrl+Shift+F` |
| Focus search (`Esc` clears) | `Ctrl+F` |
| Switch tabs | `Ctrl+1` / `2` / `3` / `4` |
| Bigger / smaller preview | `Ctrl+=` / `Ctrl+-`, or `Ctrl` + mouse wheel |
| Rescan folder | `F5` |
| Toggle subfolders | `Ctrl+Shift+S` |

Fonts can also be opened by command-line argument (a file **or** a folder), by
dragging a file or folder onto the window, or from File then Recent folders.

## Making it the default font viewer

The app writes nothing to the registry, so file associations stay yours to set:

1. Right-click a `.ttf` (or any font file) in Explorer.
2. **Open with**, then **Choose another app**.
3. Pick **More apps**, then **Look for another app on this PC**, and browse to
   `SimpleFontViewer.exe`.
4. Tick **Always use this app to open .ttf files**.

Repeat for each extension you want to claim.

## Portable files

Everything the app keeps lives next to the exe:

```
SimpleFontViewer.exe
SimpleFontViewer.json        <- settings + favorites index (created on first run)
SimpleFontViewer.log         <- only if an unhandled error occurs
Favorite Fonts\              <- created on your first favorite
```

If the exe sits in a folder you cannot write to (for example `Program Files`),
favoriting reports a clear error rather than silently storing files elsewhere.
Move the exe to a writable folder.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```bat
publish.bat
```

Output: `publish\SimpleFontViewer.exe` — a self-contained, single-file `win-x64`
build with the runtime, WPF and the native FreeType / HarfBuzz libraries packed
inside. Symbols are embedded, so no `.pdb` ships beside the exe. Expect an exe of
tens of MB; that is the cost of running with nothing installed.

## Architecture at a glance

```
UI LAYER (WPF, MVVM)       MainWindow, FontListView, PreviewView,
                           GalleryView, GlyphMapView, InfoView
     | frozen BitmapSource        | async, cancellable requests
SERVICES                   FolderScanner, FavoritesService,
                           SettingsService, HashService, RenderService
     | BGRA buffers, metadata     | queued jobs (priority + cancel)
FONT ENGINE                one thread owns ALL native objects:
                           FormatSniffer, FormatNormalizer (WOFF to SFNT),
                           FaceCache (LRU), TextLayout,
                           GlyphRasterizer, MetadataReader
     | P/Invoke
NATIVE                     freetype.dll, libHarfBuzzSharp.dll
```

Three decisions carry most of the design:

- **FreeType renders every format.** One code path, and it covers Type 1 and
  bitmap fonts that DirectWrite cannot open. HarfBuzz shapes SFNT fonts for
  correct ligatures and kerning; Type 1 and bitmap fonts use FreeType's cmap
  plus kerning directly.
- **One engine thread owns every native object.** FreeType and HarfBuzz are not
  thread-safe, so all requests queue at a single counter with two priorities
  (High for the selected font's preview, Low for gallery rows and metadata
  scans), and every request is cancellable.
- **Fonts are read fully into memory and pinned.** Nothing is ever file-locked, so
  you can delete, rename or un-favorite a font while it is on screen.

Full detail — module specs, the WOFF2 decoder, the favorites matching rules — is in
[the blueprint](docs/Simple-Font-Viewer-Blueprint-v1.0.md).

## Roadmap

Built phase by phase; each phase ends with zero new warnings, passing tests, a
changelog entry and a commit. Full criteria in blueprint Section 12.

| Phase | Scope | Status |
|---|---|---|
| 0 | Scaffold and de-risk: solution, csproj, manifest, NuGet, FreeType + HarfBuzz spike, single-file publish | Not started |
| 1 | Font engine core: sniffer, engine thread, FaceCache, TextLayout, rasterizer, metadata | Not started |
| 2 | WOFF and WOFF2 managed decoders | Not started |
| 3 | Folder browsing and Preview tab | Not started |
| 4 | All Fonts gallery (virtualized, 1,000+ fonts) | Not started |
| 5 | Favorites (copy, Recycle Bin, hash matching) | Not started |
| 6 | Glyphs and Info tabs | Not started |
| 7 | Search, subfolders, settings persistence, polish | Not started |
| 8 | Hardening against the stress list | Not started |
| 9 | Release: README, changelog, zip, size and cold-start report | Not started |

## Repository layout

```
docs/                             Blueprint and design documents
publish.bat                       Single-file release build
src/SimpleFontViewer/
  Core/                           Formats, sniffer, FaceKey, FontInfo, exceptions
  Engine/                         Engine thread, FaceCache, layout, rasterizer, metadata
  Engine/Woff/                    WOFF 1.0 / WOFF 2.0 decoders, SFNT writer
  Services/                       Render, scan, favorites, settings, hashing
  ViewModels/  Views/  Controls/  WPF MVVM layer
  Assets/                         Application icon
tests/SimpleFontViewer.Tests/     xUnit tests
tests/fonts/                      Local test fonts (git-ignored)
```

## Third-party components and licenses

| Component | License |
|---|---|
| [FreeTypeSharp](https://github.com/ryancheung/FreeTypeSharp) | MIT |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT |
| FreeType (native, via FreeTypeSharp) | FreeType License (FTL) |
| HarfBuzz (native, via HarfBuzzSharp) | MIT |

> Portions of this software are copyright 2026 The FreeType Project
> (www.freetype.org). All rights reserved.

This credit line also appears in **Help**, then **About**.

Test fonts are never committed to this repository. Windows system fonts are read
in place; open-licensed (OFL) fonts are downloaded into the git-ignored
`tests/fonts/` folder, and tests skip with a message when a file is missing.

## License

This project is released under the [MIT License](LICENSE).
