// Shares this computer's screen with the phone app's "Computer Screen" demo, over the local Wi-Fi.
//
//   node Tools/screen-server/server.js      (or double-click start.bat)
//   then open http://localhost:8765 in Chrome on this computer and press Share.
//
// The page captures the screen (getDisplayMedia), encodes JPEG frames and posts them here. The phone
// finds this server by itself on the local network, long-polls /frame for the newest JPEG, and posts
// look-and-pinch clicks to /input, which a small PowerShell helper turns into real mouse input.
// Node's standard library only; nothing to install.

const http = require('http');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');

const PORT = 8765;
const SENDER_PAGE = fs.readFileSync(path.join(__dirname, 'sender.html'));

let frame = null;          // newest JPEG
let frameNo = 0;
let surface = { width: 0, height: 0, kind: 'monitor' };
let waiting = [];          // phones waiting for a frame newer than the one they have
let lastPhoneSeen = 0;
let phoneFrames = 0;

// Pairing: only phones approved on this computer's page see the screen or move the mouse.
const approved = new Set(['127.0.0.1']);
const pending = new Map();   // address -> last seen
function clientAddress(req) { return (req.socket.remoteAddress || '').replace(/^::ffff:/, ''); }
function isApproved(req) { return approved.has(clientAddress(req)); }

// ---------- mouse input (Windows) ----------

// mouse.ps1 reads one command per line and moves or clicks the real mouse.
let injector = null;
function sendInput(line) {
  if (process.platform !== 'win32') return;
  if (!injector || injector.exitCode !== null) {
    injector = spawn('powershell', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
      '-File', path.join(__dirname, 'mouse.ps1')], { stdio: ['pipe', 'ignore', 'inherit'] });
  }
  injector.stdin.write(line + '\n');
}

function handleInput(msg) {
  // Clicks only make sense where the shared picture is the whole screen.
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

// ---------- HTTP ----------

function body(req) {
  return new Promise((resolve) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => resolve(Buffer.concat(chunks)));
  });
}

function sendFrame(res) {
  res.writeHead(200, {
    'Content-Type': 'image/jpeg',
    'Content-Length': frame.length,
    'Cache-Control': 'no-store',
    'X-Frame': String(frameNo),
    'X-Surface': `${surface.width}x${surface.height}`,
  });
  res.end(frame);
  phoneFrames++;
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  switch (`${req.method} ${url.pathname}`) {
    case 'GET /':
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      return res.end(SENDER_PAGE);

    case 'GET /hello': {  // how the phone recognizes this server; asks for approval the first time
      const who = clientAddress(req);
      if (!approved.has(who)) pending.set(who, Date.now());
      res.writeHead(200, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify({ app: 'glasses-screen', approved: approved.has(who), sharing: frame !== null,
        width: surface.width, height: surface.height }));
    }

    case 'POST /approve': {  // from the sender page on this computer only
      if (clientAddress(req) !== '127.0.0.1') { res.writeHead(403); return res.end(); }
      const who = (await body(req)).toString().trim();
      approved.add(who);
      pending.delete(who);
      console.log(`Approved ${who}`);
      res.writeHead(204);
      return res.end();
    }

    case 'POST /push': {  // a frame from the sender page
      frame = await body(req);
      frameNo++;
      const w = parseInt(req.headers['x-width'] || '0', 10);
      const h = parseInt(req.headers['x-height'] || '0', 10);
      if (w && h) surface = { width: w, height: h, kind: req.headers['x-kind'] || 'monitor' };
      const ready = waiting;
      waiting = [];
      ready.forEach((r) => { clearTimeout(r.timer); sendFrame(r.res); });
      res.writeHead(204);
      return res.end();
    }

    case 'GET /frame': {  // the phone: newest frame, or wait up to a second for one newer than ?after=
      if (!isApproved(req)) { res.writeHead(403); return res.end(); }
      lastPhoneSeen = Date.now();
      const after = parseInt(url.searchParams.get('after') || '-1', 10);
      if (frame && frameNo > after) return sendFrame(res);
      const entry = { res, timer: null };
      entry.timer = setTimeout(() => {
        waiting = waiting.filter((e) => e !== entry);
        res.writeHead(204);
        res.end();
      }, 1000);
      waiting.push(entry);
      return;
    }

    case 'POST /input': {
      if (!isApproved(req)) { res.writeHead(403); return res.end(); }
      try { handleInput(JSON.parse((await body(req)).toString())); } catch (e) { /* ignore bad input */ }
      res.writeHead(204);
      return res.end();
    }

    case 'GET /status':  // for the sender page
      res.writeHead(200, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify({ phone: Date.now() - lastPhoneSeen < 3000, phoneFrames, addresses: addresses(),
        pending: [...pending].filter(([, t]) => Date.now() - t < 10000).map(([a]) => a) }));

    default:
      res.writeHead(404);
      return res.end();
  }
});

function addresses() {
  return Object.values(os.networkInterfaces()).flat()
    .filter((a) => a && a.family === 'IPv4' && !a.internal)
    .map((a) => `${a.address}:${PORT}`);
}

server.listen(PORT, '0.0.0.0', () => {
  console.log('\nGlasses screen server is running.');
  console.log(`Open http://localhost:${PORT} in Chrome on this computer and press Share.`);
  console.log('The phone finds it by itself on the same Wi-Fi. Addresses:', addresses().join(', '));
  console.log('If Windows asks about the firewall, allow private networks.\n');
});
