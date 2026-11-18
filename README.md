# Typography Font File Viewer

A lightweight, portable Windows font viewer that ships as **one stand-alone `.exe`**.
No installer, no .NET runtime to install, no registry writes. Copy the exe anywhere
and run it.

> **Status: v1.1.** Every format the blueprint promises opens, both views work,
> favourites persist, and the exe is **60.4 MB** reaching a window in **under a
> second**. Since v1.0: an optimization pass took out 337 lines without changing
> behaviour, and an adversarial pass found and fixed seven ways to break it. See
> [Roadmap](#roadmap), [Known limits](#known-limits) and
> [the blueprint](docs/Typography-Font-File-Viewer-Blueprint-v1.1.md).

---

## Running it

Take the zip from
[the latest release](https://github.com/VagueScorpio/typography-font-file-viewer/releases/latest),
pull `TypographyFontFileViewer.exe` out of it, put it in a folder you can write to, and
double-click it. Nothing is installed, nothing is unpacked beside it, and nothing is
written to the registry. To remove it, delete the exe and the two or three files listed
under [Portable files](#portable-files).

Windows SmartScreen will warn about an unsigned exe downloaded from the internet;
**More info** then **Run anyway** gets past it. Building it yourself from this
repository avoids the warning entirely.

The zip is a release asset, not a committed file — `publish.bat` produces the identical
exe from this source. See [Building](#building).

You can also start it with a font file or a folder as the only argument, drag either
onto the window, or make it the handler for font files — see
[Making it the default font viewer](#making-it-the-default-font-viewer).

## What it does

- **Open one font, get the whole folder.** Double-click any font file and every
  supported font in that folder loads into a scrollable list — no opening files
  one at a time.
- **Two views, one switch.** *Single font* shows one face large with text you
  control; *Gallery* shows every font in the folder at once, each drawn in its own
  typeface, at Small, Medium, Large or Extra Large. Double-click any font in the
  gallery to open it large.
- **Favorites.** Star a font and the file is *copied* into a `Favorite Fonts`
  folder next to the exe. Un-starring sends that copy to the Recycle Bin.
- **Custom preview text,** a 6–288 pt size slider and a waterfall of sizes.
- **Colour fonts in colour.** Emoji and other `COLR`/`CPAL` or bitmap-colour faces are
  composited rather than flattened to outlines.
- **Glyph map** with click-to-copy, a jump box, and a details overlay; virtualized
  and tested on a 30,000-glyph CJK font.
- **Font info panel** — family, style, version, designer, license, embedding
  permissions, glyph count, variable-font axes, SHA-256.
- **Search** by file name, family or style as you type, with an optional subfolder scan,
  three sort orders and a recent-folders menu. Everything is remembered for next time.
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
| `.dfont` | Mac data-fork suitcase (one list entry per `sfnt` resource) |

Formats are detected by **magic bytes first, extension second**, so a `.ttf` that
is really CFF-based still opens. `.afm`, `.pfm` and `.inf` are treated as
companion files, not fonts. Not supported: `.eot`, SVG fonts, BMFont
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
| Single font view — Preview | `Ctrl+1` |
| Gallery view | `Ctrl+2` |
| Single font view — Glyphs, Info | `Ctrl+3`, `Ctrl+4` |
| Flip between the two views | `Ctrl+Tab` |
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
   `TypographyFontFileViewer.exe`.
4. Tick **Always use this app to open .ttf files**.

Repeat for each extension you want to claim.

## Portable files

Everything the app keeps lives next to the exe:

```
TypographyFontFileViewer.exe
TypographyFontFileViewer.json        <- settings + favorites index (created on first run)
TypographyFontFileViewer.log         <- only if an unhandled error occurs
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

`publish.bat` empties `publish\` first, so what is left is exactly what the build
produced: one `TypographyFontFileViewer.exe`, self-contained and single-file for
`win-x64`, with the runtime, WPF and the native FreeType / HarfBuzz libraries packed
inside. Symbols are embedded, so no `.pdb` ships beside it.

At v1.1 the exe is **63,299,693 bytes (60.4 MB)** and reaches a usable window in
**0.90–1.01 s** with a font on the command line, measured from a folder holding
nothing but the exe. The blueprint's target is under 2 s. Tens of MB is the cost of
needing nothing installed on the target PC.

The size has moved by under a kilobyte since v1.0 (63,298,809 bytes) in both
directions, despite the app's own IL shrinking by about that much: the exe is a
compressed bundle of a 136 MB payload of which this app is 0.3%, so a kilobyte of IL
moving changes how the rest compresses by more than the kilobyte itself.

To build and test during development:

```bat
dotnet build TypographyFontFileViewer.slnx
```

```bat
dotnet test --solution TypographyFontFileViewer.slnx
```

The tests run on Microsoft.Testing.Platform rather than VSTest, which the .NET 10 SDK
requires a project to opt into; `global.json` does that for the whole repository.

### Test fonts

Tests that need real fonts read them from `tests/fonts/`, which is git-ignored and
built on demand with [fonttools](https://github.com/fonttools/fonttools):

```bat
py -3 -m venv tools\.venv
```

```bat
tools\.venv\Scripts\python.exe -m pip install -r tools\requirements.txt
```

```bat
tools\.venv\Scripts\python.exe tools\make-test-fonts.py
```

That fetches open-licensed fonts, derives static instances from the variable ones,
packs two of them into a `.ttc` and into a `.dfont`, and converts every SFNT font into
matching `.woff` and `.woff2` — the triples the WOFF decoders are checked against.
Roughly 21 MB, covering every format the app opens. Details in
[tools/README.md](tools/README.md). Tests skip with a message when a font is missing,
so the suite runs without this step.

## Architecture at a glance

```
UI LAYER (WPF, MVVM)       MainWindow, FontListView, PreviewView,
                           GalleryView, GlyphMapView, InfoView,
                           SelfRenderingImage (a row, or a glyph cell,
                             that fetches its own picture and gives up
                             when it is recycled)
     | frozen BitmapSource        | async, cancellable requests
SERVICES                   FolderScanner, FavoritesService,
                           SettingsService, HashService (bounded LRU),
                           RenderService, FrozenBitmap
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
[the blueprint](docs/Typography-Font-File-Viewer-Blueprint-v1.1.md).

## Roadmap

Built phase by phase; each phase ends with zero new warnings, passing tests, a
changelog entry and a commit. Full criteria in blueprint Section 12.

| Phase | Scope | Status |
|---|---|---|
| 0 | Scaffold and de-risk: solution, csproj, manifest, NuGet, FreeType + HarfBuzz spike, single-file publish | **Done** |
| 1 | Font engine core: sniffer, engine thread, FaceCache, TextLayout, rasterizer, metadata | **Done** |
| 2 | WOFF and WOFF2 managed decoders | **Done** |
| 3 | Folder browsing and Preview tab | **Done** |
| 4 | All Fonts gallery (virtualized, 1,000+ fonts) | **Done** |
| 5 | Favorites (copy, Recycle Bin, hash matching) | **Done** |
| 6 | Glyphs and Info panels | **Done** |
| 7 | Search, subfolders, settings persistence, polish | **Done** |
| 8 | Hardening against the stress list | **Done** |
| 9 | Release: README, changelog, zip, size and cold-start report | **Done** |

Since v1.0, two passes over the finished thing:

| Pass | Scope | Result |
|---|---|---|
| Optimization | dead code, duplication, idiom, structure | −337 code lines, no behaviour change |
| Hardening | an adversarial read of every line, then trying to break it | 7 findings, all fixed, 10 regression tests |

Ideas deliberately left out of v1.0 — installing fonts to Windows, variable-font axis
sliders, OpenType feature toggles, side-by-side compare, a full dark theme, EOT and
SVG, specimen export, and auto-refresh on file changes — are listed in blueprint
Section 15.

## Known limits

Measured and recorded rather than smoothed over:

- **Memory misses its target.** Blueprint Section 13.4 asks for under 400 MB with a
  2,000-font folder open in the gallery. Listing and the full metadata sweep settle at
  297 MB, but scrolling the whole gallery repeatedly reaches 440–500 MB. Every cache is
  bounded and the figure oscillates rather than climbing, and the app stays responsive,
  but it is over. What remains is churn, not retention: each render allocates a BGRA
  buffer, anything over 85 KB lands on the large object heap, and that heap is not
  compacted by default. The fix is to pool those buffers, which first needs
  `RenderedText` to expose a length-bounded view rather than a raw array.
- **Folders behind a junction are skipped.** With Subfolders on, the scan steps over
  directory junctions and symbolic links rather than following them. A junction
  pointing back at an ancestor would otherwise be walked until the path outgrew what
  Windows accepts — which is what v1.1 fixed. The cost is that a font living *only*
  behind a junction is not found.
- **Not yet verified by a person:** a network share, a USB drive physically pulled
  mid-scan, 200% display scaling, and a second monitor at a different DPI. v1.1 fixed
  the code behind the pulled-drive case — an error during the walk no longer escapes
  the scanner — but the hardware behaviour around it is still untested.
- **A clean PC is still untested.** The exe starts here with `dotnet` off `PATH` and
  `DOTNET_ROOT` pointed at nothing, which is as close as a machine with .NET installed
  can get to proving it is self-contained.
- **Variable fonts preview at their default instance.** Axes are listed in the Info
  panel; there are no sliders.

## Repository layout

```
docs/                             Blueprint and design documents
publish.bat                       Single-file release build
global.json                       Opts the repo into the Microsoft.Testing.Platform runner
TypographyFontFileViewer.slnx     Solution (the .NET 10 SDK's XML solution format)
tools/                            fonttools scripts that build the test-font corpus
src/TypographyFontFileViewer/
  Core/                           Formats, sniffer, FaceKey, FontInfo, exceptions
  Engine/                         Engine thread, FaceCache, layout, rasterizer, metadata
  Engine/Woff/                    WOFF 1.0 / WOFF 2.0 decoders, SFNT writer
  Services/                       Render, scan, favorites, settings, hashing
  ViewModels/  Views/  Controls/  WPF MVVM layer
  Assets/                         Application icon
tests/TypographyFontFileViewer.Tests/     xUnit tests
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
