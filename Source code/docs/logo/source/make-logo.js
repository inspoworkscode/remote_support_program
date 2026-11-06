// Builds every logo file for The Remote Support Program from this one script.
//
//   node make-logo.js <output folder>
//
// Needs opentype.js 1.3.4 and @resvg/resvg-js 2.6.2 (npm install them in any folder and point
// NODE_PATH at its node_modules). The wording is turned into outlines with Plus Jakarta Sans
// (SIL Open Font License, fonts\ beside this script), so no file needs the font to display.
"use strict";
const fs = require("fs");
const path = require("path");
const opentype = require("opentype.js");
const { Resvg } = require("@resvg/resvg-js");

const out = path.resolve(process.argv[2] || path.join(__dirname, ".."));
const fontDir = path.join(__dirname, "fonts");
const extraBold = opentype.loadSync(path.join(fontDir, "PlusJakartaSans-ExtraBold.ttf"));
const bold = opentype.loadSync(path.join(fontDir, "PlusJakartaSans-Bold.ttf"));

// The colours. The same purples as the website (site.css).
const C = {
  top: "#8b5cf6", bottom: "#5b21b6",   // the tile's gradient
  inner: "#ffffff", outer: "#d9ccfd",  // the two signal arcs
  ink: "#1b2430", inkDeep: "#6d28d9",  // wording on light grounds
  white: "#ffffff", lilac: "#a78bfa",  // wording on dark grounds
};

const r2 = n => Math.round(n * 100) / 100;

// A mouse pointer, tip at (0,0), 13 x 20 units, softened by a stroke of its own colour.
const ARROW = "M0 0 L0 17 L4.5 13 L7.5 20 L10.2 18.9 L7.4 12.3 L13 12.3 Z";
const pointer = (x, y, s) =>
  `<path d="${ARROW}" transform="translate(${x} ${y}) scale(${s})" fill="#fff" stroke="#fff" stroke-width="${r2(1.6 / s)}" stroke-linejoin="round"/>`;

// A quarter arc up and to the left of (cx, cy), trimmed at each end so the caps stand clear.
function arc(cx, cy, r, trim) {
  const a0 = (180 + trim) * Math.PI / 180, a1 = (270 - trim) * Math.PI / 180;
  const p = a => [r2(cx + r * Math.cos(a)), r2(cy + r * Math.sin(a))];
  const [x0, y0] = p(a0), [x1, y1] = p(a1);
  return `M${x0} ${y0} A${r} ${r} 0 0 1 ${x1} ${y1}`;
}

let gid = 0;
// The mark on a 120-unit tile. `small` is drawn for 32 pixels and under: one thicker arc and a
// bigger pointer, because two thin arcs run together at that size.
function markBody(small) {
  const id = "rsp" + (gid++);
  const tile = `<defs><linearGradient id="${id}" x1="0" y1="0" x2="1" y2="1">` +
    `<stop offset="0" stop-color="${C.top}"/><stop offset="1" stop-color="${C.bottom}"/></linearGradient></defs>` +
    `<rect width="120" height="120" rx="${small ? 26 : 28}" fill="url(#${id})"/>`;
  if (small) {
    const tx = 55, ty = 45;
    return tile +
      `<path d="${arc(tx, ty, 23, 4)}" fill="none" stroke="${C.inner}" stroke-width="11" stroke-linecap="round"/>` +
      pointer(tx, ty, 2.9);
  }
  const tx = 58, ty = 50;
  return tile +
    `<path d="${arc(tx, ty, 30, 6)}" fill="none" stroke="${C.outer}" stroke-width="7.5" stroke-linecap="round"/>` +
    `<path d="${arc(tx, ty, 16, 6)}" fill="none" stroke="${C.inner}" stroke-width="7.5" stroke-linecap="round"/>` +
    pointer(tx, ty, 2.55);
}
const markSvg = small =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 120" width="120" height="120">${markBody(small)}</svg>\n`;

// Text as one outline path, with optional tracking in em.
function textPath(font, text, x, baseline, size, trackEm = 0) {
  const scale = size / font.unitsPerEm;
  const glyphs = font.stringToGlyphs(text);
  const parts = [];
  let pen = x;
  glyphs.forEach((g, i) => {
    parts.push(g.getPath(pen, baseline, size).toPathData(2));
    pen += g.advanceWidth * scale + (i < glyphs.length - 1 ? trackEm * size : 0);
    if (!trackEm && i < glyphs.length - 1) pen += font.getKerningValue(g, glyphs[i + 1]) * scale;
  });
  return { d: parts.join(""), right: pen };
}

