// Generates deterministic Windows icon assets from the selected source mark.
// Run with NODE_PATH pointing at the bundled workspace node_modules (sharp).
const fs = require('fs');
const path = require('path');
const sharp = require('sharp');

const root = path.resolve(__dirname, '..');
const assetDir = path.join(root, 'apps', 'PopGlot.Windows', 'Assets');
const sourcePath = path.join(assetDir, 'popglot-mark-selected-source.png');
const png = path.join(assetDir, 'popglot-app-avatar-v3.png');
const ico = path.join(assetDir, 'PopGlot-v3.ico');
const sizes = [16, 24, 32, 48, 64, 128, 256];

function icoEntry(size, bytes, offset) {
  const entry = Buffer.alloc(16);
  entry.writeUInt8(size === 256 ? 0 : size, 0);
  entry.writeUInt8(size === 256 ? 0 : size, 1);
  entry.writeUInt8(0, 2);
  entry.writeUInt8(0, 3);
  entry.writeUInt16LE(1, 4);
  entry.writeUInt16LE(32, 6);
  entry.writeUInt32LE(bytes.length, 8);
  entry.writeUInt32LE(offset, 12);
  return entry;
}

(async () => {
  const source = fs.readFileSync(sourcePath);
  const decoded = await sharp(source).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  // The generated source contains two opaque near-black guide strokes that
  // disappear on its original black preview but become visible on a brand
  // plate. Remove only near-black pixels; the white/cyan mark is unchanged.
  for (let i = 0; i < decoded.data.length; i += 4) {
    if (decoded.data[i] < 24 && decoded.data[i + 1] < 24 && decoded.data[i + 2] < 24) {
      decoded.data[i + 3] = 0;
    }
  }
  const cleanedSource = await sharp(decoded.data, {
    raw: { width: decoded.info.width, height: decoded.info.height, channels: 4 }
  }).png().toBuffer();
  const mark = await sharp(cleanedSource)
    .trim({ background: { r: 0, g: 0, b: 0, alpha: 0 }, threshold: 10 })
    .resize(390, 390, { fit: 'contain' })
    .png()
    .toBuffer();
  // The chosen mark contains a white upper surface. A stable full-bleed
  // indigo plate keeps it visible in Windows light surfaces and prevents
  // transparent corners from being rendered as black by legacy icon hosts.
  const prepared = await sharp({
    create: { width: 512, height: 512, channels: 4, background: '#5B5BD6' }
  })
    .composite([{ input: mark, gravity: 'centre' }])
    .png()
    .toBuffer();
  await sharp(prepared).toFile(png);
  const images = await Promise.all(sizes.map(size => sharp(prepared).resize(size, size).png().toBuffer()));
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);
  let offset = header.length + images.length * 16;
  const entries = images.map((bytes, index) => {
    const entry = icoEntry(sizes[index], bytes, offset);
    offset += bytes.length;
    return entry;
  });
  fs.writeFileSync(ico, Buffer.concat([header, ...entries, ...images]));
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
