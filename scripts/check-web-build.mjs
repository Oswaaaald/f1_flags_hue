import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { sourceBuildId } from '../apps/web/build-id.mjs';
const manifest = JSON.parse(await readFile('apps/web/dist/build.json', 'utf8'));
assert.equal(manifest.id, await sourceBuildId(), 'Frontend build is stale: run npm run build --prefix apps/web');
console.log('PASS embedded frontend matches its source fingerprint');
