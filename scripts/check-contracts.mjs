import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { readFile, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
const root = path.resolve(import.meta.dirname, '..');
const temporary = await mkdtemp(path.join(tmpdir(), 'f1hue-contract-'));
try {
  const target = path.join(temporary, 'types.ts');
  const dotnet = existsSync(path.join(root, '.tools/dotnet/dotnet')) ? path.join(root, '.tools/dotnet/dotnet') : 'dotnet';
  execFileSync(dotnet, [path.join(root, 'apps/host/bin/Release/net10.0/f1-hue.dll'), '--export-contract', target]);
  execFileSync(process.execPath, [path.join(root, 'apps/web/node_modules/prettier/bin/prettier.cjs'), '--write', '--end-of-line', 'lf', target]);
  assert.equal((await readFile(path.join(root, 'apps/web/src/types.ts'), 'utf8')).replaceAll('\r\n', '\n'), await readFile(target, 'utf8'), 'The browser contract differs from the API. Run make contracts.');
  console.log('PASS generated browser contract matches the .NET API');
} finally { await rm(temporary, { recursive: true, force: true }); }
