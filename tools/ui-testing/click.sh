#!/bin/bash
# Click at window-relative coords.  Usage: click.sh "<window title>" <x> <y>
export DISPLAY=:0
TITLE="${1:?window title}"; X="${2:?x}"; Y="${3:?y}"
WID=$(xdotool search --name "$TITLE" | head -1)
[ -z "$WID" ] && { echo "NO WINDOW matching: $TITLE"; exit 1; }
xdotool windowactivate "$WID" 2>/dev/null; sleep 0.3
xdotool mousemove --window "$WID" "$X" "$Y" click 1
echo "clicked $X,$Y in $WID"
