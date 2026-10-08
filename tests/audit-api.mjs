// Disposable, simulated services only. Includes the real HTTP shutdown path.
import assert from 'node:assert/strict';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { existsSync } from 'node:fs';
import { mkdtemp, readFile, writeFile, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
const root = path.resolve(import.meta.dirname, '..');
const dotnet = existsSync(path.join(root, '.tools/dotnet/dotnet')) ? path.join(root, '.tools/dotnet/dotnet') : 'dotnet';
const executable = process.env.F1_HUE_TEST_EXECUTABLE ?? dotnet;
const prefix = process.env.F1_HUE_TEST_EXECUTABLE ? [] : [path.join(root, 'apps/host/bin/Release/net10.0/f1-hue.dll')];
let checks = 0;
function check(value, label) { assert.ok(value, label); checks++; console.log('PASS ' + label); }
for (const trusted of [false, true]) {
  const data = await mkdtemp(path.join(tmpdir(), 'f1hue-http-audit-'));
  const port = Number(process.env.F1_HUE_AUDIT_PORT ?? 18374) + Number(trusted);
  const base = `http://127.0.0.1:${port}`;
  let logs = '', cookie = '';
  const child = spawn(executable, [...prefix, '--simulate', '--no-feed', '--data', data, '--port', String(port)], {
    env: { ...process.env, F1_HUE_ALLOWED_HOSTS: 'audit.example', F1_HUE_TRUSTED_PROXIES: trusted ? '127.0.0.1' : '' }, stdio: ['ignore', 'pipe', 'pipe'],
  });
  const exited = once(child, 'exit');
  child.stdout.on('data', b => logs += b); child.stderr.on('data', b => logs += b);
  const controllers = [];
  async function req(route, method = 'GET', body, expected = 200, headers = {}) {
    const options = { method, signal: AbortSignal.timeout(10000), headers: {
      'X-F1Hue-Request': '1', ...(cookie ? { Cookie: cookie } : {}), ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...headers,
    }, body: body === undefined ? undefined : JSON.stringify(body) };
    const response = headers.Host ? await new Promise((resolve, reject) => {
      const request = http.request(base + route, { method, headers: options.headers, timeout: 10000 }, res => {
        let text = ''; res.on('data', b => text += b); res.on('end', () => resolve({ status: res.statusCode, headers: new Headers(Object.fromEntries(Object.entries(res.headers).map(([key, value]) => [key, Array.isArray(value) ? value.join('; ') : value]))), json: async () => JSON.parse(text) }));
      });
      request.on('error', reject); request.on('timeout', () => request.destroy(new Error('timeout'))); request.end(options.body);
    }) : await fetch(base + route, options);
    const result = await response.json().catch(() => null);
    assert.equal(response.status, expected, `${route}: ${JSON.stringify(result)}`);
    const token = response.headers.get('set-cookie'); if (token) cookie = token.split(';')[0];
    return { response, body: result };
  }
  try {
    for (let i = 0; ; i++) {
      try { if ((await fetch(base + '/health')).ok) break; } catch {}
      if (i > 100 || child.exitCode !== null) throw new Error('Startup failed: ' + logs);
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    const proxy = { Host: 'audit.example', Origin: 'https://audit.example', 'X-Forwarded-Proto': 'https', 'X-Forwarded-For': '192.0.2.50' };
    await req('/api/auth/status', 'GET', undefined, trusted ? 200 : 403, proxy);
    check(true, trusted ? 'Explicitly trusted HTTPS proxy retains same-origin protection' : 'Untrusted forwarded headers do not change the effective request origin');
    const code = (await readFile(path.join(data, 'setup-code.txt'), 'utf8')).trim();
    const setup = await req('/api/auth/setup', 'POST', { code, password: 'disposable-audit-password' }, 200, trusted ? proxy : {});
    check(setup.response.headers.get('set-cookie').toLowerCase().includes('secure') === trusted, 'Session cookie Secure follows the verified TLS scheme');
    const before = (await req('/api/settings')).body;
    await req('/api/settings', 'PATCH', { brightness: 155 }, 200, { 'If-Match': `"${before.revision}"` });
    const conflict = await req('/api/settings', 'PATCH', { brightness: 99 }, 409, { 'If-Match': `"${before.revision}"` });
    check(conflict.body.code === 'settings_conflict' && (await req('/api/settings')).body.brightness === 155, 'A stale form receives a structured conflict and does not overwrite current settings');
    if (trusted) continue;
    for (const [route, body] of [
      ['/api/test/preview', []], ['/api/test/preview', null], ['/api/replay/start', []], ['/api/replay/start', { kind: 'local', id: 'x', speed: {} }],
      ['/api/calibration/arm', []], ['/api/calibration/clock', false], ['/api/hue/pair', []], ['/api/hue/pair', { ip: '127.0.0.1' }],
      ['/api/test/preview', {}], ['/api/test/preview', { flag: null }], ['/api/test/preview', { flag: '0' }], ['/api/test/preview', { flag: 'GREEN', unexpected: true }], ['/api/calibration/adjust', {}], ['/api/hue/select', { lightIds: null, groupIds: [] }], ['/api/hue/scene', null], ['/api/settings', []], ['/api/settings', { effects: [] }],
    ]) await req(route, route === '/api/settings' ? 'PATCH' : 'POST', body, 400);
    check(true, 'Malformed request shapes return 400 without internal errors');
    await req('/ready'); const schema = (await req('/api/schema')).body;
    check(schema.State && schema.SelectionRequest && JSON.stringify(schema).includes('revision'), 'Authenticated API schema is generated from server response and request types');
    const backup = (await req('/api/backup', 'POST')).body;
    check((await stat(path.join(data, 'backups', backup.file))).size > 0, 'Backup API writes a usable private database snapshot');
    for (const route of ['/api/recovery/available', '/api/recovery/pair', '/api/recovery/abandon'])
      await req(route, 'POST', route.endsWith('/pair') ? { ip: '192.168.1.10' } : route.endsWith('/abandon') ? { confirmation: 'ABANDONNER' } : undefined, 409);
    check(true, 'Recovery actions reject an absent recovery instead of changing a valid profile');
    const inventory = (await req('/api/hue/inventory')).body;
    await req('/api/hue/select', 'POST', { lightIds: [inventory.lights[0].id], groupIds: [], entertainmentAreaId: null });
    await req('/api/test/preview', 'POST', { flag: 'SC' });
    const diagnostic = (await req('/api/diagnostics')).body;
    check(diagnostic.timeline.some(row => row.stage === 'command_applied') && !JSON.stringify(diagnostic).includes('disposable-audit-password'), 'Bounded diagnostic records played effects without authentication secrets');
    for (let i = 0; i < 20; i++) {
      const controller = new AbortController(); controllers.push(controller);
      const response = await fetch(base + '/api/events', { headers: { Cookie: cookie }, signal: controller.signal });
      assert.equal(response.status, 200); await response.body.getReader().read();
    }
    await req('/api/events', 'GET', undefined, 429);
    check(true, 'SSE capacity is enforced while existing pages remain connected');
    for (let i = 0; i < 5; i++) await req('/api/auth/login', 'POST', { password: 'wrong' }, i < 4 ? 401 : 429, { 'X-Forwarded-For': `192.0.2.${i}` });
    const limited = await req('/api/auth/login', 'POST', { password: 'wrong' }, 429);
    check(limited.response.headers.get('retry-after') === '60', 'Rate limit returns 429 and Retry-After; untrusted X-Forwarded-For cannot bypass it');
    const began = Date.now();
    await writeFile(path.join(data, 'stop.request'), 'stop');
    const [exit] = await Promise.race([exited, new Promise((_, reject) => setTimeout(() => reject(new Error('Shutdown exceeded budget')), 5000).unref())]);
    check(exit === 0 && Date.now() - began < 3000, `Shutdown stops SC and 20 SSE connections promptly (${Date.now() - began} ms)`);
  } finally {
    controllers.forEach(c => c.abort());
    if (child.exitCode === null) { await writeFile(path.join(data, 'stop.request'), 'stop'); await Promise.race([exited, new Promise(r => setTimeout(r, 5000).unref())]); }
    if (child.exitCode === null) { child.kill('SIGKILL'); await exited; }
    await rm(data, { recursive: true, force: true });
  }
}
console.log(`\n${checks} audit HTTP checks passed.`);
