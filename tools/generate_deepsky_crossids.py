#!/usr/bin/env python3
"""Generates Core/DeepSkyCrossIdTable.cs from SIMBAD.

THE PROBLEM IT SOLVES. The installed catalogues designate objects the way their compilers did.
HyperLEDA calls the Andromeda Galaxy NGC0224. The shipped nebula list calls the Orion Nebula
NGC 1976. An observer calls them M31 and M42, and if the search box does not know that, the two
best-known objects in the northern sky are unreachable by the only names anyone uses.

Cross-identification is not something to reason out: it is a measured statement that two
designations refer to one object, and the authority for it is SIMBAD (Wenger et al. 2000, A&AS 143,
9), which maintains it from the literature. So this reads SIMBAD rather than encoding anyone's
memory of which NGC number M81 is.

WHAT IS PULLED. Two sets, and their union:

  * every object carrying a Messier identifier, 110 of them by construction;
  * every object carrying BOTH a common name and an NGC or IC number, which is the set an observer
    would search for by name (the Ring, the Sombrero, the Jellyfish).

For each, its M / NGC / IC designations, its common names, its position, its SIMBAD object type,
its angular size and its V magnitude. The position and type matter beyond aliasing: a Messier
object that no installed catalogue carries (the globular clusters, the open clusters) becomes a
pointable target on its own, rather than a name that resolves to nothing.

The object-type vocabulary is SIMBAD's own, shipped alongside so the interface can say "Globular
Cluster" instead of "GlC" without a hand-written translation table.

Run:
    ./env/bin/python generate_deepsky_crossids.py --out ../ExoInstruments/Core/DeepSkyCrossIdTable.cs
"""

import argparse
import collections
import csv
import io
import re
import urllib.parse
import urllib.request

TAP_URL = "https://simbad.cds.unistra.fr/simbad/sim-tap/sync"

# The two sets, as WHERE clauses on basic.oid. Kept as separate queries and merged in Python
# rather than combined with UNION inside an IN(), which SIMBAD's ADQL parser rejects.
#
#   1. every object carrying a Messier number (SIMBAD writes them padded, "M  31");
#   2. every object carrying both a common name and an NGC or IC number.
SELECTORS = {
    "Messier": "b.oid IN (SELECT oidref FROM ident WHERE id LIKE 'M %')",
    "named NGC/IC": (
        "b.oid IN (SELECT oidref FROM ident WHERE id LIKE 'NAME %') "
        "AND b.oid IN (SELECT oidref FROM ident WHERE id LIKE 'NGC %' OR id LIKE 'IC %')"
    ),
}

# Which identifiers are worth carrying. Every object in SIMBAD has dozens (2MASX, IRAS, LEDA,
# GALEXASC...) and none of them is a name anyone types into a search box.
#
# The catalogue designations are matched STRICTLY, as a number with at most a single-letter suffix.
# SIMBAD also numbers individual stars inside clusters and nebulae in the same namespace ("NGC
# 1976 721" is a star in the Orion Nebula, not a nebula), and a loose prefix match drags several
# hundred of those in as if they were deep-sky objects.
CATALOGUE_IDENTIFIER = re.compile(r"^(M|NGC|IC) (\d+)([A-Za-z])?$")
COMMON_NAME = re.compile(r"^NAME ")


def is_kept_identifier(identifier):
    return bool(CATALOGUE_IDENTIFIER.match(identifier) or COMMON_NAME.match(identifier))

EXPECTED_MESSIER = 110


def tap_query(adql):
    body = urllib.parse.urlencode(
        {"REQUEST": "doQuery", "LANG": "ADQL", "FORMAT": "csv", "QUERY": adql}
    ).encode()
    with urllib.request.urlopen(TAP_URL, data=body, timeout=600) as response:
        text = response.read().decode("utf-8")
    if text.lstrip().startswith("<"):
        raise SystemExit("SIMBAD returned an error rather than a table:\n" + text[:2000])
    return list(csv.DictReader(io.StringIO(text)))


def collapse(text):
    return " ".join(text.split())


def fetch_objects(where):
    rows = tap_query(
        "SELECT b.main_id AS mainid, b.ra AS raj, b.dec AS decj, b.otype_txt AS otype, "
        "b.galdim_majaxis AS majaxis, b.galdim_minaxis AS minaxis, f.V AS vmag "
        "FROM basic b LEFT JOIN allfluxes f ON b.oid = f.oidref "
        f"WHERE {where}"
    )
    return {collapse(r["mainid"]): r for r in rows}


def fetch_identifiers(where, into=None):
    rows = tap_query(
        "SELECT b.main_id AS mainid, i.id AS ident "
        "FROM basic b JOIN ident i ON b.oid = i.oidref "
        f"WHERE {where}"
    )
    grouped = into if into is not None else collections.defaultdict(list)
    for row in rows:
        identifier = collapse(row["ident"])
        if is_kept_identifier(identifier):
            grouped[collapse(row["mainid"])].append(identifier)
    return grouped


def messier_number(identifier):
    match = re.match(r"^M (\d+)$", identifier)
    return int(match.group(1)) if match else None


def order_identifiers(identifiers):
    """Messier first, then NGC, then IC, then common names; each group alphabetically by number.

    Not cosmetic: the first identifier becomes the entry's displayed designation, and M 31 is what
    the object is called.
    """
    def key(identifier):
        upper = identifier.upper()
        if upper.startswith("M "):
            return (0, messier_number(identifier) or 0, identifier)
        if upper.startswith("NGC "):
            return (1, int(re.sub(r"\D", "", identifier) or 0), identifier)
        if upper.startswith("IC "):
            return (2, int(re.sub(r"\D", "", identifier) or 0), identifier)
        return (3, 0, identifier)

    return sorted(set(identifiers), key=key)


