// Native launcher fixture: authenticate through its private, temporary CI profile.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
assert.equal(process.env.CI, 'true', 'Requires a disposable CI runner');
const root = 'http://127.0.0.1:8081/api';
let cookie = '';
async function request(route, body, headers = {}) {
  const response = await fetch(root + route, { method: 'POST', headers: { Cookie: cookie, 'X-F1Hue-Request': '1', 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(body ?? {}) });
  assert.equal(response.status, 200, await response.clone().text());
  if (response.headers.has('set-cookie')) cookie = response.headers.get('set-cookie').split(';')[0];
  return response.json();
}
const secret = (await readFile(path.join(process.env.F1_HUE_LAUNCHER_PROFILE, 'desktop-launch.key'), 'utf8')).trim();
const { ticket } = await request('/auth/desktop/ticket', undefined, { 'X-F1Hue-Launcher': secret });
await request('/auth/desktop/login', { ticket });
await request('/hue/select', { lightIds: ['11111111-1111-1111-1111-111111111111'], groupIds: [] });
await request('/test/preview', { flag: 'SC' });
const state = await (await fetch(root + '/state', { headers: { Cookie: cookie } })).json();
assert.ok(state.simulation && state.runner.running);
console.log('PASS Actual native launcher owns a simulated effect with automatic desktop authentication');
// A separate SSE consumer remains connected when the calling script requests Quit.
const { spawn } = await import('node:child_process');
const child = spawn(process.execPath, ['--input-type=module', '-e', 'const c = new AbortController(); setTimeout(() => c.abort(), 15000).unref(); try { const r = await fetch(process.env.F1_HUE_TEST_SSE_URL, { headers: { Cookie: process.env.F1_HUE_TEST_COOKIE }, signal: c.signal }); for await (const _ of r.body) {} } catch {}'], { detached: true, stdio: 'ignore', env: { ...process.env, F1_HUE_TEST_SSE_URL: root + '/events', F1_HUE_TEST_COOKIE: cookie } });
child.unref();
await new Promise(resolve => setTimeout(resolve, 150));
