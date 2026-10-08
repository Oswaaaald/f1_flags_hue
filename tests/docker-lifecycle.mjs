import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
const name = 'f1hue-test-' + randomUUID(), volume = name + '-data', port = 18380;
const docker = (...args) => execFileSync('docker', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
let cookie = '';
async function request(route, method = 'GET', body) {
  const response = await fetch(`http://127.0.0.1:${port}${route}`, { method, signal: AbortSignal.timeout(10000), headers: { Cookie: cookie, 'X-F1Hue-Request': '1', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) }, body: body === undefined ? undefined : JSON.stringify(body) });
  assert.equal(response.status, 200, await response.clone().text());
  if (response.headers.has('set-cookie')) cookie = response.headers.get('set-cookie').split(';')[0];
  return response.json();
}
async function ready() {
  for (let i = 0; i < 100; i++) { try { if ((await fetch(`http://127.0.0.1:${port}/health`)).ok) return; } catch {} await new Promise(r => setTimeout(r, 100)); }
  throw new Error('Container startup failed: ' + docker('logs', name));
}
try {
  docker('volume', 'create', volume);
  docker('run', '-d', '--name', name, '--read-only', '--cap-drop=ALL', '--security-opt=no-new-privileges', '--tmpfs', '/tmp:rw,size=32m,mode=1777', '-v', `${volume}:/data`, '-p', `127.0.0.1:${port}:8080`, 'f1-hue:test', '--simulate', '--no-feed');
  await ready();
  const code = docker('exec', name, 'cat', '/data/setup-code.txt');
  await request('/api/auth/setup', 'POST', { code, password: 'docker-disposable-test' });
  const inventory = await request('/api/hue/inventory');
  await request('/api/hue/select', 'POST', { lightIds: [inventory.lights[0].id], groupIds: [] });
  await request('/api/settings', 'PATCH', { offsetSeconds: 41.5 });
  await request('/api/test/preview', 'POST', { flag: 'SC' });
  const controller = new AbortController();
  const stream = await fetch(`http://127.0.0.1:${port}/api/events`, { headers: { Cookie: cookie }, signal: controller.signal });
  await stream.body.getReader().read();
  const start = Date.now(); docker('stop', '--time', '45', name);
  assert.ok(Date.now() - start < 5000, 'Stop must not wait for the SSE timeout'); controller.abort();
  assert.equal(docker('inspect', '-f', '{{.State.ExitCode}}', name), '0');
  docker('start', name); await ready();
  assert.equal((await request('/api/settings')).offsetSeconds, 41.5);
  assert.equal((await request('/api/state')).runner.running, false);
  assert.notEqual(docker('exec', name, 'id', '-u'), '0');
  console.log('PASS Container runs without root/writeable rootfs, stops with SSE open and preserves settings after restart');
} finally { spawnSync('docker', ['rm', '-f', name], { stdio: 'ignore' }); spawnSync('docker', ['volume', 'rm', volume], { stdio: 'ignore' }); }
