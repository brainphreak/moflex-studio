#!/bin/sh
# Publish a Moflex Studio version: the clownsec.com update channel (what the app's Update button reads)
# and a GitHub release, both with the same Moflex.Studio.zip made from the current commit.
#   ./release.sh 1.4 notes.md
set -e
cd "$(dirname "$0")"
VER="$1"; NOTES="$2"
[ -n "$VER" ] && [ -f "$NOTES" ] || { echo "usage: ./release.sh VERSION NOTES.md"; exit 1; }
SERVER=brainphreak:/var/www/html/clownsec/studio
URL=https://www.clownsec.com/studio

SRC_VER=$(sed -n 's/.*const string AppVersion = "\([^"]*\)".*/\1/p' src/MoflexStudio.cs)
[ "$SRC_VER" = "$VER" ] || { echo "AppVersion in src/MoflexStudio.cs is $SRC_VER, not $VER"; exit 1; }
[ -z "$(git status --porcelain --untracked-files=no)" ] || { echo "commit first (the zip is made from HEAD)"; exit 1; }
git merge-base --is-ancestor HEAD origin/main 2>/dev/null || git push -q origin HEAD:main

OUT=$(mktemp -d)
git archive --format=zip --prefix="Moflex Studio/" -o "$OUT/Moflex.Studio.zip" HEAD
MD5=$(md5 -q "$OUT/Moflex.Studio.zip" 2>/dev/null || md5sum "$OUT/Moflex.Studio.zip" | cut -d' ' -f1)
printf '%s\n%s\n' "$VER" "$MD5" > "$OUT/latest.txt"
: > "$OUT/index.html"                                   # the folder is never listed

# zip first, latest.txt last: nobody is offered a version whose zip isn't there yet
rsync "$OUT/Moflex.Studio.zip" "$OUT/index.html" "$SERVER/"
GOT=$(curl -sL "$URL/Moflex.Studio.zip?v=$VER" | (md5 -q 2>/dev/null || md5sum | cut -d' ' -f1))
[ "$GOT" = "$MD5" ] || { echo "server zip md5 $GOT != $MD5 -- latest.txt NOT updated"; exit 1; }
rsync "$OUT/latest.txt" "$SERVER/"
echo "update channel: $(curl -sL "$URL/latest.txt?t=$(date +%s)" | tr '\n' ' ')"

gh release create "v$VER" "$OUT/Moflex.Studio.zip" --title "Moflex Studio $VER" --target main -F "$NOTES"
rm -rf "$OUT"
echo "released $VER"
