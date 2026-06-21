#!/bin/bash
# Launch the CleverPoint Migrator Photino app under WSLg, forced onto XWayland
# (GDK_BACKEND=x11) so xdotool can see and drive it. Always exits 0; the app is
# detached and survives the shell. Inspect with check-ux.sh afterwards.
APP="${1:-/mnt/c/trash/SharePoint-Migrator/CleverPoint-Migrator/src/CleverPoint.Migrator.Ux/bin/Debug/net8.0/CleverPoint.Migrator.Ux}"
LOG="${2:-/tmp/ux-app.log}"
export DISPLAY=:0
unset WAYLAND_DISPLAY
export GDK_BACKEND=x11
export WEBKIT_DISABLE_COMPOSITING_MODE=1
pkill -f CleverPoint.Migrator.Ux 2>/dev/null
sleep 2
setsid "$APP" > "$LOG" 2>&1 < /dev/null &
disown
echo "launched (pid group detached); waiting for window…"
exit 0
