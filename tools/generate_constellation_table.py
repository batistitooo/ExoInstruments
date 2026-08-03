#!/usr/bin/env python3
"""Generates Core/ConstellationTable.cs from the IAU constellation boundaries.

WHICH DATA. The boundaries are Delporte's (1930, "Delimitation Scientifique des Constellations",
Cambridge University Press), the ones the IAU adopted in 1928 and has not changed since. They are
drawn along lines of constant right ascension and constant declination in the mean equinox of
B1875, which is the whole reason a lookup needs a precession: the boundaries are rectangles only
in that frame, and in J2000 they are slanted curves.

The machine-readable form is Roman (1987, PASP 99, 695), VizieR VI/42, which rearranged Delporte's
boundary list so that a position can be resolved by a single ordered scan.

NAMES COME FROM THE IAU ITSELF, not from VizieR. VizieR's constellation page is the list everyone
copies (astropy's shipped constellation_names.dat cites it), and it carries three spellings the IAU
does not use: Chamaleon for Chamaeleon, Ophiucus for Ophiuchus, and Pisces Austrinus for Piscis
Austrinus. This generator reads the IAU's own table, which also carries the genitive of each name,
and uses VizieR only as an independent check that the same 88 abbreviations appear on both.

WHY GENERATED. 357 boundary records and 88 names is exactly the volume where a hand transcription
introduces one silent error that no other test would catch: a wrong declination on one record moves
a boundary by degrees and only shows up if someone happens to query that patch of sky.
tools/constellation-tests compares the C# lookup back against astropy's get_constellation over a
dense grid, so a transcription error here fails there.

Run:
    ./env/bin/python generate_constellation_table.py --out ../ExoInstruments/Core/ConstellationTable.cs
"""

import argparse
import html as html_module
import re
import urllib.request

BOUNDARY_URL = "https://cdsarc.cds.unistra.fr/ftp/VI/42/data.dat"
# The IAU's own table of the 88 constellations, with abbreviation, English meaning and genitive.
# Served from the ESO-hosted archive of iau.org's public pages; www.iau.org itself no longer
# resolves this path since its site was restructured.
NAMES_URL = "https://iauarchive.eso.org/public/themes/constellations/"
# Read only to confirm the same 88 abbreviations appear in the catalogue community's own list.
VIZIER_NAMES_URL = "https://vizier.cds.unistra.fr/vizier/VizieR/constellations.htx"

# What the boundary file must contain if it downloaded intact. Roman's own rearrangement is 357
# records; a truncated download is otherwise indistinguishable from a valid short file.
EXPECTED_RECORDS = 357
EXPECTED_CONSTELLATIONS = 88

# Roman's ReadMe prints these worked examples, at equinox 1950. They are checked here in B1875
# after the same precession the C# side uses, so a mangled download cannot pass silently.
# (RA hours, Dec degrees, equinox, expected abbreviation)
README_EXAMPLES = [
    (9.0000, 65.0000, 1950.0, "UMa"),
    (23.5000, -20.0000, 1950.0, "Aqr"),
    (5.1200, 9.1200, 1950.0, "Ori"),
    (9.4555, -19.9000, 1950.0, "Hya"),
    (12.8888, 22.0000, 1950.0, "Com"),
    (15.6687, -12.1234, 1950.0, "Lib"),
    (19.0000, -40.0000, 1950.0, "CrA"),
    (6.2222, -81.1234, 1950.0, "Men"),
]


def fetch(url):
    with urllib.request.urlopen(url, timeout=120) as response:
        return response.read().decode("utf-8", errors="replace")


def parse_boundaries(text):
    """The four fixed-width columns of VI/42 data.dat: RA_low, RA_up (hours), DE_low (deg), name."""
    records = []
    for line in text.splitlines():
        if not line.strip():
            continue
        parts = line.split()
        if len(parts) != 4:
            raise SystemExit(f"unreadable boundary record: {line!r}")
        ra_low, ra_up, dec_low, abbrev = parts
        records.append((float(ra_low), float(ra_up), float(dec_low), abbrev))
    return records


