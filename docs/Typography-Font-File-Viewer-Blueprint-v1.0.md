# Typography Font File Viewer — Architecture, Design & Build Blueprint (v1.0)

> **How to use this file:** Put it in an empty project folder, open Claude Code there, and say:
> *"Read Typography-Font-File-Viewer-Blueprint-v1.0.md and build the app phase by phase. Stop after each phase, show me what works, and wait for my OK before starting the next."*

---

## Table of Contents

1. Goal & Requirements
2. Language Decision
3. Supported Font Formats
4. Architecture
5. Core Design Decisions
6. Module Specifications
7. Favorites System
8. UI Design
9. Keyboard & Mouse
10. Settings & Portable Files
11. Project Structure & Build Configuration
12. Build Phases (for Claude Code)
13. Testing Plan
14. Acceptance Checklist
15. Out of Scope for v1.0 / Future Ideas
16. Standing Conventions

---

## 1. Goal & Requirements

Build a lightweight, portable Windows font viewer that ships as **one stand-alone .exe**. It runs with nothing installed. No .NET runtime, no installer, no registry writes.

### Must-haves

| # | Requirement |
|---|---|
| R1 | Open every major font format (see Section 3). |
| R2 | When one font is opened, load **every supported font in the same folder** automatically. The user scrolls through them without opening files one at a time. |
| R3 | An "All Fonts" view shows every font in the folder at once, each rendered in its own typeface. |
| R4 | A **Favorite** toggle. Favoriting copies the font file into a `Favorite Fonts` folder next to the exe. |
| R5 | Un-favoriting **deletes the copy** from `Favorite Fonts` (sent to the Recycle Bin as a safety net). |
| R6 | Custom preview text and a size slider. |
| R7 | Glyph / character map for the selected font. |
| R8 | Font info panel (family, style, version, designer, license, glyph count, and so on). |
| R9 | Search/filter the font list by name. Optional "include subfolders" scan. |
| R10 | Never crash on a bad font file. A broken file shows an error card and the rest of the folder keeps working. |

---

## 2. Language Decision

**Recommendation: C# on .NET 10 (LTS), WPF for the UI, FreeType for font decoding/rasterizing, HarfBuzz for text shaping. Published as a self-contained single-file `win-x64` exe.**

| Option | Exe result | Format coverage | Effort | Verdict |
|---|---|---|---|---|
| **C# / .NET 10 + WPF + FreeType + HarfBuzz** | One exe, tens of MB (runtime rides inside) | All formats in Section 3 | Medium | **Recommended** |
| Rust + egui + FreeType | Smaller exe (roughly 10–20 MB) | All, but FreeType must be compiled from C source with zlib + brotli | Higher | Only if exe size matters above everything |
| C++ / Win32 + FreeType | Smallest (a few MB) | All | Highest | Overkill for this app |
| Python + PySide6 + PyInstaller | Large exe, slow startup | Good | Low | Weakest stand-alone story |

**Why C# wins here:**
- It's the same stack as the Simple Rich Text Editor, so the toolchain is already on the dev PC.
- A self-contained single-file publish gives you one exe with nothing to install.
- FreeTypeSharp and HarfBuzzSharp are free, MIT-licensed NuGet packages that ship native Windows binaries. FreeTypeSharp is already used by QuickLook, a popular WPF app, so FreeType inside WPF is proven.
- .NET has zlib and Brotli decompression built in. Those two are what WOFF and WOFF2 need.

**Size trade-off:** The exe will be tens of MB because the .NET runtime and WPF are bundled inside it. That is the cost of "runs with nothing installed." Claude Code should report the final published size.

---

## 3. Supported Font Formats

Detect formats by **magic bytes first, extension second**. A `.ttf` that is really CFF-based should still open.

| Extension(s) | Format | Magic bytes | How it's handled |
|---|---|---|---|
| `.ttf` | TrueType | `00 01 00 00` or `true` | FreeType + HarfBuzz |
| `.otf` | OpenType (CFF or TrueType outlines) | `OTTO` or `00 01 00 00` | FreeType + HarfBuzz |
| `.ttc`, `.otc` | TrueType/OpenType Collection | `ttcf` | Each face becomes its own list entry |
| `.woff` | WOFF 1.0 | `wOFF` | **Managed decoder** → SFNT bytes → FreeType + HarfBuzz |
| `.woff2` | WOFF 2.0 | `wOF2` | **Managed decoder** → SFNT bytes → FreeType + HarfBuzz |
| `.pfb`, `.pfa` | PostScript Type 1 | `80 01` (pfb) / `%!PS-AdobeFont` or `%!FontType1` (pfa) | FreeType. Attach `.afm`/`.pfm` metrics if present |
| `.fon`, `.fnt` | Windows bitmap font | `MZ` (fon) / version word (fnt) | FreeType. Fixed sizes only |
| `.bdf` | X11 bitmap (text) | `STARTFONT` | FreeType. Fixed sizes only |
| `.pcf` (also `.pcf.gz`) | X11 bitmap (binary) | `01 66 63 70` | FreeType. Fixed sizes only |
| `.dfont` | Mac data-fork suitcase | resource-map header | FreeType |

