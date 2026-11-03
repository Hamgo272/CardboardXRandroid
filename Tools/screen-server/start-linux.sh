#!/usr/bin/env bash
# Starts the Linux screen-sharing server. Screen capture is done by Chrome/Chromium.
# For remote mouse/keyboard control under X11, install xdotool; Wayland generally blocks injection.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PORT=8765
URL="http://localhost:${PORT}"

if ! command -v node >/dev/null 2>&1; then
    echo "Node.js is required. Install it, then rerun this script." >&2
    exit 1
fi

if [[ "${XDG_SESSION_TYPE:-}" == "wayland" ]]; then
    echo "Note: Wayland supports browser screen capture, but remote mouse/keyboard control may require an X11 session." >&2
elif ! command -v xdotool >/dev/null 2>&1; then
    echo "Note: install xdotool to enable remote mouse/keyboard control (screen sharing still works)." >&2
fi

open_page() {
    if [[ "${OPEN_BROWSER:-1}" == "1" ]] && command -v xdg-open >/dev/null 2>&1; then
        xdg-open "$URL" >/dev/null 2>&1 &
    fi
}

# Reuse an existing CardboardXR server instead of starting a second listener.
if node - "$PORT" <<'NODE'
const http = require('http');
const port = Number(process.argv[2]);
const req = http.get({ hostname: '127.0.0.1', port, path: '/status', timeout: 900 }, res => {
  let body = '';
  res.setEncoding('utf8');
  res.on('data', chunk => body += chunk);
  res.on('end', () => {
    try {
      const status = JSON.parse(body);
      process.exit(res.statusCode === 200 &&
        Array.isArray(status.addresses) && Array.isArray(status.pending) ? 0 : 1);
    } catch (_) { process.exit(1); }
  });
});
req.on('timeout', () => { req.destroy(); process.exit(1); });
req.on('error', () => process.exit(1));
NODE
then
    echo "CardboardXR screen server is already running at ${URL}."
    echo "Reuse that server; do not start another copy."
    open_page
    exit 0
fi

# If another app owns the port, explain that rather than triggering an unhandled Node error.
if node - "$PORT" <<'NODE'
const net = require('net');
const socket = net.connect(Number(process.argv[2]), '127.0.0.1');
socket.setTimeout(700);
socket.once('connect', () => { socket.destroy(); process.exit(0); });
socket.once('error', () => process.exit(1));
socket.once('timeout', () => { socket.destroy(); process.exit(1); });
NODE
then
    echo "Port ${PORT} is already in use by another application." >&2
    echo "Stop that application or choose a different port before starting CardboardXR." >&2
    exit 1
fi

echo "Starting CardboardXR screen server. Open ${URL} in Chrome or Chromium and press Share."
open_page
exec node "$SCRIPT_DIR/server.js"
