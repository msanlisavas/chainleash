// Generate the 1200×630 Open Graph card (public/og-image.png) from the live
// dashboard screenshot: cover-cropped, dark gradient, wordmark + tagline.
// Run: `node tools/gen-og-image.mjs` (from frontend/dashboard).
import sharp from 'sharp';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..', '..');                    // repo root
const SHOT = join(root, 'assets', 'dashboard.png');
const OUT = join(root, 'frontend', 'dashboard', 'public', 'og-image.png');

const W = 1200, H = 630;

const shot = await sharp(SHOT)
  .resize(W, H, { fit: 'cover', position: 'top' })
  .toBuffer();

// Bottom gradient + text block. Fonts fall back to an installed monospace
// (matches the site's instrumentation voice closely enough for a link card).
const overlay = Buffer.from(`
<svg width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <linearGradient id="fade" x1="0" y1="0" x2="0" y2="1">
      <stop offset="35%" stop-color="#0a0c10" stop-opacity="0"/>
      <stop offset="78%" stop-color="#0a0c10" stop-opacity="0.9"/>
      <stop offset="100%" stop-color="#0a0c10" stop-opacity="0.98"/>
    </linearGradient>
  </defs>
  <rect width="${W}" height="${H}" fill="url(#fade)"/>
  <rect x="0" y="${H - 6}" width="${W}" height="6" fill="#e5484d"/>
  <text x="64" y="${H - 138}" font-family="'IBM Plex Mono', Consolas, monospace" font-size="54"
        font-weight="600" letter-spacing="10" fill="#e7ecf3">CHAINLEASH</text>
  <text x="64" y="${H - 88}" font-family="'IBM Plex Mono', Consolas, monospace" font-size="27"
        fill="#9aa6b8">The chain-enforced leash for autonomous staking agents.</text>
  <text x="64" y="${H - 44}" font-family="'IBM Plex Mono', Consolas, monospace" font-size="21"
        fill="#6b7686">Live on Casper 2.0 testnet · an agent that can rebalance, but cannot steal</text>
</svg>`);

await sharp(shot).composite([{ input: overlay }]).png().toFile(OUT);
console.log('wrote', OUT);