**Companion files** (`.afm`, `.pfm`, `.inf`) are **not** listed as fonts. They're attached to their matching `.pfb`/`.pfa` and travel with it when favorited.

**Not supported in v1.0:** `.eot` (Embedded OpenType), SVG fonts, `.fnt` from game engines (BMFont text format). The scanner skips these quietly.

---

## 4. Architecture

### 4.1 Layer diagram

```
┌──────────────────────────────────────────────────────────────────┐
│ UI LAYER (WPF, MVVM)                                             │
│  MainWindow ─ FontListView ─ PreviewView ─ GalleryView           │
│              GlyphMapView ─ InfoView ─ StatusBar                 │
│  ViewModels: MainVM, FontItemVM, PreviewVM, GalleryVM,           │
│              GlyphMapVM, InfoVM                                  │
└───────────────▲─────────────────────────────────┬────────────────┘
                │ BitmapSource (frozen)            │ async requests
┌───────────────┴─────────────────────────────────▼────────────────┐
│ SERVICES                                                         │
│  FolderScanner   FavoritesService   SettingsService   HashService│
│  RenderService (public async API; hides the engine thread)       │
└───────────────▲─────────────────────────────────┬────────────────┘
                │ results (BGRA pixel buffers,     │ queued jobs
                │ metadata, glyph lists)           │ (priority + cancel)
┌───────────────┴─────────────────────────────────▼────────────────┐
│ FONT ENGINE (one dedicated thread owns ALL native objects)       │
│  FormatSniffer → FormatNormalizer (WOFF/WOFF2 → SFNT)            │
│  FaceCache (LRU) → LoadedFace { pinned bytes, FT_Face, hb_font } │
│  TextLayout (HarfBuzz for SFNT, FreeType fallback for others)    │
│  GlyphRasterizer (FreeType → BGRA)   MetadataReader (name table) │
└───────────────▲──────────────────────────────────────────────────┘
                │ P/Invoke
┌───────────────┴──────────────────────────────────────────────────┐
│ NATIVE: freetype.dll (FreeTypeSharp)  libHarfBuzzSharp.dll       │
│ (bundled inside the single-file exe, self-extracted at runtime)  │
└──────────────────────────────────────────────────────────────────┘
```

### 4.2 How the pieces fit together (plain version)

- **FormatNormalizer is a translator at the border.** Every font that arrives is turned into something the engine understands. WOFF and WOFF2 are compressed wrappers around a normal TrueType/OpenType file. The normalizer unwraps them into plain SFNT bytes. Everything after that point treats the font as an ordinary TTF/OTF.
- **The Font Engine is a single cashier.** FreeType and HarfBuzz objects are not safe to use from several threads at once. So one dedicated thread owns all of them, and every request waits in line at that one counter. The UI never touches native objects. It hands over a ticket (a request) and gets back a finished picture (a frozen bitmap).
- **The FaceCache is a short shelf behind the counter.** The last ~64 fonts used stay loaded, so scrolling back and forth stays instant. The least-recently-used font falls off the shelf when space runs out.

### 4.3 Data flow: "user double-clicks a font in Explorer"

