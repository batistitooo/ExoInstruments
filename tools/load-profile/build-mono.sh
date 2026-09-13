#!/bin/bash
# Builds the harness for Mono, the runtime KSP runs, as C# 7.3 like the mod.
# Usage: build-mono.sh [core dir] [output dir]
# Set KSP_MANAGED to KSP's Resources/Data/Managed to compile against KSP's own mscorlib, so the exe
# also runs on the game's Unity Mono (Mono 6.12's mscorlib has overloads Unity's lacks).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
core="${1:-$here/../../ExoInstruments/Core}"
out="${2:-$here/bin/mono}"
mkdir -p "$out"

files=(Healpix Float16 ParallelWork EmissionMap EmissionPatchSet GalacticCoordinates SexagesimalCoordinates
       StarTarget StarNames StellarColor Colorimetry CieColourMatchingTable SpectralCurve
       ExoplanetCSVLoader BackgroundStarCatalogLoader StarCatalogMerger CatalogDensityThinner)
sources=("$here/LoadProfile.cs")
for f in "${files[@]}"; do sources+=("$core/$f.cs"); done

refs=()
if [ -n "${KSP_MANAGED:-}" ]; then
    refs=(-nostdlib -r:"$KSP_MANAGED/mscorlib.dll" -r:"$KSP_MANAGED/System.dll" -r:"$KSP_MANAGED/System.Core.dll")
fi

csc -nologo -langversion:7.3 -optimize+ ${refs[@]+"${refs[@]}"} -out:"$out/loadprof.exe" "${sources[@]}"
echo "built $out/loadprof.exe from $core"
