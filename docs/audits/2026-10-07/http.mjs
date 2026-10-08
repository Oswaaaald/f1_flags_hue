import { spawn } from "node:child_process";
import http from "node:http";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
const root = path.resolve(import.meta.dirname, "../../..");
const data = await mkdtemp(path.join(tmpdir(), "f1hue-audit-http-"));
const port = Number(process.env.F1_HUE_AUDIT_PORT ?? 18371),
  base = `http://127.0.0.1:${port}`;
const dotnet = existsSync(`${root}/.tools/dotnet/dotnet`)
  ? `${root}/.tools/dotnet/dotnet`
  : "dotnet";
const child = spawn(
  dotnet,
  [
    `${root}/apps/host/bin/Release/net10.0/f1-hue.dll`,
    "--simulate",
    "--no-feed",
    "--data",
    data,
    "--port",
    String(port),
  ],
  {
    env: { ...process.env, F1_HUE_ALLOWED_HOSTS: "audit.example" },
    stdio: ["ignore", "pipe", "pipe"],
  },
);
let logs = "",
  cookie = "",
  closed = false;
child.stdout.on("data", (b) => (logs += b));
child.stderr.on("data", (b) => (logs += b));
child.on("exit", () => (closed = true));
child.on("error", (e) => {
  logs += e.message;
  closed = true;
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
async function req(url, method = "GET", body, extra = {}) {
  const response = await fetch(base + url, {
    method,
    signal: AbortSignal.timeout(10000),
    headers: {
      Cookie: cookie,
      "X-F1Hue-Request": "1",
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      ...extra,
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const set = response.headers.get("set-cookie");
  if (set) cookie = set.split(";")[0];
  const text = await response.text();
  let value;
  try {
    value = JSON.parse(text);
  } catch {
    value = text;
  }
  return { status: response.status, body: value };
}
let sse;
try {
  for (let i = 0; i < 100; i++) {
    try {
      if ((await req("/health")).status === 200) break;
    } catch {}
    if (closed || i === 99) throw Error(logs);
    await wait(100);
  }
  const code = (
    await readFile(path.join(data, "setup-code.txt"), "utf8")
  ).trim();
  await req("/api/auth/setup", "POST", {
    code,
    password: "audit-disposable-account",
  });
  const proxy = await new Promise((resolve, reject) => {
    const r = http.request(
      base + "/api/auth/status",
      {
        headers: {
          Host: "audit.example",
          Origin: "https://audit.example",
          "X-Forwarded-Proto": "https",
        },
      },
      (res) => {
        res.resume();
        resolve(res.statusCode);
      },
    );
    r.on("error", reject);
    r.end();
  });
  console.log(JSON.stringify({ probe: "https-proxy", status: proxy }));
  const fuzz = [];
  for (const [url, method, body] of [
    ["/api/hue/select", "POST", { lightIds: null, groupIds: [] }],
    ["/api/hue/select", "POST", {}],
    ["/api/hue/select", "POST", { lightIds: ["not-a-guid"], groupIds: [] }],
    ["/api/test/preview", "POST", []],
    ["/api/test/preview", "POST", null],
    ["/api/test/preview", "POST", { flag: 1 }],
    ["/api/calibration/clock", "POST", { remaining: null }],
    ["/api/settings", "PATCH", []],
    ["/api/settings", "PATCH", { effects: null }],
    ["/api/settings", "PATCH", { transitionSeconds: 1e100 }],
    ["/api/replay/start", "POST", { id: "x", kind: "archive", speed: {} }],
    ["/api/hue/scene", "POST", {}],
  ]) {
    const r = await req(url, method, body);
    fuzz.push({ url, body, status: r.status, error: r.body?.error });
  }
  console.log(JSON.stringify({ probe: "invalid-input", result: fuzz }));
  const inv = (await req("/api/hue/inventory")).body;
  await req("/api/hue/select", "POST", {
    lightIds: inv.lights.map((l) => l.id),
    groupIds: [],
  });
  await req("/api/test/preview", "POST", { flag: "SC" });
  const frames = [];
  sse = http.get(base + "/api/events", { headers: { Cookie: cookie } }, (res) =>
    res.on("data", (chunk) => {
      for (const l of chunk.toString().split("\n"))
        if (l.startsWith("data: ")) {
          try {
            const s = JSON.parse(l.slice(6));
            frames.push({
              at: Date.now(),
              running: s.runner.running,
              stopping: s.runner.stopping,
              effect: s.runner.activeEffect,
            });
          } catch {}
        }
    }),
  );
  sse.on("error", () => {});
  await wait(300);
  const start = Date.now();
  await writeFile(path.join(data, "stop.request"), "");
  while (!closed && Date.now() - start < 40000) await wait(100);
  console.log(
    JSON.stringify({
      probe: "shutdown-with-sse",
      exited: closed,
      elapsedMs: Date.now() - start,
      framesAfterStop: frames
        .filter((f) => f.at >= start)
        .map((f) => ({ ...f, atMs: f.at - start, at: undefined })),
    }),
  );
} finally {
  sse?.destroy();
  if (!closed) child.kill("SIGKILL");
  await wait(100);
  await rm(data, { recursive: true, force: true });
}
