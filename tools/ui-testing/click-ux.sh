#!/bin/bash
# Click at window-relative coords in the CleverPoint Migrator window.
# Usage: click-ux.sh <x> <y>
export DISPLAY=:0
X="${1:?x}"; Y="${2:?y}"
WID=$(xdotool search --name "CleverPoint Migrator" 2>/dev/null | tail -1)
[ -z "$WID" ] && { echo "NO WINDOW"; exit 1; }
xdotool windowactivate "$WID" 2>/dev/null; sleep 0.3
xdotool mousemove --window "$WID" "$X" "$Y" click 1
echo "clicked $X,$Y in $WID"