1. `TypographyFontFileViewer.exe "D:\Fonts\RobotoSlab-Bold.ttf"` starts.
2. `FolderScanner` lists every supported file in `D:\Fonts\` (plus subfolders if that setting is on). The list appears immediately, showing file names.
3. The opened file is selected, and `RenderService` renders its preview at high priority.
4. In the background (low priority), the engine opens every other file to read family/style/face count. TTC files expand into one entry per face. List rows update as the metadata arrives.
5. `FavoritesService` marks stars (size pre-filter, then hash; see Section 7).

---

## 5. Core Design Decisions

| Decision | Choice | Reason |
|---|---|---|
| Rendering engine | FreeType for **every** format | One code path. Covers Type 1 and bitmap fonts that WPF/DirectWrite can't open. |
| Shaping | HarfBuzz for SFNT fonts; plain FreeType cmap + kerning for Type 1/bitmap fonts | Correct ligatures, kerning (GPOS) and complex scripts where the font supports them. |
| File loading | Read the whole file into memory (`FileShare.ReadWrite \| FileShare.Delete`), pin it, and use `FT_New_Memory_Face` | **No file locks.** The user can delete, rename or un-favorite a font while it's on screen. |
| WOFF/WOFF2 | Decode in managed C# (built-in `ZLibStream` / `BrotliDecoder`) | HarfBuzz needs plain SFNT bytes anyway, and the prebuilt FreeType may lack Brotli. |
| Threading | One engine thread plus two priority queues (High = selected font/preview, Low = gallery rows/metadata scan) | Native libraries aren't thread-safe. Priority keeps the UI responsive. |
| Cancellation | Every request has a `CancellationToken`. Recycled gallery rows cancel stale jobs. | Fast scrolling must not pile up useless work. |
| Output to UI | Engine returns `byte[]` BGRA plus size. The UI thread builds `BitmapSource.Create(...)` and calls `Freeze()`. | Keeps WPF objects off the engine thread. |
| DPI | Per-Monitor V2 manifest. Pixel size = pt × (DPI ÷ 72). | Crisp text on high-DPI screens. |
| MVVM helper | `CommunityToolkit.Mvvm` (MIT) | `ObservableObject` and `RelayCommand` without boilerplate. |
| Delete behavior | Un-favorite sends the copy to the **Recycle Bin** | The favorite copy might be the user's only copy. |

---

## 6. Module Specifications

### 6.1 `FormatSniffer`
- `FontFormat Sniff(ReadOnlySpan<byte> header, string extension)`: read the first 64 bytes and match the magic-byte table in Section 3. Fall back to the extension.
- `IsSupportedExtension(string ext)`: used by the scanner.
- `IsCompanionExtension(string ext)`: `.afm .pfm .inf`.

### 6.2 `FormatNormalizer`
- `byte[] ToSfnt(byte[] data, FontFormat fmt)`: returns the input unchanged for non-WOFF formats.

**WOFF 1.0 decoder (`WoffDecoder`)**
- 44-byte header: signature, flavor, length, numTables, reserved, totalSfntSize, version, meta/priv offsets.
- 20-byte table directory entries: tag, offset, compLength, origLength, origChecksum.
- If `compLength < origLength`, inflate with `System.IO.Compression.ZLibStream`. Otherwise copy raw.
- Rebuild the SFNT: offset table (sfntVersion = flavor, numTables, searchRange, entrySelector, rangeShift), then table records sorted by tag, then table data 4-byte aligned. Copy the original checksums.

**WOFF 2.0 decoder (`Woff2Decoder`)**: follow the W3C WOFF2 spec. Google's `woff2` C++ library (MIT) is the reference for the logic.
- Header (48 bytes), then a table directory with **UIntBase128** lengths and the 63-entry known-tag table (flag value 63 = explicit tag follows).
- Transform version bits: for `glyf`/`loca`, **version 0 = transformed** and 3 = null transform. For `hmtx`, **version 1 = transformed** and 0 = null. All other tables are null transform only.
- Collection directory when flavor = `ttcf` (WOFF2 can hold a TTC).
- One Brotli stream holds all the table data. Decompress it with `System.IO.Compression.BrotliDecoder`.
- Rebuild `glyf` from the transformed streams (nContour, nPoints, flag, glyph, composite, bbox bitmap + bbox, instruction, and the optional overlap-simple bitmap). Use the 128-entry triplet encoding for point coordinates.
- Regenerate `loca` (short/long per `indexFormat`). Rebuild `hmtx` left-side bearings from glyph xMin when transformed.
- Pad each table to 4 bytes, write the offset table/records, and recompute table checksums plus `head.checkSumAdjustment`.
- **Bounds-check everything.** Reject a `totalSfntSize` over 256 MB, mismatched stream lengths, or overflowing counts. A malformed file must throw `FontLoadException`, never crash or hang.

### 6.3 `FontEngine` (internal) and `RenderService` (public)
- One background `Thread` (not the thread pool), named `FontEngine`, with `IsBackground = true`.
- It owns one `FT_Library` and drains two `Channel<EngineJob>` queues, checking High before Low.
- `RenderService` API (all async, all cancellable):
  - `Task<FaceSummary[]> ReadSummariesAsync(string path, ct)`: one summary per face (index, family, style, format, glyph count, fixed sizes, error).
  - `Task<RenderedText> RenderTextAsync(FaceKey key, string text, double pt, double dpi, int maxWidthPx, RenderStyle style, Priority p, ct)`.
  - `Task<GlyphCellBitmap[]> RenderGlyphCellsAsync(FaceKey key, IReadOnlyList<uint> codepoints, int cellPx, ct)`.
  - `Task<IReadOnlyList<GlyphEntry>> ListGlyphsAsync(FaceKey key, ct)`: codepoint, glyph index, glyph name.
  - `Task<FontInfo> ReadInfoAsync(FaceKey key, ct)`.
- `FaceKey` = (full path, face index, file length, last-write time). Any change to the file invalidates the cache entry.

### 6.4 `FaceCache`
- LRU with two limits: **64 faces** and a **256 MB** byte budget, whichever is hit first.
- `LoadedFace` holds: pinned byte buffer (`GCHandle` pinned or `NativeMemory.Alloc`), `FT_Face`, HarfBuzz `Blob`/`Face`/`Font` (SFNT formats only), and the format. On eviction it releases everything in order: hb_font → hb_face → hb_blob → FT_Done_Face → unpin. The buffer must stay pinned for as long as the FT_Face lives.
- Type 1: after `FT_New_Memory_Face`, call `FT_Attach_Stream` / `FT_Attach_File` for a matching `.afm`/`.pfm` if one exists.

### 6.5 `TextLayout`
- **SFNT path (HarfBuzz):** buffer ← text, `GuessSegmentProperties()`, shape with the font scaled to the pixel size × 64. Read the glyph infos/positions (26.6 fixed-point).
- **Fallback path (Type 1, FON/FNT, BDF, PCF):** map each char via `FT_Get_Char_Index`, advance by `advance.x`, and apply `FT_Get_Kerning` when `FT_HAS_KERNING`.
- Line breaking: honor `\n`, and word-wrap at `maxWidthPx` by measuring word runs. A missing glyph renders as `.notdef` (glyph 0). Never drop characters silently.
- **Bitmap fonts:** `FT_Select_Size` picks the nearest available strike. The UI snaps the size slider to the available sizes and shows them (for example, "Bitmap font — sizes: 8, 10, 12, 14").

### 6.6 `GlyphRasterizer`
- `FT_Load_Glyph(gid, FT_LOAD_DEFAULT | FT_LOAD_COLOR)` → `FT_Render_Glyph(FT_RENDER_MODE_NORMAL)`.
- Handle pixel modes: `GRAY` (8-bit coverage), `MONO` (1-bit, bitmap fonts), `BGRA` (color bitmaps/COLRv0, premultiplied).
- Composite into a BGRA buffer: coverage × text color over the background color. Glyph position = pen + `bitmap_left`, baseline − `bitmap_top`.

### 6.7 `MetadataReader`
- SFNT name table via `FT_Get_Sfnt_Name_Count`/`FT_Get_Sfnt_Name`. Prefer platform 3 (Windows), encoding 1 or 10, language 0x0409, decoded as UTF-16BE. Fall back to platform 1 (Mac Roman; register `CodePagesEncodingProvider`).
- Name IDs to read: 0 copyright, 1 family, 2 subfamily, 4 full name, 5 version, 6 PostScript name, 7 trademark, 8 manufacturer, 9 designer, 10 description, 11 vendor URL, 12 designer URL, 13 license, 14 license URL, 16/17 typographic family/subfamily (prefer these over 1/2 when present).
- Type 1: `FT_Get_PS_Font_Info` (full_name, family_name, version, notice).
- FON/FNT: `FT_Get_WinFNT_Header` (copyright, nominal sizes).
- BDF/PCF: `FT_Get_BDF_Property` (FOUNDRY, FAMILY_NAME, COPYRIGHT).
- Also read: `FT_Get_Font_Format` ("TrueType", "CFF", "Type 1", "Windows FNT", "BDF", "PCF"), container (WOFF/WOFF2/TTC), `num_glyphs`, `units_per_EM`, `num_faces`, mapped character count, `FT_Get_FSType_Flags` (embedding: Installable / Restricted / Preview & Print / Editable), and variable-font axes plus named instances via `FT_Get_MM_Var` (listed only; v1.0 previews the default instance).

### 6.8 `FolderScanner`
- `Task<IReadOnlyList<FontFile>> ScanAsync(string folder, bool includeSubfolders, ct)`.
- Uses `Directory.EnumerateFiles` with `EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = includeSubfolders, AttributesToSkip = Hidden | System }`.
- Filters by supported extension and skips companions. Sorts by natural file-name order ("Font2" before "Font10").
- Supports long paths (`\\?\` handled by .NET) and Unicode file names.

### 6.9 `HashService`
- SHA-256 of file contents, streamed. Results are cached per (path, length, last-write time).

---

## 7. Favorites System

### 7.1 Location
- `FavoritesDir = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "Favorite Fonts")`.
- Use `Environment.ProcessPath`, **not** `Assembly.Location` (that one is empty in single-file apps).
- Create the folder on the first favorite.

### 7.2 Source of truth
The **files in the `Favorite Fonts` folder are the truth.** A small index in `TypographyFontFileViewer.json` speeds up lookups:

```json
"favorites": [
  { "fileName": "RobotoSlab-Bold.ttf", "sha256": "…", "size": 168240,
    "companions": [], "originalPath": "D:\\Fonts\\RobotoSlab-Bold.ttf",
    "addedUtc": "2026-11-08T18:00:00Z" }
]
```

**Startup reconcile:** a file in the folder with no index entry gets hashed and added. An index entry whose file is missing gets dropped. The user can drop files into the folder or delete them by hand, and the app stays correct.

### 7.3 Is this font a favorite?
Think of it as a guest list checked by ID, not by name. Two files called `Roboto.ttf` from different sites may differ, and a renamed copy is still the same font.
1. **Size pre-filter:** the only files that get hashed are those whose byte size matches some favorite's size. Nearly every file is ruled out for free.
2. **Hash match:** SHA-256 equals a favorite's hash → star on.
3. Opening the `Favorite Fonts` folder itself shows every entry starred.

### 7.4 Favorite (star on)
1. If the hash is already in the index, do nothing (already a favorite).
2. Target name = original file name. If a *different* file already has that name, use `Name (2).ext`, `Name (3).ext`, and so on.
3. Copy to a temp name in the folder, then `File.Move` to the final name (atomic; no half-copied files).
4. **Type 1:** also copy the companions (`.afm`, `.pfm`, `.inf` with the same base name), renamed to match the target base name.
5. **TTC/OTC:** favoriting any face copies the whole collection file, and every face of that file shows the star.
6. Update the index and save the settings file.

### 7.5 Un-favorite (star off)
1. Find the index entry by hash.
2. Send the file and its companions to the **Recycle Bin**: `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`.
3. Remove the index entry. If the current view *is* the favorites folder, remove the entry from the list and select the next item.
4. Memory loading (Section 5) means the file is never locked, so this works even while the font is on screen.

### 7.6 Errors
- If the exe folder is read-only (for example, under `Program Files`), show a clear message: *"Can't write to 'Favorite Fonts' next to the app: <path>. Move the app to a folder you can write to."* Do not fall back to another location silently.
- If the copy fails (disk full, permission), leave the star off, show the reason, and leave no partial file behind.

### 7.7 UI entry points
- Star button in the preview header, star column in the list, context menu item, shortcut **Ctrl+D**.
- Toolbar button **★ Favorites** opens the `Favorite Fonts` folder as the current folder. A menu item **Open Favorites folder in Explorer** does what it says.

---

## 8. UI Design

### 8.1 Main window

```
┌────────────────────────────────────────────────────────────────────────────┐
│ File  View  Help                                                           │
│ [Open…] [Open Folder…] [★ Favorites] [🔍 Search fonts…        ] [☐ Subfolders]│
├────────────────────────┬───────────────────────────────────────────────────┤
│ D:\Fonts               │  Roboto Slab Bold                       [★]       │
│ 128 fonts · 6 ★        │  [ Preview ] [ All Fonts ] [ Glyphs ] [ Info ]    │
│ ────────────────────── │ ───────────────────────────────────────────────── │
│ ★ Roboto Slab Bold  TTF│  Text: [The quick brown fox jumps over…   ][▾]    │
│   Roboto Slab Light TTF│  Size: [──────●────────] 48 pt   [◐ Dark bg]      │
│   Inter Regular   WOFF2│                                                   │
│   Cambria (1/2)     TTC│   The quick brown fox jumps over the lazy dog     │
│   Cambria Math(2/2) TTC│                                                   │
│ ⚠ broken-font.otf   OTF│   12  The quick brown fox jumps over the lazy dog │
│   …                    │   18  The quick brown fox jumps over the lazy…    │
│                        │   24  The quick brown fox jumps over…             │
│                        │   36  The quick brown fox…                        │
│                        │   72  The quick…                                  │
├────────────────────────┴───────────────────────────────────────────────────┤
│ RobotoSlab-Bold.ttf · TrueType · 1,284 glyphs · Font 4 of 128       v1.0   │
└────────────────────────────────────────────────────────────────────────────┘
```

### 8.2 Left sidebar: font list
- Virtualized `ListBox` (`VirtualizingPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling`).
- Row: star, display name (family + style once known, file name until then), format badge, and "(n/m)" for collection faces. A ⚠ icon marks files that failed to load; the tooltip shows the reason.
- Context menu: Favorite/Unfavorite, Open containing folder (`explorer.exe /select,"path"`), Copy file path, Copy family name.
- The header shows the current folder, total count, and favorites count.
- Sort options (View menu): File name (default), Family name, Format.

### 8.3 Tab 1: Preview
- Preview text box with a dropdown of presets: pangram, `ABC…Z`, `abc…z`, `0123456789`, punctuation `!@#$%^&*()…`, and "Custom" (whatever the user typed last, remembered).
- Size slider, 6–288 pt, plus a numeric box. **Ctrl+mouse-wheel** over the preview changes the size.
- Large rendering of the text at the chosen size, word-wrapped to the panel width.
- **Waterfall** below it: the same text at 12, 18, 24, 36, 48, 72 pt, each line clipped.
- Dark-background toggle (white on near-black vs. black on white).
- Re-render on every keystroke, debounced to 30 ms. Each new request cancels the previous one.

