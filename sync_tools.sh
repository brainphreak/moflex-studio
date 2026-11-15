#!/bin/sh
# app/tools are copies of the shared helpers in the public moflex-encoder repo; refresh them from there.
set -e
src="${1:-$HOME/moflex-encoder/tools}"
for f in make_super_moflex.py moflex_addstreams.py ass_to_srt.py srt_clean.py; do cp "$src/$f" "$(dirname "$0")/app/tools/$f"; done
echo "synced from $src"
