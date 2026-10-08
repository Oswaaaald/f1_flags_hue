// Exercise the installer filesystem transaction with an isolated systemctl fake.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, writeFile, readFile, readlink, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
if (process.platform !== 'linux') { console.log('SKIP Linux installer transaction requires GNU mv'); process.exit(0); }
const temporary = await mkdtemp(path.join(tmpdir(), 'f1hue-install-'));
const install = path.join(temporary, 'install with spaces'), units = path.join(temporary, 'units'), source = path.join(temporary, 'source'), fake = path.join(temporary, 'bin');
try {
  for (const directory of [fake, source]) await mkdir(directory, { recursive: true });
  await writeFile(path.join(fake, 'systemctl'), `#!/bin/sh
case "$*" in
 *show*) if [ -f "$F1_HUE_UNIT_DIR/f1-hue.service" ]; then echo loaded; else echo not-found; fi ;;
 *is-active*) [ -f "$F1_HUE_TEST_STATE/active" ] ;;
 *stop*) [ "\${F1_HUE_TEST_FAIL:-}" != stop ] || exit 1; rm -f "$F1_HUE_TEST_STATE/active" ;;
 *enable*) [ "\${F1_HUE_TEST_FAIL:-}" != start ] || exit 1; touch "$F1_HUE_TEST_STATE/active" ;;
 *start*) touch "$F1_HUE_TEST_STATE/active" ;;
esac
`, { mode: 0o700 });
  const env = { ...process.env, PATH: fake + path.delimiter + process.env.PATH, F1_HUE_INSTALL_ROOT: install, F1_HUE_UNIT_DIR: units, F1_HUE_DATA_DIR: path.join(temporary, 'data'), F1_HUE_TEST_STATE: temporary };
  const run = failure => spawnSync('sh', ['deploy/linux/install.sh', source], { env: { ...env, F1_HUE_TEST_FAIL: failure ?? '' }, encoding: 'utf8' });
  await writeFile(path.join(source, 'f1-hue'), '#!/bin/sh\necho 2.0.0-test.1\n', { mode: 0o700 });
  assert.equal(run().status, 0);
  const old = await readlink(path.join(install, 'current'));
  const oldUnit = await readFile(path.join(units, 'f1-hue.service'), 'utf8');
  await writeFile(path.join(source, 'f1-hue'), '#!/bin/sh\necho 2.0.0-test.2\n', { mode: 0o700 });
  assert.notEqual(run('stop').status, 0);
  assert.equal(await readlink(path.join(install, 'current')), old);
  console.log('PASS Failed service stop never replaces a running installation');
  assert.notEqual(run('start').status, 0);
  assert.equal(await readlink(path.join(install, 'current')), old);
  assert.equal(await readFile(path.join(units, 'f1-hue.service'), 'utf8'), oldUnit);
  assert.ok((await stat(path.join(temporary, 'active'))).isFile());
  console.log('PASS Failed update startup rolls back both the executable pointer and the service unit');
  assert.equal(run().status, 0);
  assert.notEqual(await readlink(path.join(install, 'current')), old);
  assert.ok((await stat(path.join(old, 'f1-hue'))).isFile());
  console.log('PASS Successful update atomically switches versions and retains the previous package');
} finally { await rm(temporary, { recursive: true, force: true }); }
