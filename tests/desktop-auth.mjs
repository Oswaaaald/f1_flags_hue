// Uses disposable simulated profiles, including migration from password authentication.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import http from 'node:http';
import {readFile, writeFile, mkdtemp, rm, stat} from 'node:fs/promises';
import {existsSync} from 'node:fs';
import {tmpdir} from 'node:os';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const data = await mkdtemp(path.join(tmpdir(), 'f1hue-desktop-api-'));
const port = Number(process.env.F1_HUE_TEST_PORT ?? 18092);
const base = `http://127.0.0.1:${port}`;
const executable = process.env.F1_HUE_TEST_EXECUTABLE ?? (existsSync(path.join(root, '.tools/dotnet/dotnet')) ? path.join(root, '.tools/dotnet/dotnet') : 'dotnet');
const prefix = process.env.F1_HUE_TEST_EXECUTABLE ? [] : [path.join(root, `apps/host/bin/${process.env.F1_HUE_TEST_BUILD ?? 'Release'}/net10.0/f1-hue.dll`)];
let child;
let logs = '';
let cookie = '';
let checks = 0;
function check(condition, label) { assert.ok(condition, label); checks++; console.log('PASS ' + label); }
async function start(desktop) {
    logs = '';
    child = spawn(executable, [...prefix, '--simulate', '--no-feed', '--data', data, '--port', String(port), ...(desktop ? ['--desktop'] : [])], {
        env: {...process.env, F1_HUE_ALLOWED_HOSTS: 'attacker.example'}, stdio: ['ignore', 'pipe', 'pipe'],
    });
    child.stdout.on('data', bytes => logs += bytes); child.stderr.on('data', bytes => logs += bytes);
    for (let i = 0; i < 100; i++) {
        try { if ((await fetch(base + '/health', {signal: AbortSignal.timeout(500)})).ok) return; } catch {}
        if (child.exitCode !== null) throw new Error('Service exited: ' + logs);
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('Service did not start: ' + logs);
}
async function stop() {
    if (!child || child.exitCode !== null) return;
    const processToStop = child;
    const stopped = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => { processToStop.kill('SIGKILL'); reject(new Error('Service did not stop')); }, 10000);
        processToStop.once('exit', () => { clearTimeout(timeout); resolve(); });
    });
    await writeFile(path.join(data, 'stop.request'), 'stop');
    await stopped;
    child = null;
}
async function req(url, method = 'GET', body, expected = 200, headers = {}) {
    const response = await fetch(base + url, {
        method, signal: AbortSignal.timeout(5000),
        headers: {'X-F1Hue-Request': '1', ...(body === undefined ? {} : {'Content-Type': 'application/json'}), ...(cookie ? {Cookie: cookie} : {}), ...headers},
        body: body === undefined ? undefined : JSON.stringify(body),
    });
    assert.equal(response.status, expected, `${method} ${url} returned ${response.status}`);
    const session = response.headers.get('set-cookie'); if (session) cookie = session.split(';')[0];
    return {response, body: await response.json().catch(() => null)};
}
async function ticket(secret) { return (await req('/api/auth/desktop/ticket', 'POST', undefined, 200, {'X-F1Hue-Launcher': secret})).body.ticket; }
async function rejectHost(host) {
    const status = await new Promise((resolve, reject) => {
        const request = http.request(base + '/health', {headers: {Host: host}}, response => { response.resume(); resolve(response.statusCode); });
        request.on('error', reject); request.end();
    });
    assert.equal(status, 403);
}
try {
    await start(true);
    const fresh = (await req('/api/auth/status')).body;
    await req('/api/state', 'GET', undefined, 401);
    check(fresh.mode === 'desktop' && !fresh.setupRequired && !existsSync(path.join(data, 'setup-code.txt')), 'A fresh desktop profile requires no password and grants no anonymous access');
    await stop();
    await start(false);
    const setup = (await readFile(path.join(data, 'setup-code.txt'), 'utf8')).trim();
    await req('/api/auth/setup', 'POST', {code: setup, password: 'existing-local-password-123'});
    const passwordCookie = cookie;
    await req('/api/settings', 'PATCH', {offsetSeconds: 41.5});
    await req('/api/auth/desktop/ticket', 'POST', undefined, 404);
    check(true, 'Password mode does not expose launcher authentication');
    await stop();
    const savedBridge = 'simulated-encrypted-bridge-fixture';
    await writeFile(path.join(data, 'bridge.enc'), savedBridge);

    await start(true);
    const status = (await req('/api/auth/status')).body;
    check(status.mode === 'desktop' && !status.setupRequired && !status.authenticated, 'Desktop starts without password setup and rejects the old password session');
    await req('/api/state', 'GET', undefined, 401);
    await req('/api/auth/login', 'POST', {password: 'existing-local-password-123'}, 404);
    await req('/api/auth/setup', 'POST', {code: setup, password: 'another-local-password-123'}, 404);
    check(true, 'Desktop keeps the API protected without a password endpoint');
    const keyPath = path.join(data, 'desktop-launch.key');
    const secret = (await readFile(keyPath, 'utf8')).trim();
    const mode = (await stat(keyPath)).mode & 0o777;
    check(secret.length === 64 && (process.platform === 'win32' || mode === 0o600), 'Launcher secret has private file permissions');
    await req('/api/auth/desktop/ticket', 'POST', undefined, 401);
    await req('/api/auth/desktop/ticket', 'POST', undefined, 401, {'X-F1Hue-Launcher': '0'.repeat(64)});
    check(true, 'Public callers cannot mint desktop tickets');
    await req('/api/auth/desktop/ticket', 'POST', undefined, 403, {'X-F1Hue-Launcher': secret, 'X-F1Hue-Request': ''});
    await req('/api/auth/desktop/ticket', 'POST', undefined, 403, {'X-F1Hue-Launcher': secret, Origin: 'https://evil.example'});
    await req('/api/auth/desktop/ticket', 'POST', undefined, 403, {'X-F1Hue-Launcher': secret, 'Sec-Fetch-Site': 'cross-site'});
    await rejectHost('attacker.example');
    await rejectHost('192.168.1.10:' + port);
    await rejectHost('127.0.0.1:' + (port + 1));
    check(true, 'Desktop enforces CSRF, canonical loopback Host and port even with an allowed-host override');
    const code = await ticket(secret);
    const login = await req('/api/auth/desktop/login', 'POST', {ticket: code});
    const setCookie = login.response.headers.get('set-cookie');
    check(setCookie.includes('httponly') && setCookie.includes('samesite=strict') && setCookie.includes('max-age=28800'), 'Desktop exchange issues a protected eight-hour session cookie');
    await req('/api/auth/desktop/login', 'POST', {ticket: code}, 401);
    await req('/api/auth/desktop/login', 'POST', {ticket: '0'.repeat(64)}, 401);
    check(true, 'Consumed and fabricated tickets are rejected');
    const state = (await req('/api/state')).body;
    check(state.settings.offsetSeconds === 41.5 && !(JSON.stringify(state).includes(secret) || JSON.stringify(state).includes(code)), 'Automatic authentication preserves settings and exposes no launcher secrets');
    const inventory = (await req('/api/hue/inventory')).body;
    await req('/api/hue/select', 'POST', {lightIds: [inventory.lights[0].id], groupIds: [], entertainmentAreaId: null});
    await req('/api/test/preview', 'POST', {flag: 'RED'}); await req('/api/stop', 'POST');
    check(true, 'Automatically authenticated sessions can control and stop simulated lights');
    const pendingCode = await ticket(secret);
    const desktopCookie = cookie;
    await stop();
    check(!existsSync(keyPath) && await readFile(path.join(data, 'bridge.enc'), 'utf8') === savedBridge && !logs.includes(secret) && !logs.includes(code), 'Shutdown removes launcher proof, preserves the Hue vault and logs no access secrets');

    await start(true);
    await req('/api/state', 'GET', undefined, 401);
    await req('/api/auth/desktop/ticket', 'POST', undefined, 401, {'X-F1Hue-Launcher': secret});
    await req('/api/auth/desktop/login', 'POST', {ticket: pendingCode}, 401);
    const newSecret = (await readFile(keyPath, 'utf8')).trim();
    check(newSecret !== secret, 'Restart invalidates desktop sessions, launcher secrets and unused tickets');
    await req('/api/auth/desktop/login', 'POST', {ticket: await ticket(newSecret)});
    await req('/api/auth/logout', 'POST');
    await req('/api/state', 'GET', undefined, 401);
    check(true, 'Logout still revokes desktop access');
    await stop();

    await start(false);
    cookie = desktopCookie;
    await req('/api/state', 'GET', undefined, 401);
    const network = (await req('/api/auth/status')).body;
    await req('/api/auth/login', 'POST', {password: 'existing-local-password-123'});
    check(network.mode === 'password' && !network.setupRequired && (await req('/api/settings')).body.offsetSeconds === 41.5, 'Returning to server mode preserves the password and settings and rejects desktop sessions');
    cookie = passwordCookie;
    check((await req('/api/auth/status')).body.authenticated, 'Desktop mode does not overwrite previously stored password sessions');
    await stop();

    const invalid = spawn(executable, [...prefix, '--desktop', '--listen', '0.0.0.0', '--data', data], {stdio: 'ignore'});
    const exit = await new Promise((resolve, reject) => { invalid.once('exit', resolve); setTimeout(() => { invalid.kill(); reject(new Error('Invalid desktop listener did not exit')); }, 5000).unref(); });
    check(exit === 2 && !existsSync(keyPath), 'Desktop mode refuses network listeners before creating any access secret');
    console.log(`\n${checks} desktop authentication API checks passed.`);
} finally { await stop(); await rm(data, {recursive: true, force: true}); }
