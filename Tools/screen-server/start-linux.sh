#!/usr/bin/env bash
# Starts the Linux screen-sharing server. Screen capture is done by Chrome/Chromium.
# For remote mouse/keyboard control under X11, install xdotool; Wayland generally blocks injection.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v node >/dev/null 2>&1; then
    echo "Node.js is required. Install it, then rerun this script." >&2
    exit 1
fi

if [[ "${XDG_SESSION_TYPE:-}" == "wayland" ]]; then
    echo "Note: Wayland supports browser screen capture, but remote mouse/keyboard control may require an X11 session." >&2
elif ! command -v xdotool >/dev/null 2>&1; then
    echo "Note: install xdotool to enable remote mouse/keyboard control (screen sharing still works)." >&2
fi

URL="http://localhost:8765"
echo "Starting CardboardXR screen server. Open ${URL} in Chrome or Chromium and press Share."
if [[ "${OPEN_BROWSER:-1}" == "1" ]] && command -v xdg-open >/dev/null 2>&1; then
    xdg-open "$URL" >/dev/null 2>&1 &
fi

exec node "$SCRIPT_DIR/server.js"
