// Only archives built by this workflow are unpacked. Never read a user profile.
import { readdir, mkdir } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
for (const file of await readdir('release')) {
  if (!/^f1-hue-.*\.(zip|tar\.gz)$/.test(file)) continue;
  const directory = 'inventory/' + file.replace(/\.(zip|tar\.gz)$/, '');
  await mkdir(directory, { recursive: true });
  if (file.endsWith('.zip')) execFileSync('unzip', ['-q', 'release/' + file, '-d', directory]);
  else execFileSync('tar', ['-xzf', 'release/' + file, '-C', directory]);
}
