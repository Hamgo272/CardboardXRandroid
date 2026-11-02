// Shares this computer's screen with the phone app's "Computer Screen" demo, over the local Wi-Fi.
//
//   node Tools/screen-server/server.js      (or use start.bat / start-linux.sh)
//   then open http://localhost:8765 in Chrome/Chromium and press Share.
//
// The page captures the screen (getDisplayMedia), encodes JPEG frames and posts them here. The phone
// finds this server by itself on the local network, long-polls /frame for the newest JPEG, and posts
// look-and-pinch clicks to /input. Linux remote input requires xdotool and an X11 desktop session.
// Node's standard library only; nothing to install for screen capture/streaming.

const http = require('http');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');
const { spawnSync } = require('child_process');

const PORT = 8765;
const SENDER_PAGE = fs.readFileSync(path.join(__dirname, 'sender.html'));
let frame = null;
let frameNo = 0;
let surface = { width: 0, height: 0, kind: 'monitor' };
let waiting = [];
let lastPhoneSeen = 0;
let phoneFrames = 0;
const approved = new Set(['127.0.0.1']);
const pending = new Map();
function clientAddress(req) { return (req.socket.remoteAddress || '').replace(/^::ffff:/, ''); }
function isApproved(req) { return approved.has(clientAddress(req)); }

