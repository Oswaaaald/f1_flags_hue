import { createHash } from 'node:crypto';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
export async function sourceBuildId(root = import.meta.dirname) {
  const hash = createHash('sha256');
  async function add(relative) {
    const files = await readdir(path.join(root, relative), { withFileTypes: true });
    for (const file of files.sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
      const name = path.posix.join(relative, file.name);
      if (file.isDirectory()) await add(name);
      else hash.update(name).update('\0').update((await readFile(path.join(root, name), 'utf8')).replaceAll('\r\n', '\n'));
    }
  }
  for (const file of ['package.json', 'package-lock.json', 'build.mjs', 'build-id.mjs']) hash.update(file).update((await readFile(path.join(root, file), 'utf8')).replaceAll('\r\n', '\n'));
  await add('src');
  return hash.digest('hex').slice(0, 20);
}
