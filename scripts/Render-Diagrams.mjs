import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const directory = fileURLToPath(new URL('../docs/diagrams/', import.meta.url));
const skillRoot = resolve(process.argv[2] ?? join(homedir(), '.codex/skills/pretty-mermaid'));
const renderer = join(skillRoot, 'scripts/batch.mjs');
const packagePath = join(skillRoot, 'node_modules/beautiful-mermaid/package.json');
assert(existsSync(renderer) && existsSync(packagePath), 'Install the pretty-mermaid skill and its dependencies, or pass its directory as the first argument.');
const version = JSON.parse(readFileSync(packagePath, 'utf8')).version;
const temporary = mkdtempSync(join(tmpdir(), 'yoursafe-diagrams-'));
const input = join(temporary, 'input');
const output = join(temporary, 'output');
const escape = value => value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
const sources = new Map();

try {
  mkdirSync(input);
  for (const file of readdirSync(directory).filter(name => name.endsWith('.mmd')).sort()) {
    const source = readFileSync(join(directory, file), 'utf8');
    const title = source.match(/^\s*accTitle: (.+)$/m)?.[1];
    const description = source.match(/^\s*accDescr: (.+)$/m)?.[1];
    assert(title && description, `${file}: accessible title and description required`);
    sources.set(file, { source, title, description });
    // beautiful-mermaid 1.1.3 treats accessibility directives as nodes; restore them on the SVG root instead.
    writeFileSync(join(input, file), source.replace(/^\s*acc(?:Title|Descr):[^\r\n]*\r?\n/gm, ''));
  }
  assert(sources.size > 0, 'No Mermaid sources found.');
  const rendered = spawnSync(process.execPath, [renderer, '--input-dir', input, '--output-dir', output,
    '--theme', 'github-light', '--font', 'Segoe UI', '--padding', '32', '--node-spacing', '36', '--layer-spacing', '48'],
  { stdio: 'inherit' });
  assert.equal(rendered.status, 0, rendered.error?.message ?? 'Diagram rendering failed.');
  const figures = [];
  for (const [file, { source, title, description }] of sources) {
    // Keep exports offline: the renderer adds Google Fonts imports even for an installed Windows font.
    const svg = readFileSync(join(output, file.replace('.mmd', '.svg')), 'utf8').replace(/@import\s+url\([^)]*\)\s*;?/g, '');
    assert(svg.startsWith('<svg '), `${file}: SVG output required`);
    assert(!/<script\b|<foreignObject\b|@import|(?:href|src)=["']https?:|\b(?:NaN|undefined)\b/i.test(svg), `${file}: invalid or non-standalone SVG`);
    const dimensions = svg.match(/viewBox="0 0 ([\d.]+) ([\d.]+)"/);
    assert(dimensions, `${file}: finite SVG dimensions required`);
    const width = Number(dimensions[1]), height = Number(dimensions[2]);
    assert(width > 0 && height > 0 && svg.includes('<text'), `${file}: empty figure`);
    const frameWidth = Math.max(820, width + 48), frameHeight = height + 112;
    const metadata = escape(JSON.stringify({ source: file, sourceSha256: createHash('sha256').update(source).digest('hex'),
      renderer: `beautiful-mermaid@${version}`, theme: 'github-light', line: '#64748b', muted: '#334155', border: '#94a3b8' }));
    const opening = svg.match(/^<svg\b[^>]*>/)[0]
      .replace(/viewBox="[^"]*"/, `viewBox="0 0 ${frameWidth} ${frameHeight}"`)
      .replace(/width="[^"]*"/, `width="${frameWidth}"`).replace(/height="[^"]*"/, `height="${frameHeight}"`)
      .replace('<svg ', '<svg role="img" aria-labelledby="title desc" ')
      .replace(/--line:[^;"]+/, '--line:#64748b').replace(/--muted:[^;"]+/, '--muted:#334155')
      .replace('style="', 'style="--border:#94a3b8;');
    const body = svg.slice(svg.indexOf('>') + 1, svg.lastIndexOf('</svg>'));
    const header = `<title id="title">${escape(title)}</title><desc id="desc">${escape(description)}</desc><metadata>${metadata}</metadata>`
      + `<rect width="100%" height="100%" fill="#ffffff"/><g font-family="Segoe UI,Arial,sans-serif" fill="#0f172a">`
      + `<text x="24" y="34" font-size="24" font-weight="600">${escape(title)}</text>`
      + '<text x="24" y="63" font-size="14">Read arrows in order. SECRET = plaintext; KEY = encryption key; cylinders = stored data.</text></g>';
    const result = `${opening}${header}<g transform="translate(${(frameWidth - width) / 2},88)">${body}</g></svg>\n`.replace(/[ \t]+$/gm, '');
    assert(result.includes(metadata) && result.endsWith('</svg>\n'), `${file}: incomplete export`);
    figures.push([file.replace('.mmd', '.svg'), result]);
  }
  // Render and check every figure before replacing any checked-in output; screenshot SVGs are untouched.
  for (const [file, svg] of figures) writeFileSync(join(directory, file), svg);
  console.log(`Wrote ${figures.length} accessible SVGs using github-light; annotated screenshots preserved.`);
} finally {
  assert.equal(dirname(resolve(temporary)), resolve(tmpdir()), 'Temporary cleanup must stay inside the system temp directory.');
  assert(temporary.includes('yoursafe-diagrams-'));
  rmSync(temporary, { recursive: true, force: true });
}
