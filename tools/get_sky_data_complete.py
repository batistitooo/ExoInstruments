#!/usr/bin/env python3
"""Installs the COMPLETE ExoInstruments sky data: every Gaia DR3 star, with everything else.

Installs into GameData/ExoInstruments/PluginData:
  GaiaStarCatalog.starcat                          25.29 GB, all 1.8 billion stars, downloaded
  GaiaStarCatalog.V13, V15, V17 and V19.starcat    8.08 GB of magnitude tiers, downloaded
  dust map, galaxy catalogue and galaxy images     478 MB, downloaded
  HalphaMap.emission and HalphaPatches.patchset    built on this computer
That is 33.85 GB downloaded from the release. After get_sky_data_compact.py it only adds the
25.29 GB main file.

The two H-alpha files are not in the release: their survey terms do not allow redistribution.
Once the downloads are done, setup_data.py builds them here. It installs numpy, scipy, astropy,
astropy-healpix and requests into a private virtualenv, downloads about 600 MB of survey data and
takes up to an hour. A failed build keeps the downloads.

    python3 GameData/ExoInstruments/tools/get_sky_data_complete.py
    py GameData\\ExoInstruments\\tools\\get_sky_data_complete.py     (Windows)

Every download is checked against the release's sha256, and files already in place are kept.
Rerun it to resume an interrupted download or build.
Needs ExoInstruments 0.5.0 or newer and Python 3.9 or newer.
"""

import sys

if sys.version_info < (3, 9):
    sys.exit("This needs Python 3.9 or newer.")

from sky_data_release import main

if __name__ == "__main__":
    sys.exit(main(variant="complete", description=__doc__))
