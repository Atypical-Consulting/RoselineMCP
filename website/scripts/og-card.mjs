#!/usr/bin/env node
// Guards against #198: public/og.png is a rendered copy of og-card.html, and nothing tied the two
// together, so a corrected template could ship with a stale image (the card read 89% after the
// text surfaces had moved to 85%).
//
//   node scripts/og-card.mjs          render og-card.html and fail if public/og.png does not match
//   node scripts/og-card.mjs --write  re-render and overwrite public/og.png (npm run og:render)
//
// The comparison is perceptual, not byte equality: PNG encoders and text antialiasing differ by
// Chrome version and platform. Both images are averaged over BLOCK x BLOCK pixel blocks (which
// absorbs antialiasing noise but not a changed glyph), and the check fails when too many blocks
// differ by more than BLOCK_TOLERANCE on any channel. Chrome comes from $CHROME_BIN or the usual
// install locations. Needs `npm ci` first: the card loads its fonts from node_modules.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, copyFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { inflateSync } from 'node:zlib';

const WIDTH = 2400, HEIGHT = 1260; // must match og:image:width/height in src/layouts/Base.astro
const BLOCK = 8;                   // 4 CSS px at device-scale-factor 2
const BLOCK_TOLERANCE = 32;        // 0-255 mean drift a block may show (Windows vs macOS text rendering: 4 blocks at 24)
const MAX_DIFFERING_BLOCKS = 40;   // a changed stat number differs in ~800 blocks, so 20x headroom

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const card = join(root, 'og-card.html');
const committed = join(root, 'public', 'og.png');
const fix = 'cd website && npm ci && npm run og:render   (then commit public/og.png)';

function findChrome() {
  const candidates = [
    process.env.CHROME_BIN,
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/usr/bin/google-chrome', '/usr/bin/google-chrome-stable', '/usr/bin/chromium', '/usr/bin/chromium-browser',
  ].filter(Boolean);
  const found = candidates.find((c) => existsSync(c));
  if (!found) throw new Error('No Chrome found. Set CHROME_BIN to a Chrome/Chromium binary.');
  return found;
}

function render(out) {
  execFileSync(findChrome(), [
    '--headless', '--disable-gpu', '--hide-scrollbars', '--no-sandbox',
    `--screenshot=${out}`, '--window-size=1200,630', '--force-device-scale-factor=2',
    '--virtual-time-budget=10000', pathToFileURL(card).href,
  ], { stdio: 'ignore' });
}

// Minimal PNG decoder: 8-bit RGB/RGBA, non-interlaced (what Chrome and the committed card are).
function decode(file) {
  const b = readFileSync(file);
  let o = 8, w = 0, h = 0, type = 0;
  const idat = [];
  while (o < b.length) {
    const len = b.readUInt32BE(o), name = b.toString('latin1', o + 4, o + 8), d = b.subarray(o + 8, o + 8 + len);
    if (name === 'IHDR') {
      w = d.readUInt32BE(0); h = d.readUInt32BE(4); type = d[9];
      if (d[8] !== 8 || d[12] !== 0) throw new Error(`${file}: need an 8-bit non-interlaced PNG`);
    }
    if (name === 'IDAT') idat.push(d);
    o += 12 + len;
  }
  const bpp = type === 6 ? 4 : type === 2 ? 3 : 0;
  if (!bpp) throw new Error(`${file}: need an RGB or RGBA PNG`);
  const raw = inflateSync(Buffer.concat(idat)), stride = w * bpp, px = Buffer.alloc(h * stride);
  for (let y = 0; y < h; y++) {
    const f = raw[y * (stride + 1)], src = y * (stride + 1) + 1, dst = y * stride;
    for (let x = 0; x < stride; x++) {
      const a = x >= bpp ? px[dst + x - bpp] : 0;
      const up = y ? px[dst - stride + x] : 0;
      const c = x >= bpp && y ? px[dst - stride + x - bpp] : 0;
      const p = a + up - c, pa = Math.abs(p - a), pb = Math.abs(p - up), pc = Math.abs(p - c);
      const pred = f === 0 ? 0 : f === 1 ? a : f === 2 ? up : f === 3 ? (a + up) >> 1 : (pa <= pb && pa <= pc ? a : pb <= pc ? up : c);
      px[dst + x] = (raw[src + x] + pred) & 255;
    }
  }
  return { w, h, bpp, px };
}

function blockMeans({ w, h, bpp, px }) {
  const bw = Math.floor(w / BLOCK), bh = Math.floor(h / BLOCK), out = new Float32Array(bw * bh * 3);
  for (let by = 0; by < bh; by++) for (let bx = 0; bx < bw; bx++) {
    const s = [0, 0, 0];
    for (let y = 0; y < BLOCK; y++) for (let x = 0; x < BLOCK; x++) {
      const i = ((by * BLOCK + y) * w + bx * BLOCK + x) * bpp;
      s[0] += px[i]; s[1] += px[i + 1]; s[2] += px[i + 2];
    }
    for (let c = 0; c < 3; c++) out[(by * bw + bx) * 3 + c] = s[c] / (BLOCK * BLOCK);
  }
  return out;
}

const tmp = mkdtempSync(join(tmpdir(), 'og-card-'));
try {
  const fresh = join(tmp, 'og.png');
  render(fresh);
  const a = decode(fresh);
  if (a.w !== WIDTH || a.h !== HEIGHT) throw new Error(`render is ${a.w}x${a.h}, expected ${WIDTH}x${HEIGHT} (device-scale-factor drifted?)`);
  if (process.argv.includes('--write')) {
    copyFileSync(fresh, committed);
    console.log(`og-card: wrote ${committed}`);
  } else {
    const b = decode(committed);
    if (b.w !== WIDTH || b.h !== HEIGHT) throw new Error(`public/og.png is ${b.w}x${b.h}, expected ${WIDTH}x${HEIGHT}`);
    const ma = blockMeans(a), mb = blockMeans(b);
    let bad = 0;
    for (let i = 0; i < ma.length; i += 3) {
      if (Math.max(Math.abs(ma[i] - mb[i]), Math.abs(ma[i + 1] - mb[i + 1]), Math.abs(ma[i + 2] - mb[i + 2])) > BLOCK_TOLERANCE) bad++;
    }
    console.log(`og-card: ${bad} blocks differ (limit ${MAX_DIFFERING_BLOCKS})`);
    if (bad > MAX_DIFFERING_BLOCKS) {
      console.error(`og-card: public/og.png is stale against og-card.html. Re-render it with:\n  ${fix}`);
      process.exitCode = 1;
    }
  }
} catch (e) {
  console.error(`og-card: ${e.message}`);
  process.exitCode = 1;
} finally {
  rmSync(tmp, { recursive: true, force: true });
}
