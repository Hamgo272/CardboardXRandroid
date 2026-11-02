// Shares a Linux desktop with the phone over the local Wi-Fi.
// Install Node.js and (for remote mouse/keyboard control under X11) xdotool, then run:
//   bash Tools/screen-server/start.sh
// Open the printed local URL in Chrome/Chromium and press Share. Linux desktop capture is provided
// by the browser's getDisplayMedia API; select the entire screen for correct remote input mapping.
const { spawn } = require('child_process');
const path = require('path');
const { spawnSync } = require('child_process');
const node = process.env.NODE || 'node';
const server = path.join(__dirname, 'server.js');
const result = spawnSync(node, [server], { stdio: 'inherit' });
process.exit(result.status === null ? 1 : result.status);