### 8.4 Tab 2: All Fonts (gallery)
- Every font in the current (filtered) list, one row each. Row: small label (file name · family/style · ★), then the preview text rendered in that font at the **gallery size** (own slider, default 28 pt), single line, clipped.
- Virtualized and recycling. Rows request renders at Low priority when they become visible and cancel when recycled. Rendered bitmaps go into an LRU (about 300 rows) so scrolling back is instant.
- Clicking a row selects that font in the sidebar. Double-click switches to the Preview tab.
- The star on each row toggles the favorite directly.
- A row for a broken font shows the error text instead of a rendering.

### 8.5 Tab 3: Glyphs
- Grid of every mapped character (`FT_Get_First_Char` / `FT_Get_Next_Char`). Virtualized, so a 60,000-glyph CJK font must scroll smoothly.
- Cell size slider (32–128 px). Each cell shows the glyph plus a small `U+XXXX` label.
- Hover shows a tooltip: code point, glyph index, glyph name (`FT_Get_Glyph_Name` when the font has names). Click copies the character to the clipboard. Double-click shows the glyph large in an overlay with metrics (advance, bounding box).
- Checkbox **Show unmapped glyphs**: appends glyphs with no Unicode mapping, by glyph index.
- A "Jump to" box accepts `U+00E9`, `e9`, or the character itself.

