#!/usr/bin/env python3
"""Build the local test-font corpus for Typography Font File Viewer.

The repository never commits fonts (see tests/fonts/README.md). This script
assembles the corpus on demand instead, in three steps:

  1. Fetch a small set of open-licensed source fonts.
  2. Derive extra formats from them with fonttools: static instances snapped out
     of variable fonts, a TrueType Collection built from two of those statics.
  3. Convert every SFNT font present into matching .woff and .woff2 files.

Step 3 is the one Phase 2 depends on. The WOFF decoders are checked by opening
X.ttf, X.woff and X.woff2 and demanding the same glyph count, the same family
name and the same rendered outlines, so each font has to exist in all three
forms, built from one original.

Usage (from the repository root):

    tools\\.venv\\Scripts\\python.exe tools\\make-test-fonts.py
    tools\\.venv\\Scripts\\python.exe tools\\make-test-fonts.py --no-fetch
    tools\\.venv\\Scripts\\python.exe tools\\make-test-fonts.py --force

Everything it writes lands in tests/fonts/, which is git-ignored.
"""

from __future__ import annotations

import argparse
import io
import sys
import tarfile
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT = REPO_ROOT / "tests" / "fonts"

USER_AGENT = "typography-font-file-viewer-test-font-builder"
TIMEOUT_SECONDS = 60

# Sources are pinned to project repositories that publish the fonts under an
# open licence. Each entry says what the font is there to exercise.
@dataclass(frozen=True)
class Source:
    name: str          # file name written into tests/fonts/
    url: str
    licence: str
    exercises: str


@dataclass(frozen=True)
class ArchiveSource:
    """A .tar.gz holding several fonts; only the named members are taken out."""
    label: str
    url: str
    licence: str
    exercises: str
    members: tuple[str, ...]   # base names to extract, everything else ignored


SOURCES: tuple[Source, ...] = (
    Source(
        "Inter[opsz,wght].ttf",
        "https://raw.githubusercontent.com/google/fonts/main/ofl/inter/Inter%5Bopsz%2Cwght%5D.ttf",
        "OFL 1.1",
        "variable TrueType, two axes",
    ),
    Source(
        "NotoSans[wdth,wght].ttf",
        "https://raw.githubusercontent.com/google/fonts/main/ofl/notosans/NotoSans%5Bwdth%2Cwght%5D.ttf",
        "OFL 1.1",
        "variable TrueType, large glyph set",
    ),
    Source(
        "NotoSerif[wdth,wght].ttf",
        "https://raw.githubusercontent.com/google/fonts/main/ofl/notoserif/NotoSerif%5Bwdth%2Cwght%5D.ttf",
        "OFL 1.1",
        "variable TrueType, serif outlines",
    ),
    Source(
        "SourceSerif4-Regular.otf",
        "https://raw.githubusercontent.com/adobe-fonts/source-serif/release/OTF/SourceSerif4-Regular.otf",
        "OFL 1.1",
        "OpenType with CFF outlines, not TrueType",
    ),
    Source(
        "NimbusRoman-Regular.pfb",
        "https://raw.githubusercontent.com/ArtifexSoftware/urw-base35-fonts/master/fonts/NimbusRoman-Regular.t1",
        "AFPL / OFL (URW base-35)",
        "PostScript Type 1, the FreeType fallback shaping path",
    ),
)

# Spleen's release tarball is the one place that ships the same bitmap font as
# BDF, PCF and a Windows .fon, which covers three formats FreeType handles very
# differently from outline fonts.
ARCHIVES: tuple[ArchiveSource, ...] = (
    ArchiveSource(
        "spleen 2.2.0",
        "https://github.com/fcambus/spleen/releases/download/2.2.0/spleen-2.2.0.tar.gz",
        "BSD 2-clause",
        "X11 bitmap (BDF text, PCF binary) and Windows bitmap (.fon), fixed sizes",
        members=(
            "spleen-8x16.bdf",
            "spleen-8x16.pcf",
            "spleen-8x16.fon",
            "spleen-12x24.pcf",
            "spleen-12x24.fon",
        ),
    ),
)

# Weights snapped out of each variable font, so the corpus has ordinary static
# TrueType files too. Phase 2 compares these against their own WOFF copies.
STATIC_INSTANCES: tuple[tuple[str, int], ...] = (("Regular", 400), ("Bold", 700))


def log(message: str) -> None:
    print(message, flush=True)