// The full logo: the mark at 96 units, the wording beside it, the block centred on the mark.
function logoSvg(onDark) {
  const markSize = 96, gap = 22, size1 = 41, size2 = 19.5, track2 = 0.62;
  const cap1 = extraBold.tables.os2.sCapHeight / extraBold.unitsPerEm * size1;
  const cap2 = bold.tables.os2.sCapHeight / bold.unitsPerEm * size2;
  const between = 0.36 * size1;                 // room for the descenders of "Support"
  const blockHeight = cap1 + between + cap2;
  const top = (markSize - blockHeight) / 2;
  const base1 = top + cap1, base2 = base1 + between + cap2;
  const x = markSize + gap;
  const l1 = textPath(extraBold, "The Remote Support", x, base1, size1);
  const l2 = textPath(bold, "PROGRAM", x, base2, size2, track2);
  const width = Math.ceil(Math.max(l1.right, l2.right)) + 2;
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${markSize}" width="${width}" height="${markSize}">` +
    `<g transform="scale(${markSize / 120})">${markBody(false)}</g>` +
    `<path d="${l1.d}" fill="${onDark ? C.white : C.ink}"/>` +
    `<path d="${l2.d}" fill="${onDark ? C.lilac : C.inkDeep}"/></svg>\n`;
}

function png(svg, width, file) {
  const r = new Resvg(svg, { fitTo: { mode: "width", value: width }, background: "rgba(0,0,0,0)" });
  const data = r.render().asPng();
  fs.writeFileSync(file, data);
  return data;
}

// A Windows .ico holding PNG images (allowed since Windows Vista).
function ico(images, file) {
  const head = Buffer.alloc(6 + 16 * images.length);
  head.writeUInt16LE(0, 0); head.writeUInt16LE(1, 2); head.writeUInt16LE(images.length, 4);
  let offset = head.length;
  images.forEach(({ size, data }, i) => {
    const e = 6 + 16 * i;
    head.writeUInt8(size >= 256 ? 0 : size, e); head.writeUInt8(size >= 256 ? 0 : size, e + 1);
    head.writeUInt16LE(1, e + 4); head.writeUInt16LE(32, e + 6);
    head.writeUInt32LE(data.length, e + 8); head.writeUInt32LE(offset, e + 12);
    offset += data.length;
  });
  fs.writeFileSync(file, Buffer.concat([head, ...images.map(i => i.data)]));
}

const pngDir = path.join(out, "png");
fs.mkdirSync(pngDir, { recursive: true });

const files = {
  "rsp-mark.svg": markSvg(false),
  "rsp-mark-small.svg": markSvg(true),
  "rsp-logo.svg": logoSvg(false),
  "rsp-logo-white.svg": logoSvg(true),
};
for (const [name, svg] of Object.entries(files)) fs.writeFileSync(path.join(out, name), svg);

for (const w of [1024, 512, 256, 48]) png(files["rsp-mark.svg"], w, path.join(pngDir, `rsp-mark-${w}.png`));
for (const w of [32, 16]) png(files["rsp-mark-small.svg"], w, path.join(pngDir, `rsp-mark-${w}.png`));
png(files["rsp-logo.svg"], 2400, path.join(pngDir, "rsp-logo-2400.png"));
png(files["rsp-logo-white.svg"], 2400, path.join(pngDir, "rsp-logo-white-2400.png"));
png(files["rsp-logo.svg"], 800, path.join(pngDir, "rsp-logo-800.png"));
png(files["rsp-logo-white.svg"], 800, path.join(pngDir, "rsp-logo-white-800.png"));

const icoImages = [16, 24, 32].map(size => ({ size, data: png(files["rsp-mark-small.svg"], size, path.join(pngDir, "_tmp.png")) }))
  .concat([48, 64, 128, 256].map(size => ({ size, data: png(files["rsp-mark.svg"], size, path.join(pngDir, "_tmp.png")) })));
fs.unlinkSync(path.join(pngDir, "_tmp.png"));
ico(icoImages, path.join(out, "rsp.ico"));

console.log("Written to " + out);
