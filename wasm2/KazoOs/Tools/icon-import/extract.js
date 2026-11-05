// Regenerates the per-library icon JSON resources embedded by Kazo.Os.AppSdk.Icons
// (Shared/Kazo.Os.AppSdk.Icons/Data/*.json) from the upstream npm packages listed in
// package.json. Run `npm install && npm run extract` from this directory, then review
// and commit the changed Data/*.json files. See ../../docs/icon-library.md.
const fs = require('fs');
const path = require('path');

const ROOT = __dirname;
const OUT = path.join(ROOT, '..', '..', 'Shared', 'Kazo.Os.AppSdk.Icons', 'Data');
fs.mkdirSync(OUT, { recursive: true });

function readSvg(filePath) {
  return fs.readFileSync(filePath, 'utf8');
}

function extract(svg) {
  // Strip XML/HTML comments (license banners).
  svg = svg.replace(/<!--[\s\S]*?-->/g, '');
  const viewBoxMatch = svg.match(/viewBox="([^"]+)"/);
  const viewBox = viewBoxMatch ? viewBoxMatch[1] : '0 0 24 24';
  const strokeBased = /\bstroke="currentColor"/.test(svg) || /\bstroke-width=/.test(svg);
  // Inner content between the first '>' of the opening <svg ...> tag and '</svg>'.
  const openEnd = svg.indexOf('>');
  const closeStart = svg.lastIndexOf('</svg>');
  let body = svg.slice(openEnd + 1, closeStart).trim();
  // Strip <title>...</title> (we surface the friendly name separately).
  let title = null;
  const titleMatch = body.match(/<title>([\s\S]*?)<\/title>/);
  if (titleMatch) {
    title = titleMatch[1].trim();
    body = body.replace(/<title>[\s\S]*?<\/title>/, '').trim();
  }
  // Normalize whitespace between tags.
  body = body.replace(/>\s+</g, '><').trim();
  return { viewBox, body, strokeBased, title };
}

function toDisplayName(fileBase) {
  return fileBase
    .split('-')
    .filter(Boolean)
    .map(w => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
}

function collect(dir, opts = {}) {
  const { nameSuffix = '', group = null } = opts;
  const files = fs.readdirSync(dir).filter(f => f.endsWith('.svg'));
  const icons = [];
  for (const file of files) {
    const base = path.basename(file, '.svg');
    const name = base + nameSuffix;
    const svg = readSvg(path.join(dir, file));
    const { viewBox, body, strokeBased, title } = extract(svg);
    if (!body) continue;
    // Single-letter keys: n(ame) v(iewBox) b(ody) s(trokeBased) g(roup) t(itle).
    // See IconCatalogJsonSerializerContext.cs for the matching C# record.
    icons.push({
      n: name,
      v: viewBox,
      b: body,
      s: strokeBased ? 1 : 0,
      g: group,
      t: title || toDisplayName(base),
    });
  }
  return icons;
}

// --- Bootstrap Icons (MIT) ---
const bootstrap = collect(path.join(ROOT, 'node_modules/bootstrap-icons/icons'));
fs.writeFileSync(path.join(OUT, 'bootstrap-icons.json'), JSON.stringify(bootstrap));
console.log('bootstrap-icons:', bootstrap.length);

// --- Font Awesome Free (solid + regular + brands) ---
const faSolid = collect(path.join(ROOT, 'node_modules/@fortawesome/fontawesome-free/svgs/solid'), { group: 'solid' });
const faRegular = collect(path.join(ROOT, 'node_modules/@fortawesome/fontawesome-free/svgs/regular'), { nameSuffix: '-regular', group: 'regular' });
const faBrands = collect(path.join(ROOT, 'node_modules/@fortawesome/fontawesome-free/svgs/brands'), { group: 'brands' });
// A handful of Font Awesome's own meta icons (e.g. "font-awesome") exist in both the solid
// and brands styles under the same bare file name; disambiguate the brands copy so every
// (library, name) pair stays unique.
const usedNames = new Set([...faSolid, ...faRegular].map(i => i.n));
for (const icon of faBrands) {
  if (usedNames.has(icon.n)) icon.n = `${icon.n}-brand`;
  usedNames.add(icon.n);
}
const fontAwesome = [...faSolid, ...faRegular, ...faBrands];
fs.writeFileSync(path.join(OUT, 'font-awesome.json'), JSON.stringify(fontAwesome));
console.log('font-awesome:', fontAwesome.length, '(solid', faSolid.length, 'regular', faRegular.length, 'brands', faBrands.length, ')');

// --- Lucide (ISC; Feather-lineage) ---
const lucide = collect(path.join(ROOT, 'node_modules/lucide-static/icons'));
fs.writeFileSync(path.join(OUT, 'lucide.json'), JSON.stringify(lucide));
console.log('lucide:', lucide.length);

// --- Simple Icons (brand logos, CC0) ---
const simple = collect(path.join(ROOT, 'node_modules/simple-icons/icons'));
fs.writeFileSync(path.join(OUT, 'simple-icons.json'), JSON.stringify(simple));
console.log('simple-icons:', simple.length);

// Sanity: check for duplicate names within a single library (would break lookup-by-name).
let anyDupes = false;
for (const [lib, icons] of [['bootstrap', bootstrap], ['fontawesome', fontAwesome], ['lucide', lucide], ['simple', simple]]) {
  const seen = new Set();
  let dupes = 0;
  for (const icon of icons) {
    if (seen.has(icon.n)) dupes++;
    seen.add(icon.n);
  }
  console.log(lib, 'duplicate names:', dupes);
  if (dupes > 0) anyDupes = true;
}
if (anyDupes) {
  console.error('Duplicate icon names found within a library; fix before committing.');
  process.exit(1);
}

let totalBytes = 0;
for (const f of fs.readdirSync(OUT)) {
  const sz = fs.statSync(path.join(OUT, f)).size;
  totalBytes += sz;
  console.log(f, (sz / 1024 / 1024).toFixed(2), 'MB');
}
console.log('TOTAL', (totalBytes / 1024 / 1024).toFixed(2), 'MB');