def fetch(source: Source, out_dir: Path, force: bool) -> Path | None:
    target = out_dir / source.name

    # A Type 1 font is saved as .pfb or .pfa depending on what the archive holds,
    # so look for either before deciding it is missing.
    candidates = [target]
    if target.suffix.lower() == ".pfb":
        candidates.append(target.with_suffix(".pfa"))

    for candidate in candidates:
        if candidate.exists() and not force:
            log(f"  kept     {candidate.name}  (already present)")
            return candidate

    request = urllib.request.Request(source.url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=TIMEOUT_SECONDS) as response:
            data = response.read()
    except (urllib.error.URLError, TimeoutError) as error:
        log(f"  SKIPPED  {source.name}  ({error})")
        return None

    if not data:
        log(f"  SKIPPED  {source.name}  (empty response)")
        return None

    # The URW archive stores Type 1 fonts as .t1; check the PFB magic so the file
    # is not named something the scanner would believe without reason.
    if target.suffix.lower() == ".pfb" and data[:2] != b"\x80\x01":
        if data[:2] == b"%!":
            target = target.with_suffix(".pfa")
        else:
            log(f"  SKIPPED  {source.name}  (not a Type 1 font)")
            return None

    target.write_bytes(data)
    log(f"  fetched  {target.name}  ({len(data):,} bytes, {source.licence})")
    return target


def fetch_archive(archive: ArchiveSource, out_dir: Path, force: bool) -> list[Path]:
    """Pull only the named members out of a tarball, flattened into out_dir."""
    wanted = set(archive.members)
    present = {name for name in wanted if (out_dir / name).exists()}
    if present == wanted and not force:
        for name in sorted(wanted):
            log(f"  kept     {name}  (already present)")
        return [out_dir / name for name in sorted(wanted)]

    request = urllib.request.Request(archive.url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=TIMEOUT_SECONDS) as response:
            payload = response.read()
    except (urllib.error.URLError, TimeoutError) as error:
        log(f"  SKIPPED  {archive.label}  ({error})")
        return []

    produced: list[Path] = []
    try:
        with tarfile.open(fileobj=io.BytesIO(payload), mode="r:gz") as tar:
            for member in tar.getmembers():
                # Match on the base name only, and never trust a path from an
                # archive: anything with a separator or a parent reference in the
                # name we write is rejected outright.
                base = Path(member.name).name
                if not member.isfile() or base not in wanted:
                    continue
                if base != Path(base).name or base in {"", ".", ".."}:
                    continue

                extracted = tar.extractfile(member)
                if extracted is None:
                    continue
                data = extracted.read()
                target = out_dir / base
                target.write_bytes(data)
                log(f"  fetched  {base}  ({len(data):,} bytes, {archive.licence})")
                produced.append(target)
    except (tarfile.TarError, OSError) as error:
        log(f"  SKIPPED  {archive.label}  ({type(error).__name__}: {error})")
        return produced

    for name in sorted(wanted - {path.name for path in produced}):
        log(f"  SKIPPED  {name}  (not in {archive.label})")

    return produced


def derive_statics(variable_path: Path, force: bool) -> list[Path]:
    """Snap fixed weights out of a variable font so the corpus has static TTFs."""
    from fontTools.ttLib import TTFont
    from fontTools.varLib import instancer

    produced: list[Path] = []
    stem = variable_path.name.split("[")[0]

    for label, weight in STATIC_INSTANCES:
        target = variable_path.with_name(f"{stem}-{label}.ttf")
        if target.exists() and not force:
            log(f"  kept     {target.name}  (already present)")
            produced.append(target)
            continue

        try:
            font = TTFont(variable_path)
            axes = {axis.axisTag for axis in font["fvar"].axes}
            if "wght" not in axes:
                font.close()
                continue

            try:
                static = instancer.instantiateVariableFont(
                    font, {"wght": weight}, inplace=False, updateFontNames=True
                )
            except Exception:
                # updateFontNames needs a STAT table with the right axis values;
                # without one the instance is still correct, just named for the
                # default style.
                font = TTFont(variable_path)
                static = instancer.instantiateVariableFont(
                    font, {"wght": weight}, inplace=False, updateFontNames=False
                )

            static.save(target)
            static.close()
            log(f"  built    {target.name}  (wght={weight} instance)")
            produced.append(target)
        except Exception as error:  # a bad source font must not stop the run
            log(f"  SKIPPED  {target.name}  ({type(error).__name__}: {error})")

    return produced


def build_collection(statics: list[Path], out_dir: Path, force: bool) -> Path | None:
    """Pack two static faces into one .ttc, for the collection-handling paths."""
    from fontTools.ttLib import TTCollection, TTFont

    if len(statics) < 2:
        return None

    target = out_dir / "TestCollection.ttc"
    if target.exists() and not force:
        log(f"  kept     {target.name}  (already present)")
        return target

    try:
        collection = TTCollection()
        collection.fonts = [TTFont(path) for path in statics[:2]]
        collection.save(str(target))
        for font in collection.fonts:
            font.close()
        names = ", ".join(path.name for path in statics[:2])
        log(f"  built    {target.name}  (2 faces: {names})")
        return target
    except Exception as error:
        log(f"  SKIPPED  {target.name}  ({type(error).__name__}: {error})")
        return None


