#!/bin/bash
# Capture the named window to a PNG.  Usage: shot.sh "<window title>" <out.png>
export DISPLAY=:0
TITLE="${1:?window title}"
OUT="${2:-/tmp/ux.png}"
WID=$(xdotool search --name "$TITLE" | head -1)
[ -z "$WID" ] && { echo "NO WINDOW matching: $TITLE"; exit 1; }
import -window "$WID" "$OUT" && echo "shot -> $OUT (window $WID)"
