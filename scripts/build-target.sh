#!/bin/sh
# Assemble the target program with vasm and check the result against the reference hash.
# The script writes the executable and the vasm listing to build/target/.
#
# Environment variables:
#   VASM           The vasm executable. The default is vasmm68k_mot from the PATH.
#   TARGET_SOURCE  The directory that contains asm/Prevue.asm. The default is ./target-source.
set -eu

ROOT=$(cd "$(dirname "$0")/.." && pwd)
VASM=${VASM:-vasmm68k_mot}
TARGET_SOURCE=${TARGET_SOURCE:-$ROOT/target-source}
OUT=$ROOT/build/target
EXPECTED=6bd4760d1cf0706297ef169461ed0d7b7f0b079110a78e34d89223499e7c2fa2

if ! command -v "$VASM" >/dev/null 2>&1; then
    echo "error: cannot find vasm. Set VASM to the path of vasmm68k_mot." >&2
    exit 1
fi
if [ ! -f "$TARGET_SOURCE/asm/Prevue.asm" ]; then
    echo "error: cannot find $TARGET_SOURCE/asm/Prevue.asm. Set TARGET_SOURCE." >&2
    exit 1
fi

mkdir -p "$OUT"
cd "$TARGET_SOURCE/asm"
# Keep the default vasm optimizations. The reference binary uses them.
"$VASM" -Fhunkexe -nosym -quiet -L "$OUT/ESQ.lst" -o "$OUT/ESQ" Prevue.asm >"$OUT/vasm.log" 2>&1 || {
    cat "$OUT/vasm.log" >&2
    exit 1
}

ACTUAL=$(shasum -a 256 "$OUT/ESQ" | cut -d' ' -f1)
if [ "$ACTUAL" != "$EXPECTED" ]; then
    echo "error: the SHA-256 of build/target/ESQ is $ACTUAL. The reference is $EXPECTED." >&2
    exit 1
fi
echo "build/target/ESQ matches the reference hash."
