#!/bin/bash
# Runs a managed exe on KSP's own Unity Mono by embedding the game's libmonobdwgc in a small native host.
# Build the exe with KSP_MANAGED set (see build-mono.sh), or calls into overloads Unity lacks will fail.
# Usage: unity.sh <exe> [args]. Set KSP_APP if KSP.app is not in the default Steam location.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
app="${KSP_APP:-$HOME/Library/Application Support/Steam/steamapps/common/Kerbal Space Program/KSP.app}"
bin="$here/bin/unityhost"

if [ ! -x "$bin/unityhost" ]; then
    mkdir -p "$bin"
    cp "$app/Contents/Frameworks/libmonobdwgc-2.0.dylib" "$bin/"
    # A copy with an rpath id, re-signed ad hoc because changing the id invalidates the signature.
    install_name_tool -id @rpath/libmonobdwgc-2.0.dylib "$bin/libmonobdwgc-2.0.dylib" 2>/dev/null
    codesign -s - -f "$bin/libmonobdwgc-2.0.dylib" 2>/dev/null
    clang -arch x86_64 -O1 "$here/unityhost.c" -L"$bin" -lmonobdwgc-2.0 -Wl,-rpath,"$bin" -o "$bin/unityhost"
fi

UNITY_MANAGED="$app/Contents/Resources/Data/Managed" UNITY_ETC="$app/Contents/MonoBleedingEdge/etc" \
    exec "$bin/unityhost" "$@"