### 8.6 Tab 4: Info
- Two-column key/value list with the fields from Section 6.7, plus file path, file size, modified date, and SHA-256 (computed on demand).
- Long fields (license, description) wrap. URLs are clickable and open in the default browser.
- **Copy all info** button copies the list as plain text.

### 8.7 Visual style
- Clean, native look. Windows icon glyphs come from `Segoe Fluent Icons`, falling back to `Segoe MDL2 Assets` (both ship with Windows 10/11), so no icon files need bundling.
- The app UI uses the system font. Only previews render through FreeType.
- Light app chrome by default. Dark mode is out of scope for v1.0 apart from the preview background toggle.

---

## 9. Keyboard & Mouse

| Action | Shortcut |
|---|---|
| Open font file | Ctrl+O |
| Open folder | Ctrl+Shift+O |
| Next / previous font (works from anywhere, even inside the text box) | Ctrl+↓ / Ctrl+↑ |
| Move through list (list focused) | ↑ ↓ PgUp PgDn Home End |
| Toggle favorite | Ctrl+D |
| Show favorites folder | Ctrl+Shift+F |
| Focus search | Ctrl+F (Esc clears) |
| Switch tabs | Ctrl+1 / 2 / 3 / 4 |
| Bigger / smaller preview | Ctrl+= / Ctrl+− or Ctrl+wheel |
| Rescan folder | F5 |
| Toggle subfolders | Ctrl+Shift+S |

