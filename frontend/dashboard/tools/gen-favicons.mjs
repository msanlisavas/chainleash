// Regenerate the CHAINLEASH favicon / icon set from the master logo.
//
// The logo is silver-on-transparent, which would vanish on a light browser tab.
// So every icon composites the mark onto a dark graphite chip that reads on both
// light and dark tab bars. Run: `npm run favicons` (from frontend/dashboard).
import sharp from 'sharp';
import pngToIco from 'png-to-ico';
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..', '..');            // repo root
const LOGO = join(root, 'logo.png');
const OUT_DIRS = [join(root, 'frontend', 'dashboard', 'public'), join(root, 'assets', 'favicon')];

// A dark chip (rounded squircle for tab favicons, or full-bleed square for the
// platform icons that get masked/rounded by the OS).
const chipSvg = (size, radius) => Buffer.from(`
<svg width="${size}" height="${size}" viewBox="0 0 ${size} ${size}" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <radialGradient id="g" cx="50%" cy="36%" r="78%">
      <stop offset="0%" stop-color="#1b2130"/>
      <stop offset="100%" stop-color="#0a0c10"/>
    </radialGradient>
  </defs>
  <rect width="${size}" height="${size}" rx="${radius}" ry="${radius}" fill="url(#g)"/>
  ${radius > 0
    ? `<rect x="0.75" y="0.75" width="${size - 1.5}" height="${size - 1.5}" rx="${radius - 0.75}" ry="${radius - 0.75}" fill="none" stroke="rgba(154,166,184,0.18)" stroke-width="1.5"/>`
    : ''}
</svg>`);

// Trim the transparent margin once so padding is consistent across sizes.
const markMaster = await sharp(LOGO).trim({ threshold: 10 }).png().toBuffer();

async function icon(size, { radius = Math.round(size * 0.22), scale = 0.64 } = {}) {
  const inner = Math.round(size * scale);
  const mark = await sharp(markMaster)
    .resize(inner, inner, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
    .toBuffer();
  return sharp(chipSvg(size, radius))
    .composite([{ input: mark, gravity: 'center' }])
    .png()
    .toBuffer();
}

// Tab favicons: rounded squircle, transparent corners.
const png16 = await icon(16, { radius: 4, scale: 0.78 });
const png32 = await icon(32, { radius: 7, scale: 0.72 });
const png48 = await icon(48, { radius: 11, scale: 0.7 });
// Platform icons: full-bleed dark square, mark inside the maskable safe zone.
const apple = await icon(180, { radius: 0, scale: 0.6 });
const and192 = await icon(192, { radius: 0, scale: 0.6 });
const and512 = await icon(512, { radius: 0, scale: 0.6 });

const ico = await pngToIco([png16, png32, png48]);

const files = {
  'favicon-16x16.png': png16,
  'favicon-32x32.png': png32,
  'apple-touch-icon.png': apple,
  'android-chrome-192x192.png': and192,
  'android-chrome-512x512.png': and512,
  'favicon.ico': ico,
};

for (const dir of OUT_DIRS) {
  for (const [name, buf] of Object.entries(files)) writeFileSync(join(dir, name), buf);
  console.log('wrote', Object.keys(files).length, 'icons →', dir);
}