def _cell_text(cell):
    """Visible text of one table cell, with <br> and </p> treated as line breaks."""
    text = re.sub(r"<br\s*/?>", "\n", cell, flags=re.IGNORECASE)
    text = re.sub(r"</p\s*>", "\n", text, flags=re.IGNORECASE)
    text = re.sub(r"<[^>]+>", "", text)
    return [" ".join(line.split()) for line in html_module.unescape(text).splitlines() if line.strip()]


def parse_names(html):
    """Abbreviation -> (name, English meaning, genitive), from the IAU's own constellation table.

    Each row is name / abbreviation / meaning / genitive / chart links, and each name cell carries
    an <a name="abb"> anchor. The name and the genitive are each followed by a pronunciation guide,
    sometimes in a sibling paragraph and sometimes after a <br> in the same one; taking the text
    BEFORE the anchor for the name, and the first line of the genitive cell, handles both.
    """
    names = {}
    for row in re.findall(r"<tr>(.*?)</tr>", html, re.S | re.IGNORECASE):
        cells = re.findall(r"<td[^>]*>(.*?)</td>", row, re.S | re.IGNORECASE)
        if len(cells) < 4:
            continue
        anchor = re.search(r'<a\s+name="([a-z]{3})"\s*>', cells[0], re.IGNORECASE)
        if anchor is None:
            continue
        name_lines = _cell_text(cells[0].split("<a", 1)[0])
        genitive_lines = _cell_text(cells[3])
        meaning_lines = _cell_text(cells[2])
        if not name_lines or not genitive_lines or not meaning_lines:
            raise SystemExit(f"incomplete IAU constellation row for {anchor.group(1)}")
        abbrev = _cell_text(cells[1])[0]
        names[abbrev] = (name_lines[0], meaning_lines[0], genitive_lines[0])
    return names


def parse_vizier_abbreviations(html):
    """The abbreviations VizieR's own constellation page lists, used only as a cross-check."""
    pattern = re.compile(
        r'<A HREF="[^"]*Vgraph\?VI/42[^"]*"[^>]*>([A-Za-z]{3})</A>', re.IGNORECASE
    )
    return set(pattern.findall(html))


# --- The precession the boundaries need ------------------------------------------------------
# Reimplemented here only so the generator can verify Roman's own worked examples before writing
# the table; the shipped implementation is Core/BesselianFrames.cs and the two are compared in
# tools/constellation-tests.

def _rotation(angle_deg, axis):
    import numpy as np

    a = np.radians(angle_deg)
    c, s = np.cos(a), np.sin(a)
    if axis == "z":
        return np.array([[c, s, 0.0], [-s, c, 0.0], [0.0, 0.0, 1.0]])
    if axis == "y":
        return np.array([[c, 0.0, -s], [0.0, 1.0, 0.0], [s, 0.0, c]])
    raise ValueError(axis)


def newcomb_precession(epoch1, epoch2):
    """Newcomb's precession between two Besselian epochs (ESAA 1992 chapter 3; astropy's
    earth_orientation._precession_matrix_besselian implements the same expressions)."""
    import numpy as np

    t1 = (epoch1 - 1850.0) / 1000.0
    dt = (epoch2 - 1850.0) / 1000.0 - t1

    zeta = np.polyval((17.995, 30.240 - 0.27 * t1, 23035.545 + t1 * (139.720 + 0.060 * t1), 0), dt) / 3600.0
    z = np.polyval((18.325, 109.480 + 0.39 * t1, 23035.545 + t1 * (139.720 + 0.060 * t1), 0), dt) / 3600.0
    theta = np.polyval((-41.8, -42.65 - 0.37 * t1, 20051.12 - t1 * (85.29 + 0.37 * t1), 0), dt) / 3600.0

    return _rotation(-z, "z") @ _rotation(theta, "y") @ _rotation(-zeta, "z")


