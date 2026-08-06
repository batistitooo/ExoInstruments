"""Cross-validate Core/FitsImageReader.cs against astropy.io.fits.

    ../env/bin/python compare_astropy.py

Run the C# harness first: it writes the case_*.fits files and expected.csv, which holds whatever
the C# reader decoded from each of them.

WHY THIS EXISTS SEPARATELY FROM THE C# CHECKS. The C# harness writes the FITS files itself and
reads them back, so a writer and a reader that share a misunderstanding of the format agree with
each other perfectly and the round-trip proves nothing about the format. astropy.io.fits is the
reference implementation the whole field reads FITS with; agreement with it is evidence about the
standard rather than about internal consistency. This is the same argument tools/poppy-crossvalidation
and tools/galsim-crossvalidation make for the optics.

Exit status is 0 when every file agrees exactly.
"""

import csv
import os
import sys
from collections import defaultdict

import numpy as np
from astropy.io import fits

HERE = os.path.dirname(os.path.abspath(__file__))


def load_expected(path):
    """index -> value, per file, as the C# reader decoded it."""
    per_file = defaultdict(dict)
    with open(path, newline="") as handle:
        for row in csv.DictReader(handle):
            per_file[row["file"]][int(row["index"])] = float(row["value"])
    return per_file


def main():
    expected_path = os.path.join(HERE, "expected.csv")
    if not os.path.exists(expected_path):
        print("expected.csv is missing. Run the C# harness first:")
        print("    dotnet run -p:Core=../../ExoInstruments/Core -- --out .")
        return 1

    expected = load_expected(expected_path)
    if not expected:
        print("expected.csv is empty.")
        return 1

    failures = 0
    print()
    import astropy
    print("Core/FitsImageReader.cs against astropy.io.fits (astropy %s)" % astropy.__version__)
    print()
    print("  %-24s %8s %10s  %s" % ("file", "pixels", "worst", "verdict"))

    for name in sorted(expected):
        path = os.path.join(HERE, name)
        if not os.path.exists(path):
            print("  %-24s %8s %10s  MISSING" % (name, "-", "-"))
            failures += 1
            continue

        with fits.open(path) as hdul:
            # scale_back=False and the default do_not_scale_image_data=False mean astropy applies
            # BZERO/BSCALE for us, which is exactly the quantity being compared.
            data = np.asarray(hdul[0].data, dtype=np.float64).ravel()

        mine = np.array([expected[name][i] for i in range(len(expected[name]))], dtype=np.float64)

        if data.size != mine.size:
            print("  %-24s %8d %10s  SHAPE  astropy has %d" % (name, mine.size, "-", data.size))
            failures += 1
            continue

        # NaN has to match as NaN on both sides rather than compare unequal: BLANK and a float NaN
        # are both "undefined" and the point is that neither becomes a number.
        both_nan = np.isnan(data) & np.isnan(mine)
        one_nan = np.isnan(data) ^ np.isnan(mine)

        if one_nan.any():
            print("  %-24s %8d %10s  NAN    %d pixel(s) undefined on one side only"
                  % (name, mine.size, "-", int(one_nan.sum())))
            failures += 1
            continue

        finite = ~both_nan
        worst = float(np.max(np.abs(data[finite] - mine[finite]))) if finite.any() else 0.0
        ok = worst == 0.0
        if not ok:
            failures += 1
        print("  %-24s %8d %10.3g  %s" % (name, mine.size, worst, "exact" if ok else "DIFFERS"))

    print()
    if failures:
        print("%d FILE(S) DISAGREE WITH ASTROPY" % failures)
        return 1

    print("EVERY FILE AGREES WITH ASTROPY EXACTLY")
    return 0


if __name__ == "__main__":
    sys.exit(main())