// ---------- mouse/keyboard input ----------
let injector = null;
let linuxDisplay = null;
function runXdotool(args) {
  const child = spawn('xdotool', args, { stdio: ['ignore', 'ignore', 'pipe'] });
  child.on('error', (e) => console.error('Linux input failed. Install xdotool and use an X11 session:', e.message));
}
function linuxGeometry() {
  if (linuxDisplay) return linuxDisplay;
  const result = spawnSync('xdotool', ['getdisplaygeometry'], { encoding: 'utf8' });
  if (result.status !== 0) {
    console.error('Linux mouse/keyboard control needs xdotool and an X11 session. Screen sharing itself can still work.');
    return null;
  }
  const [w, h] = result.stdout.trim().split(/\s+/).map(Number);
  if (!w || !h) return null;
  linuxDisplay = { width: w, height: h };
  return linuxDisplay;
}
function sendInput(line) {
  if (process.platform === 'win32') {
    if (!injector || injector.exitCode !== null) {
      injector = spawn('powershell', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', path.join(__dirname, 'mouse.ps1')], { stdio: ['pipe', 'ignore', 'inherit'] });
      injector.on('error', (e) => console.error('Could not start mouse.ps1:', e.message));
    }
    if (injector.stdin && !injector.stdin.destroyed) injector.stdin.write(line + '\n');
    return;
  }
  if (process.platform === 'linux') {
    const p = line.split(' ');
    const geometry = linuxGeometry();
    switch (p[0]) {
      case 'move': {
        if (!geometry) return;
        const x = Math.max(0, Math.min(geometry.width - 1, Math.round(Number(p[1]) * (geometry.width - 1))));
        const y = Math.max(0, Math.min(geometry.height - 1, Math.round(Number(p[2]) * (geometry.height - 1))));
        runXdotool(['mousemove', '--sync', String(x), String(y)]);
        break;
      }
      case 'down': runXdotool(['mousedown', '1']); break;
      case 'up': runXdotool(['mouseup', '1']); break;
      case 'wheel': {
        const delta = Number(p[1]) || 0;
        const button = delta > 0 ? '4' : '5';
        for (let i = 0; i < Math.min(12, Math.max(1, Math.round(Math.abs(delta) / 120))); i++)
          runXdotool(['click', button]);
        break;
      }
      case 'text': {
        try {
          const text = Buffer.from(p[1], 'base64').toString('utf8');
          runXdotool(['type', '--clearmodifiers', '--delay', '0', '--', text]);
        } catch (e) { console.error('Could not decode remote text:', e.message); }
        break;
      }
      case 'key':
        if (p[1] === 'enter') runXdotool(['key', 'Return']);
        else if (p[1] === 'back') runXdotool(['key', 'BackSpace']);
        break;
    }
  }
}

function handleInput(msg) {
  const x = Math.min(Math.max(+msg.x || 0, 0), 1);
  const y = Math.min(Math.max(+msg.y || 0, 0), 1);
  switch (msg.t) {
    case 'down': sendInput(`move ${x} ${y}`); sendInput('down'); break;
    case 'move': sendInput(`move ${x} ${y}`); break;
    case 'up': sendInput(`move ${x} ${y}`); sendInput('up'); break;
    case 'click': sendInput(`move ${x} ${y}`); sendInput('down'); sendInput('up'); break;
    case 'wheel': sendInput(`wheel ${Math.round(+msg.d || 0)}`); break;
    case 'text': if (/^[A-Za-z0-9+/=]+$/.test(msg.s || '')) sendInput(`text ${msg.s}`); break;
    case 'key': if (msg.k === 'enter' || msg.k === 'back') sendInput(`key ${msg.k}`); break;
  }
}

function body(req) {
  return new Promise((resolve) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => resolve(Buffer.concat(chunks)));
  });
}
function sendFrame(res) {
  res.writeHead(200, { 'Content-Type': 'image/jpeg', 'Content-Length': frame.length,
    'Cache-Control': 'no-store', 'X-Frame': String(frameNo),
    'X-Surface': `${surface.width}x${surface.height}` });
  res.end(frame);
  phoneFrames++;
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  switch (`${req.method} ${url.pathname}`) {
    case 'GET /':
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      return res.end(SENDER_PAGE);
    case 'GET /hello': {
      const who = clientAddress(req);
      if (!approved.has(who)) pending.set(who, Date.now());
      res.writeHead(200, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify({ app: 'glasses-screen', approved: approved.has(who), sharing: frame !== null,
        width: surface.width, height: surface.height }));
    }
    case 'POST /approve': {
      if (clientAddress(req) !== '127.0.0.1' && clientAddress(req) !== '::1') { res.writeHead(403); return res.end(); }
      const who = (await body(req)).toString().trim();
      approved.add(who); pending.delete(who);
      console.log(`Approved ${who}`);
      res.writeHead(204); return res.end();
    }
    case 'POST /push': {
      frame = await body(req); frameNo++;
      // sender.html encodes the scaled dimensions, matching the actual JPEG payload.
      const w = parseInt(req.headers['x-width'] || '0', 10);
      const h = parseInt(req.headers['x-height'] || '0', 10);
      if (w && h) surface = { width: w, height: h, kind: req.headers['x-kind'] || 'monitor' };
      const ready = waiting; waiting = [];
      ready.forEach((r) => { clearTimeout(r.timer); sendFrame(r.res); });
      res.writeHead(204); return res.end();
    }
    case 'GET /frame': {
      if (!isApproved(req)) { res.writeHead(403); return res.end(); }
      lastPhoneSeen = Date.now();
      const after = parseInt(url.searchParams.get('after') || '-1', 10);
      if (frame && frameNo > after) return sendFrame(res);
      const entry = { res, timer: null };
      entry.timer = setTimeout(() => {
        waiting = waiting.filter((e) => e !== entry);
        res.writeHead(204); res.end();
      }, 1000);
      waiting.push(entry); return;
    }
    case 'POST /input': {
      if (!isApproved(req)) { res.writeHead(403); return res.end(); }
      try { handleInput(JSON.parse((await body(req)).toString())); } catch (e) { /* ignore malformed input */ }
      res.writeHead(204); return res.end();
    }
    case 'GET /status':
      res.writeHead(200, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify({ phone: Date.now() - lastPhoneSeen < 3000, phoneFrames, addresses: addresses(),
        pending: [...pending].filter(([, t]) => Date.now() - t < 10000).map(([a]) => a) }));
    default:
      res.writeHead(404); return res.end();
  }
});
function addresses() {
  return Object.values(os.networkInterfaces()).flat()
    .filter((a) => a && a.family === 'IPv4' && !a.internal)
    .map((a) => `${a.address}:${PORT}`);
}
server.listen(PORT, '0.0.0.0', () => {
  console.log('\nGlasses screen server is running.');
  console.log(`Open http://localhost:${PORT} in Chrome/Chromium on this computer and press Share.`);
  console.log('The phone finds the server on the same Wi-Fi. Addresses:', addresses().join(', '));
  if (process.platform === 'linux') {
    if (process.env.XDG_SESSION_TYPE === 'wayland')
      console.log('Screen capture works in Chrome on Wayland; xdotool remote mouse control needs an X11 session.');
    else if (!linuxGeometry())
      console.log('Install xdotool for remote mouse and keyboard control.');
  }
  console.log('Allow port 8765 through the computer firewall on a private network.\n');
});
