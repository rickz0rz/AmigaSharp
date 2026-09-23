#!/bin/sh
# Download the SingleStepTests 68000 test vectors to build/cpu-tests/68000/, and the official
# opcode map to build/cpu-tests/68000.official.json.
# The files are about 200 MB. The script skips each file that is already present.
set -eu

ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=$ROOT/build/cpu-tests/68000
BASE=https://raw.githubusercontent.com/SingleStepTests/680x0/main/68000/v1
API=https://api.github.com/repos/SingleStepTests/680x0/contents/68000/v1

mkdir -p "$OUT"
curl -sSfL "$API" | grep -o '"name": *"[^"]*\.json\.gz"' | sed 's/.*"\([^"]*\)"$/\1/' | while read -r name; do
    if [ ! -f "$OUT/$name" ]; then
        curl -sSfL -o "$OUT/$name.part" "$BASE/$name"
        mv "$OUT/$name.part" "$OUT/$name"
    fi
done
if [ ! -f "$ROOT/build/cpu-tests/68000.official.json" ]; then
    curl -sSfL -o "$ROOT/build/cpu-tests/68000.official.json" \
        https://raw.githubusercontent.com/SingleStepTests/680x0/main/map/68000.official.json
fi
echo "$(ls "$OUT" | wc -l | tr -d ' ') test files in build/cpu-tests/68000."