def as_double(value):
    return float("nan") if value in (None, "", "NULL") else float(value)


def emit(objects, otypes, out):
    out.write("// GENERATED by tools/generate_deepsky_crossids.py; do not edit by hand.\n")
    out.write("//\n")
    out.write("// Cross-identifications and common names for the deep-sky objects an observer searches\n")
    out.write("// for by name, from SIMBAD (Wenger et al. 2000, A&AS 143, 9): every Messier object, and\n")
    out.write("// every NGC or IC object that carries a common name.\n")
    out.write("//\n")
    out.write("// Arrays run parallel. Identifiers[i] holds every M / NGC / IC designation and every\n")
    out.write("// common name SIMBAD records for the object, Messier number first; the first entry is\n")
    out.write("// what the object should be called. NaN means SIMBAD carries no such measurement.\n")
    out.write("\n")
    out.write("namespace ExoInstruments.Core\n{\n")
    out.write("    internal static class DeepSkyCrossIdTable\n    {\n")
    out.write(f"        public const int Count = {len(objects)};\n\n")

    out.write("        /// <summary>Every searchable designation and common name of each object, best designation first.</summary>\n")
    out.write("        public static readonly string[][] Identifiers =\n        {\n")
    for entry in objects:
        joined = ", ".join('"' + i.replace('"', '\\"') + '"' for i in entry["identifiers"])
        out.write(f"            new[] {{ {joined} }},\n")
    out.write("        };\n\n")

    write_doubles(out, "RaDeg", "Right ascension, J2000 degrees.", [e["ra"] for e in objects])
    write_doubles(out, "DecDeg", "Declination, J2000 degrees.", [e["dec"] for e in objects])
    write_doubles(out, "MajorArcmin", "Major axis of the apparent extent, arcminutes.", [e["major"] for e in objects])
    write_doubles(out, "MinorArcmin", "Minor axis of the apparent extent, arcminutes.", [e["minor"] for e in objects])
    write_doubles(out, "VMags", "Johnson V magnitude where SIMBAD has one.", [e["vmag"] for e in objects])

    write_strings(out, "ObjectTypes", "SIMBAD object type code, e.g. \"GlC\".", [e["otype"] for e in objects])

    used = sorted({e["otype"] for e in objects})
    write_strings(out, "TypeCodes", "The object-type codes appearing above, alphabetical.", used)
    write_strings(out, "TypeDescriptions", "SIMBAD's own description of each code, parallel to TypeCodes.",
                  [otypes[code] for code in used], last=True)

    out.write("    }\n}\n")


def write_strings(out, name, doc, values, last=False):
    out.write(f"        /// <summary>{doc}</summary>\n")
    out.write(f"        public static readonly string[] {name} =\n        {{\n")
    for i in range(0, len(values), 6):
        row = ", ".join('"' + v.replace("\\", "\\\\").replace('"', '\\"') + '"' for v in values[i:i + 6])
        out.write(f"            {row},\n")
    out.write("        };\n" + ("" if last else "\n"))


def write_doubles(out, name, doc, values):
    out.write(f"        /// <summary>{doc}</summary>\n")
    out.write(f"        public static readonly double[] {name} =\n        {{\n")
    for i in range(0, len(values), 6):
        row = ", ".join("double.NaN" if v != v else repr(v) for v in values[i:i + 6])
        out.write(f"            {row},\n")
    out.write("        };\n\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, help="path to write DeepSkyCrossIdTable.cs to")
    args = parser.parse_args()

    basics = {}
    identifiers = collections.defaultdict(list)
    for label, where in SELECTORS.items():
        print(f"querying SIMBAD for the {label} set ...")
        found = fetch_objects(where)
        basics.update(found)
        fetch_identifiers(where, into=identifiers)
        print(f"  {len(found)} objects")

    objects = []
    skipped = 0
    for main_id, row in sorted(basics.items()):
        ids = order_identifiers(identifiers.get(main_id, []))
        # An object with no canonical catalogue designation is not a deep-sky object that anyone
        # searches for; it is a star or a transient that happens to be numbered inside one. Those
        # are already reachable through the star catalogue and the sky chart.
        if not any(CATALOGUE_IDENTIFIER.match(i) for i in ids):
            skipped += 1
            continue
        objects.append({
            "identifiers": ids,
            "ra": as_double(row["raj"]),
            "dec": as_double(row["decj"]),
            "otype": collapse(row["otype"]),
            "major": as_double(row["majaxis"]),
            "minor": as_double(row["minaxis"]),
            "vmag": as_double(row["vmag"]),
        })

    messier = {messier_number(i) for entry in objects for i in entry["identifiers"]
               if messier_number(i) is not None}
    missing = sorted(set(range(1, EXPECTED_MESSIER + 1)) - messier)
    if missing:
        raise SystemExit(f"Messier objects missing from the pull: {missing}")

    for entry in objects:
        if entry["ra"] != entry["ra"] or entry["dec"] != entry["dec"]:
            raise SystemExit(f"{entry['identifiers'][0]} has no position; it cannot be a target")

    otypes = {collapse(r["ot"]): collapse(r["descr"]) for r in tap_query(
        "SELECT otype AS ot, description AS descr FROM otypedef")}
    unknown = sorted({e["otype"] for e in objects} - set(otypes))
    if unknown:
        raise SystemExit(f"object types with no SIMBAD description: {unknown}")

    with open(args.out, "w", encoding="utf-8") as handle:
        emit(objects, otypes, handle)
    print(f"wrote {args.out}: {len(objects)} objects, all {EXPECTED_MESSIER} Messier numbers "
          f"present; {skipped} rows dropped as stars or transients numbered inside one")


if __name__ == "__main__":
    main()