def make_woff_pair(sfnt_path: Path, force: bool) -> list[Path]:
    """Write X.woff and X.woff2 beside X.ttf / X.otf, from the same original."""
    from fontTools.ttLib import TTFont

    produced: list[Path] = []
    for flavor in ("woff", "woff2"):
        target = sfnt_path.with_suffix(f".{flavor}")
        if target.exists() and not force:
            log(f"  kept     {target.name}  (already present)")
            produced.append(target)
            continue

        try:
            font = TTFont(sfnt_path)
            font.flavor = flavor
            font.save(target)
            font.close()
            ratio = target.stat().st_size / sfnt_path.stat().st_size
            log(f"  built    {target.name}  ({target.stat().st_size:,} bytes, {ratio:.0%} of source)")
            produced.append(target)
        except Exception as error:
            log(f"  SKIPPED  {target.name}  ({type(error).__name__}: {error})")

    return produced


def verify_round_trip(sfnt_path: Path) -> bool:
    """Confirm the triple really is one font in three containers."""
    from fontTools.ttLib import TTFont

    woff = sfnt_path.with_suffix(".woff")
    woff2 = sfnt_path.with_suffix(".woff2")
    if not (woff.exists() and woff2.exists()):
        return False

    try:
        fonts = [TTFont(path) for path in (sfnt_path, woff, woff2)]
        glyph_counts = {font.getGlyphOrder().__len__() for font in fonts}
        families = {font["name"].getDebugName(1) for font in fonts}
        for font in fonts:
            font.close()
        return len(glyph_counts) == 1 and len(families) == 1
    except Exception:
        return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT,
                        help="where to build the corpus (default: tests/fonts)")
    parser.add_argument("--no-fetch", action="store_true",
                        help="only derive and convert what is already there")
    parser.add_argument("--force", action="store_true",
                        help="rebuild files that already exist")
    args = parser.parse_args()

    try:
        import fontTools  # noqa: F401
    except ImportError:
        log("fonttools is not installed. See tools/README.md.")
        return 1

    out_dir: Path = args.out
    out_dir.mkdir(parents=True, exist_ok=True)

    if not args.no_fetch:
        log(f"Fetching source fonts into {out_dir}")
        for source in SOURCES:
            fetch(source, out_dir, args.force)
        for archive in ARCHIVES:
            fetch_archive(archive, out_dir, args.force)
        log("")

    log("Deriving static instances from variable fonts")
    statics: list[Path] = []
    for variable_path in sorted(out_dir.glob("*[[]*[]].ttf")):
        statics.extend(derive_statics(variable_path, args.force))
    if not statics:
        log("  (no variable fonts found)")
    log("")

    log("Building a TrueType Collection")
    if build_collection(statics, out_dir, args.force) is None:
        log("  (needs two static faces)")
    log("")

    log("Converting to WOFF 1.0 and WOFF 2.0")
    sfnt_files = sorted(
        path for path in out_dir.iterdir()
        if path.suffix.lower() in {".ttf", ".otf"}
    )
    for sfnt_path in sfnt_files:
        make_woff_pair(sfnt_path, args.force)
    log("")

    log("Verifying triples (same glyph count and family across .ttf/.otf, .woff, .woff2)")
    complete = 0
    for sfnt_path in sfnt_files:
        if verify_round_trip(sfnt_path):
            complete += 1
            log(f"  ok       {sfnt_path.stem}")
        else:
            log(f"  INCOMPLETE {sfnt_path.stem}")
    log("")

    by_extension: dict[str, int] = {}
    total_bytes = 0
    for path in out_dir.iterdir():
        # Skip the folder's own README and .gitkeep: they are not part of the corpus.
        if path.is_file() and path.suffix.lower() != ".md" and not path.name.startswith("."):
            by_extension[path.suffix.lower()] = by_extension.get(path.suffix.lower(), 0) + 1
            total_bytes += path.stat().st_size

    summary = ", ".join(f"{count} {ext}" for ext, count in sorted(by_extension.items()))
    log(f"Corpus: {summary}")
    log(f"Total:  {total_bytes / (1024 * 1024):.1f} MB in {out_dir}")
    log(f"Triples complete: {complete} of {len(sfnt_files)}")

    suffixes = {path.suffix.lower() for path in out_dir.iterdir() if path.is_file()}
    missing = []
    if ".pcf" not in suffixes:
        missing.append(".pcf (X11 binary bitmap)")
    if not {".fon", ".fnt"} & suffixes:
        missing.append(".fon/.fnt (or read C:\\Windows\\Fonts in place)")
    if ".dfont" not in suffixes:
        missing.append(".dfont (needs a macOS data-fork suitcase; see tests/fonts/README.md)")
    if not {".pfb", ".pfa"} & suffixes:
        missing.append(".pfb/.pfa (PostScript Type 1)")
    if missing:
        log("")
        log("Still to source by hand:")
        for item in missing:
            log(f"  - {item}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