**Opening fonts:** command-line argument (a file **or** a folder), drag-and-drop a file or folder onto the window, File → Open, File → Open Folder, and File → Recent folders (last 10). To make the app the default for `.ttf` and other font types, the README explains Windows "Open with → Choose another app → Always use this app". The app itself writes nothing to the registry.

---

## 10. Settings & Portable Files

Everything lives next to the exe:

```
TypographyFontFileViewer.exe
TypographyFontFileViewer.json        ← settings + favorites index (created on first run)
Favorite Fonts\              ← created on first favorite
```

`TypographyFontFileViewer.json` fields: window bounds/state, last folder, last selected file, recent folders, preview text, preview pt size, gallery pt size, glyph cell size, include subfolders, sort order, dark preview, selected tab, favorites index.

- Write atomically (write `*.tmp`, then replace).
- If the file is corrupt, rename it to `TypographyFontFileViewer.json.bad`, start with defaults, and say so in the status bar.

---

## 11. Project Structure & Build Configuration

### 11.1 Folder layout

```
TypographyFontFileViewer/
├─ TypographyFontFileViewer.sln
├─ CHANGELOG.txt
├─ README.md
├─ publish.bat
├─ src/TypographyFontFileViewer/
│  ├─ TypographyFontFileViewer.csproj
│  ├─ app.manifest                  (PerMonitorV2 DPI, longPathAware)
│  ├─ App.xaml / App.xaml.cs        (args parsing, global exception handler)
│  ├─ MainWindow.xaml / .cs
│  ├─ Assets/app.ico
│  ├─ Core/      FontFormat.cs, FormatSniffer.cs, FontFile.cs, FaceKey.cs,
│  │             FaceSummary.cs, FontInfo.cs, FontLoadException.cs
│  ├─ Engine/    FontEngine.cs, EngineJob.cs, FaceCache.cs, LoadedFace.cs,
│  │             TextLayout.cs, GlyphRasterizer.cs, MetadataReader.cs,
│  │             Woff/WoffDecoder.cs, Woff/Woff2Decoder.cs,
│  │             Woff/Woff2GlyfReconstructor.cs, Woff/SfntWriter.cs
│  ├─ Services/  RenderService.cs, FolderScanner.cs, FavoritesService.cs,
│  │             SettingsService.cs, HashService.cs
│  ├─ ViewModels/ MainViewModel.cs, FontItemViewModel.cs, PreviewViewModel.cs,
│  │              GalleryViewModel.cs, GlyphMapViewModel.cs, InfoViewModel.cs
│  ├─ Views/     FontListView.xaml, PreviewView.xaml, GalleryView.xaml,
│  │             GlyphMapView.xaml, InfoView.xaml
│  └─ Controls/  FontRenderImage.cs   (Image that requests/cancels its own render)
└─ tests/TypographyFontFileViewer.Tests/
   ├─ TypographyFontFileViewer.Tests.csproj  (xUnit)
   └─ (tests listed in Section 13)
```

### 11.2 `TypographyFontFileViewer.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <AssemblyName>TypographyFontFileViewer</AssemblyName>
    <Version>1.0.0</Version>
    <ApplicationIcon>Assets\app.ico</ApplicationIcon>
    <ApplicationManifest>app.manifest</ApplicationManifest>

    <!-- Stand-alone single exe -->
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <DebugType>embedded</DebugType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FreeTypeSharp" Version="*" />          <!-- MIT; pin to latest stable -->
    <PackageReference Include="HarfBuzzSharp" Version="*" />          <!-- MIT; pin to latest stable -->
    <PackageReference Include="CommunityToolkit.Mvvm" Version="*" />  <!-- MIT; pin to latest stable -->
  </ItemGroup>
