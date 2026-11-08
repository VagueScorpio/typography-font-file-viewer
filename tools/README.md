# tools

Python tooling that builds the local test-font corpus. It is only needed to run
the test suite against real fonts — building or running the app itself needs
nothing here.

The scripts are committed; the virtual environment and the fonts it produces are
not.

## Setup

Requires Python 3.11 or later. On Windows, `py` is the launcher that ships with it.

```bat
py -3 -m venv tools\.venv
```

```bat
tools\.venv\Scripts\python.exe -m pip install -r tools\requirements.txt
```

That installs **fonttools** with the `woff` extra, which pulls in `brotli` and
`zopfli` — the compressors WOFF 2.0 and WOFF 1.0 need. Without the extra,
fontTools can read `.woff2` but cannot write it.

## Building the corpus

```bat
tools\.venv\Scripts\python.exe tools\make-test-fonts.py
```

Everything lands in `tests/fonts/`, which is git-ignored. The script is safe to
re-run: it keeps what is already there unless you pass `--force`.

| Flag | Effect |
|---|---|
| *(none)* | Fetch, derive and convert everything that is missing |
| `--no-fetch` | Skip downloading; only derive and convert what is already in the folder |
| `--force` | Rebuild files that already exist |
| `--out <dir>` | Build somewhere other than `tests/fonts` |

### What it does

1. **Fetches** open-licensed source fonts — three variable TrueType families
   (Inter, Noto Sans, Noto Serif, all OFL), a CFF-outline OpenType font
   (Source Serif 4, OFL), a PostScript Type 1 font (URW Nimbus Roman), and the
   Spleen bitmap font release, which ships the same font as BDF, PCF **and** a
   Windows `.fon`.
2. **Derives** static instances at `wght` 400 and 700 from each variable font
   using `fontTools.varLib.instancer`, then packs two of them into a
   `TestCollection.ttc` so the collection-handling paths have something to open.
3. **Converts** every `.ttf` and `.otf` in the folder into matching `.woff` and
   `.woff2`, all three built from the same original.
4. **Verifies** each triple opens with the same glyph count and family name.

Step 3 is what Phase 2 rests on. The WOFF and WOFF 2.0 decoders are checked by
opening `X.ttf`, `X.woff` and `X.woff2` and demanding identical glyph counts,
family names and rendered outlines, so every font must exist in all three forms.

### Coverage

| Format | Source |
|---|---|
| `.ttf` (static and variable) | Google Fonts, plus instances derived here |
| `.otf` (CFF outlines) | Source Serif 4 |
| `.ttc` | Built here from two static instances |
| `.woff`, `.woff2` | Built here from every SFNT in the folder |
| `.pfb` / `.pfa` | URW Nimbus Roman |
| `.bdf`, `.pcf`, `.fon` | Spleen |
| `.dfont` | **Not automated** — needs a macOS data-fork suitcase, copied in by hand |

Windows system fonts stay where they are. `cambria.ttc`, `msyh.ttc` and
`seguiemj.ttf` are read in place from `C:\Windows\Fonts` by the tests that need a
large collection, a large CJK font or colour emoji; they are never copied into
the repository.

## Licences

Every font fetched here is open-licensed (OFL 1.1, BSD 2-clause, or the URW
base-35 terms). None of it is committed, so no font licence travels with this
repository. The licence of each source is printed as it is fetched.
