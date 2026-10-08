import { test as base, expect, type Page } from "@playwright/test";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdtemp, readFile, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import net from "node:net";

type Service = { url: string; open(page: Page): Promise<void>; post(page: Page, route: string, body?: unknown): Promise<void> };
export const test = base.extend<{ service: Service }>({
  service: async ({}, use) => {
    const root = path.resolve(import.meta.dirname, "../../..");
    const directory = await mkdtemp(path.join(tmpdir(), "f1hue-browser-"));
    const socket = net.createServer(); socket.listen(0, "127.0.0.1"); await once(socket, "listening");
    const port = (socket.address() as net.AddressInfo).port; await new Promise<void>(resolve => socket.close(() => resolve()));
    const child = spawn("dotnet", [path.join(root, "apps/host/bin/Release/net10.0/f1-hue.dll"), "--desktop", "--simulate", "--no-feed", "--data", directory, "--port", String(port)], { stdio: ["ignore", "pipe", "pipe"] });
    const exited = once(child, "exit"); let logs = "";
    child.stdout.on("data", b => logs += b); child.stderr.on("data", b => logs += b);
    const url = `http://127.0.0.1:${port}`;
    try {
      for (let attempt = 0; ; attempt++) {
        try { if ((await fetch(url + "/health")).ok) break; } catch {}
        if (attempt > 100 || child.exitCode !== null) throw new Error(logs || "Test service did not start");
        await new Promise(resolve => setTimeout(resolve, 100));
      }
      await use({ url,
        async open(page) {
          const key = (await readFile(path.join(directory, "desktop-launch.key"), "utf8")).trim();
          const response = await fetch(url + "/api/auth/desktop/ticket", { method: "POST", headers: { "X-F1Hue-Request": "1", "X-F1Hue-Launcher": key } });
          const { ticket } = await response.json();
          await page.goto(url + "/#desktop=" + ticket);
          await expect(page.locator("#shell")).toBeVisible();
        },
        async post(page, route, body) {
          const response = await page.request.post(url + "/api" + route, { data: body, headers: { "X-F1Hue-Request": "1" } });
          expect(response.ok(), await response.text()).toBeTruthy();
        },
      });
    } finally {
      if (child.exitCode === null) { await writeFile(path.join(directory, "stop.request"), "stop"); await Promise.race([exited, new Promise(r => setTimeout(r, 5000))]); }
      if (child.exitCode === null) { child.kill("SIGKILL"); await exited; }
      await rm(directory, { recursive: true, force: true });
    }
  },
});
export { expect };