</Project>
```

Claude Code: replace each `*` with the current latest **stable** version and pin it. `IncludeNativeLibrariesForSelfExtract` is required. Without it, `freetype.dll`, `libHarfBuzzSharp.dll` and WPF's own native DLLs end up beside the exe instead of inside it. The fork `McEndu.FreeTypeSharp` (newer FreeType) is an acceptable swap if the main package causes trouble. Note which one was used in the changelog.

### 11.3 `publish.bat`

```bat
@echo off
dotnet publish src\TypographyFontFileViewer\TypographyFontFileViewer.csproj -c Release -o publish
echo.
echo Output: publish\TypographyFontFileViewer.exe
```

Final deliverable folder: `publish\TypographyFontFileViewer.exe` (only the exe; a `.pdb` must not appear because symbols are embedded). Then zip it as `TypographyFontFileViewer-v1.0.zip` with `README.md` and `CHANGELOG.txt`.

---

## 12. Build Phases (for Claude Code)

**Rules for every phase:** build with zero warnings in the new code, run the tests, update `CHANGELOG.txt`, commit to a local git repo with a clear message, then **stop and summarize** what works and how to try it.

### Phase 0: Scaffold & de-risk (the most important phase)
- Create the solution, csproj, manifest, empty MainWindow, and test project. Add the NuGet packages.
- **Spike:** load `C:\Windows\Fonts\arial.ttf` with FreeTypeSharp, shape "Hello" with HarfBuzzSharp, rasterize to BGRA, and show it in a WPF `Image`.
- Run `publish.bat`. Copy **only** the exe to a different folder (for example, `%USERPROFILE%\Desktop\tffv-test\`) and run it. "Hello" must render.
- ✅ Done when: the single exe runs from an empty folder and renders text through FreeType and HarfBuzz.

### Phase 1: Font engine core
- `FormatSniffer`, `FontEngine` thread with priority queues and cancellation, `FaceCache`, `LoadedFace` (memory-loaded, pinned), `TextLayout` (both paths), `GlyphRasterizer` (GRAY/MONO/BGRA), `MetadataReader`.
- TTC/OTC face enumeration, Type 1 companion attach, bitmap-font fixed sizes.
- ✅ Done when: unit tests open each local test font, report correct face counts and names, and render non-empty bitmaps.

### Phase 2: WOFF & WOFF2
- `WoffDecoder`, `Woff2Decoder`, `Woff2GlyfReconstructor`, `SfntWriter` per Section 6.2.
- ✅ Done when: for each test pair (`X.ttf` + `X.woff` + `X.woff2`), the decoded SFNT opens in FreeType with the **same glyph count, family name and glyph outlines** (compare a sample of rendered glyphs pixel-for-pixel), and malformed/truncated files throw `FontLoadException` without crashing.

### Phase 3: Folder browsing & Preview tab
- `FolderScanner`, sidebar list, Preview tab (text, presets, size slider, waterfall, dark bg), status bar.
- Open by command-line arg (file or folder), Open dialogs, drag-drop. Background metadata scan fills in names.
- Navigation keys from Section 9.
- ✅ Done when: opening one font loads the whole folder, the opened font is selected, and Ctrl+↓/↑ walks through every font with instant previews.

### Phase 4: All Fonts gallery
- Virtualized gallery with per-row async render, cancellation, and bitmap LRU. Gallery size slider.
- ✅ Done when: a folder of 1,000+ fonts scrolls smoothly with no UI freezes and no runaway memory.

### Phase 5: Favorites
- `FavoritesService` exactly per Section 7 (index, reconcile, size pre-filter + hash, collisions, Type 1 companions, TTC rule, Recycle Bin, error messages).
- Star UI in the list, the preview header and gallery rows; Ctrl+D; ★ Favorites button; Open in Explorer.
- ✅ Done when: favoriting copies the file next to the exe, un-favoriting moves the copy to the Recycle Bin, stars are correct after restart, and stars are correct for a renamed duplicate in another folder.

### Phase 6: Glyphs & Info tabs
- Glyph grid (virtualized, cell slider, tooltips, click-to-copy, overlay, unmapped toggle, jump box).
- Info panel with copy-all.
- ✅ Done when: a large CJK font (for example, `C:\Windows\Fonts\msyh.ttc`) scrolls smoothly in the glyph grid and Info shows all available fields.

### Phase 7: Search, subfolders, settings, polish
- Search filter (file name, family, style; case-insensitive; updates as you type). Subfolders toggle with rescan, sort options, recent folders, and settings persistence (Section 10).
- Global exception handler that logs to `TypographyFontFileViewer.log` next to the exe and shows a friendly message.
- ✅ Done when: every setting survives a restart and search on 1,000 fonts feels instant.

### Phase 8: Hardening
- Run the Section 13 stress list. Fix everything found.
- ✅ Done when: no crash, hang or file lock appears in any stress case.

### Phase 9: Release
- Final `publish.bat`, a test of the exe from an empty folder, README (features, shortcuts, supported formats, "Open with" steps, where favorites go), CHANGELOG, and `TypographyFontFileViewer-v1.0.zip`.
- Report the exe size and cold-start time.

---

## 13. Testing Plan

### 13.1 Test font sources (none committed to the repo unless the license allows it)
- **Windows system fonts** (read in place; never copied into the repo): `arial.ttf`, `cambria.ttc` (collection), `msyh.ttc` (large CJK), `seguiemj.ttf` (color emoji), and the `.fon` files in `C:\Windows\Fonts` (bitmap).
- **Open-licensed (OFL) fonts** from the Google Fonts GitHub repo for TTF/OTF/variable fonts. Make WOFF/WOFF2 copies with `fonttools` if Python is available on the dev PC; otherwise take them from the Google Fonts web CSS API.
- **Type 1:** the URW base-35 fonts (from the Ghostscript project).
- **BDF/PCF:** any X11 bitmap font set (for example, Terminus or GNU Unifont).
- Put downloaded test fonts in `tests/fonts/` (git-ignored) and have tests skip with a message when a file is missing.

### 13.2 Automated tests (xUnit)
- `FormatSniffer`: every magic-byte case, plus a wrong extension.
- WOFF/WOFF2 round trips (Phase 2 criteria) and malformed-input cases (truncated, bad lengths, a Brotli bomb).
- `FolderScanner`: filtering, companions hidden, natural sort, subfolders on/off, inaccessible folder skipped.
- `FavoritesService` (temp dirs): add, duplicate add, name collision → `(2)`, Type 1 companions, TTC rule, remove, reconcile after manual add/delete, read-only folder error.
- `SettingsService`: round trip, corrupt file → `.bad` + defaults.

### 13.3 Manual stress list (Phase 8)
- A folder with 2,000 fonts, including 50 broken or zero-byte files renamed to `.ttf`/`.woff2`.
- A 30 MB+ CJK font in every tab.
- Delete, rename or overwrite the displayed font in Explorer while viewing it (no lock, no crash, F5 recovers).
- Un-favorite while viewing the favorites folder.
- Unicode and very long file paths; a network share; a USB drive pulled mid-scan.
- Exe run from a read-only folder (favorites error message).
- Rapid Ctrl+↓ held down for 10 seconds (the queue must not grow without limit).
- 200% display scaling and a second monitor with different DPI.

### 13.4 Performance targets
| Metric | Target |
|---|---|
| Cold start to window (after first-run extraction) | < 2 s |
| Folder of 500 fonts: file list visible | < 1 s (metadata fills in after) |
| Preview re-render after keystroke | < 50 ms typical |
| Next/previous font | < 100 ms to new preview |
| Memory with a 2,000-font folder open in the gallery | < 400 MB |

---

## 14. Acceptance Checklist

- [ ] One `TypographyFontFileViewer.exe` runs on a PC with no .NET installed.
- [ ] Opens TTF, OTF, TTC, OTC, WOFF, WOFF2, PFB/PFA, FON/FNT, BDF, PCF, DFONT.
- [ ] Opening one font lists every font in its folder. Scrolling or Ctrl+↓/↑ moves through all of them.
- [ ] The All Fonts tab shows every font rendered in its own face.
- [ ] Favorite copies to `Favorite Fonts` next to the exe. Un-favorite sends the copy to the Recycle Bin.
- [ ] Stars stay correct after restart and for renamed duplicates.
- [ ] Custom preview text + size slider + waterfall.
- [ ] Glyph map with copy-on-click.
- [ ] Info panel with copy-all.
- [ ] Search + subfolder toggle.
- [ ] Bad files show ⚠ and never crash the app.
- [ ] No files locked while viewing.
- [ ] Settings and favorites persist in `TypographyFontFileViewer.json` next to the exe.
- [ ] README, CHANGELOG.txt, `TypographyFontFileViewer-v1.0.zip` delivered.

---

## 15. Out of Scope for v1.0 / Future Ideas

- Install / uninstall fonts to Windows.
- Variable-font axis sliders (v1.0 lists axes and previews the default instance).
- OpenType feature toggles (liga, smcp, ss01…).
- Side-by-side compare of 2–4 fonts.
- Full app dark theme.
- EOT and SVG font formats.
- Printing a specimen sheet / export a specimen PNG.
- FileSystemWatcher auto-refresh (v1.0 uses F5).
- Favorite "collections" (subfolders inside `Favorite Fonts`).

---

## 16. Standing Conventions

- **Versioning:** the app starts at **v1.0**. Every change bumps the version (v1.1, v1.2 …) in the csproj, the title bar/status bar, and the zip name.
- **Changelog:** one rolling `CHANGELOG.txt`, with a table of contents at the top and a heading per version. Every entry records **what** changed, **why**, and **how**.
- **Deliverables:** always provide the full set of files plus a named zip (`TypographyFontFileViewer-vX.Y.zip`), even if only one file changed.
- **Licenses:** only free, open-source NuGet packages (MIT or similar). List each package and its license in the README. FreeType is used under the FreeType License (FTL). Include its credit line in the README and in Help → About.
