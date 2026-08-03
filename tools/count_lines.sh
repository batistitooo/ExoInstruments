#!/usr/bin/env bash
# Compte toutes les lignes du dossier, sans exclusion.
#
#   ./tools/count_lines.sh              le dossier du mod
#   ./tools/count_lines.sh <chemin>     n'importe quel autre dossier

set -euo pipefail

DIR="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"

printf "Counting lines in %s ...\n" "$DIR"

# wc -l en parallèle sur des paquets de fichiers, puis somme des sous-totaux.
total=$(
  find "$DIR" -type f -print0 \
    | xargs -0 -n 500 -P 8 wc -l 2>/dev/null \
    | awk '$2 == "total" { next } { t += $1 } END { print t+0 }'
)

printf "There's %s lines in this folder.\n" "$total"
