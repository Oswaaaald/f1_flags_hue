import { build } from 'esbuild';
import { cp, mkdir, readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
await mkdir('dist', { recursive: true });
await build({ entryPoints: ['src/app.ts'], bundle: true, outfile: 'dist/app.js', minify: true, sourcemap: false, target: ['safari16', 'chrome110', 'firefox110'] });
await Promise.all(['index.html', 'app.css', 'icon.svg', 'manifest.webmanifest'].map(file => cp(`src/${file}`, `dist/${file}`)));
let html = await readFile('dist/index.html', 'utf8');
for (const file of ['app.js', 'app.css']) {
  const hash = createHash('sha256').update(await readFile(`dist/${file}`)).digest('hex').slice(0, 16);
  html = html.replace(`/${file}"`, `/${file}?v=${hash}"`);
}
await writeFile('dist/index.html', html);
