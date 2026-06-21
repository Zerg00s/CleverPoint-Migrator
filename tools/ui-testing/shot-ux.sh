#!/bin/bash
# Screenshot the running CleverPoint Migrator Photino window (XWayland) to a PNG.
# Sizes the window to a viewport-fitting height so the shot has no dead space.
# Usage: shot-ux.sh <out.png> [height]
export DISPLAY=:0
OUT="${1:-/tmp/ux.png}"
H="${2:-545}"
WID=$(xdotool search --name "CleverPoint Migrator" 2>/dev/null | tail -1)
[ -z "$WID" ] && { echo "NO WINDOW (is it launched under GDK_BACKEND=x11?)"; exit 1; }
xdotool windowactivate "$WID" 2>/dev/null
xdotool windowsize "$WID" 1320 "$H" 2>/dev/null
sleep 2
import -window "$WID" "$OUT" && echo "shot -> $OUT (window $WID, ${H}px)"
