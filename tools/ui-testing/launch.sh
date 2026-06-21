#!/bin/bash
# Launch a GUI app detached under WSLg so it survives the shell call, then report its PID.
# Usage: launch.sh <path-to-executable> [log-file]
export DISPLAY=:0
APP="${1:?path to executable}"
LOG="${2:-/tmp/ux-app.log}"
APPNAME="$(basename "$APP")"
pkill -f "$APP" 2>/dev/null
sleep 1
setsid "$APP" > "$LOG" 2>&1 < /dev/null &
disown
sleep 6   # give the WebView/UI time to render before first screenshot
PID=$(pgrep -f "$APP" | head -1)   # NOTE: $! is unreliable after setsid; use pgrep
if [ -n "$PID" ]; then echo "running: $APPNAME (pid $PID), log: $LOG"; else echo "FAILED to start; see $LOG"; tail -5 "$LOG"; fi