def to_b1875(ra_hours, dec_deg, equinox):
    import numpy as np

    m = newcomb_precession(equinox, 1875.0)
    ra = np.radians(ra_hours * 15.0)
    dec = np.radians(dec_deg)
    v = np.array([np.cos(dec) * np.cos(ra), np.cos(dec) * np.sin(ra), np.sin(dec)])
    w = m @ v
    ra_out = np.degrees(np.arctan2(w[1], w[0])) / 15.0
    if ra_out < 0.0:
        ra_out += 24.0
    return ra_out, np.degrees(np.arcsin(max(-1.0, min(1.0, w[2]))))


def find_constellation(records, ra_hours_1875, dec_deg_1875):
    """Roman's own ordered scan: the first record whose declination floor is at or below the
    position and whose right-ascension arc brackets it."""
    for ra_low, ra_up, dec_low, abbrev in records:
        if dec_low > dec_deg_1875:
            continue
        if ra_low <= ra_hours_1875 < ra_up:
            return abbrev
    return None


def check_examples(records):
    for ra_h, dec_d, equinox, expected in README_EXAMPLES:
        ra75, dec75 = to_b1875(ra_h, dec_d, equinox)
        got = find_constellation(records, ra75, dec75)
        if got != expected:
            raise SystemExit(
                f"boundary table failed Roman's own example: RA {ra_h} DEC {dec_d} at equinox "
                f"{equinox} gave {got}, the ReadMe says {expected}"
            )
    print(f"all {len(README_EXAMPLES)} worked examples from the VI/42 ReadMe reproduce")


def emit(records, names, out):
    used = sorted({abbrev for _, _, _, abbrev in records})
    out.write("// GENERATED by tools/generate_constellation_table.py; do not edit by hand.\n")
    out.write("//\n")
    out.write("// The IAU constellation boundaries, Delporte (1930) as rearranged for lookup by\n")
    out.write("// Roman (1987, PASP 99, 695) and distributed as VizieR VI/42, plus the IAU's official\n")
    out.write("// constellation names from VizieR's own constellation table.\n")
    out.write("//\n")
    out.write("// Right ascensions are HOURS and declinations DEGREES, both in the mean equinox of\n")
    out.write("// B1875, which is the frame Delporte drew the boundaries in. A position in any other\n")
    out.write("// frame has to be brought here first; see Core/BesselianFrames.cs.\n")
    out.write("//\n")
    out.write("// RECORD ORDER IS PART OF THE DATA. Roman sorted the arcs by declination floor and\n")
    out.write("// then by eastern terminus so that the first bracketing record encountered in a\n")
    out.write("// forward scan is the answer. Re-sorting this array breaks the lookup.\n")
    out.write("\n")
    out.write("namespace ExoInstruments.Core\n{\n")
    out.write("    internal static class ConstellationTable\n    {\n")

    out.write("        /// <summary>Western edge of each boundary arc, hours of right ascension, B1875.</summary>\n")
    out.write("        public static readonly double[] RaLowHours =\n        {\n")
    write_numbers(out, [r[0] for r in records])
    out.write("        };\n\n")

    out.write("        /// <summary>Eastern edge of each boundary arc, hours of right ascension, B1875.</summary>\n")
    out.write("        public static readonly double[] RaHighHours =\n        {\n")
    write_numbers(out, [r[1] for r in records])
    out.write("        };\n\n")

    out.write("        /// <summary>Southern edge of each boundary arc, degrees of declination, B1875.</summary>\n")
    out.write("        public static readonly double[] DecLowDeg =\n        {\n")
    write_numbers(out, [r[2] for r in records])
    out.write("        };\n\n")

    out.write("        /// <summary>Three-letter IAU abbreviation owning each arc, parallel to the arrays above.</summary>\n")
    out.write("        public static readonly string[] Abbreviations =\n        {\n")
    write_strings(out, [r[3] for r in records])
    out.write("        };\n\n")

    out.write("        /// <summary>The 88 abbreviations, alphabetical. AllNames, AllMeanings and AllGenitives run parallel to this.</summary>\n")
    out.write("        public static readonly string[] AllAbbreviations =\n        {\n")
    write_strings(out, used)
    out.write("        };\n\n")

    out.write("        /// <summary>The IAU's official constellation names.</summary>\n")
    out.write("        public static readonly string[] AllNames =\n        {\n")
    write_strings(out, [names[a][0] for a in used])
    out.write("        };\n\n")

    out.write("        /// <summary>What each Latin name means, as the IAU's own table glosses it (\"the Sea Monster\").</summary>\n")
    out.write("        public static readonly string[] AllMeanings =\n        {\n")
    write_strings(out, [names[a][1] for a in used])
    out.write("        };\n\n")

    out.write("        /// <summary>The genitive, which is the form that appears in Bayer and Flamsteed star names (\"Pegasi\" in 51 Pegasi).</summary>\n")
    out.write("        public static readonly string[] AllGenitives =\n        {\n")
    write_strings(out, [names[a][2] for a in used])
    out.write("        };\n")
    out.write("    }\n}\n")


def write_numbers(out, values):
    for i in range(0, len(values), 8):
        row = ", ".join(f"{v!r}" for v in values[i:i + 8])
        out.write(f"            {row},\n")


def write_strings(out, values):
    for i in range(0, len(values), 8):
        row = ", ".join('"' + v + '"' for v in values[i:i + 8])
        out.write(f"            {row},\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, help="path to write ConstellationTable.cs to")
    parser.add_argument("--boundaries", help="local copy of VI/42 data.dat instead of downloading")
    parser.add_argument("--names", help="local copy of the IAU constellation page instead of downloading")
    args = parser.parse_args()

    boundary_text = open(args.boundaries).read() if args.boundaries else fetch(BOUNDARY_URL)
    names_html = open(args.names).read() if args.names else fetch(NAMES_URL)

    records = parse_boundaries(boundary_text)
    if len(records) != EXPECTED_RECORDS:
        raise SystemExit(f"expected {EXPECTED_RECORDS} boundary records, read {len(records)}")

    names = parse_names(names_html)
    if len(names) != EXPECTED_CONSTELLATIONS:
        raise SystemExit(f"expected {EXPECTED_CONSTELLATIONS} constellation names, read {len(names)}")

    used = {abbrev for _, _, _, abbrev in records}
    missing = sorted(used - set(names))
    if missing:
        raise SystemExit(f"boundary file names constellations the IAU table has no entry for: {missing}")
    if len(used) != EXPECTED_CONSTELLATIONS:
        raise SystemExit(f"boundaries cover {len(used)} constellations, expected {EXPECTED_CONSTELLATIONS}")

    # Independent confirmation that the abbreviations agree with the catalogue community's own
    # list. Only the abbreviations: VizieR's names carry spellings the IAU does not use.
    try:
        vizier = parse_vizier_abbreviations(fetch(VIZIER_NAMES_URL))
    except OSError as error:
        print(f"warning: could not reach VizieR for the abbreviation cross-check ({error})")
    else:
        if vizier != used:
            raise SystemExit(
                "IAU and VizieR disagree on the constellation abbreviations: "
                f"IAU-only {sorted(used - vizier)}, VizieR-only {sorted(vizier - used)}"
            )
        print("abbreviations agree between the IAU's table and VizieR's")

    check_examples(records)

    with open(args.out, "w") as handle:
        emit(records, names, handle)
    print(f"wrote {args.out}: {len(records)} boundary arcs, {len(names)} names")


if __name__ == "__main__":
    main()
