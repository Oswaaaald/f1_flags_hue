#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import argparse, json, time, os
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs
from datetime import datetime, timedelta, timezone
from typing import List, Dict, Any
import yaml

def load_script(path: str) -> Dict[str, Any]:
    if not os.path.exists(path):
        raise SystemExit(f"❌ Script introuvable: {path}")
    with open(path, "r") as f:
        data = yaml.safe_load(f) or {}
    events = data.get("events") or []
    norm = []
    for i, e in enumerate(events):
        offset = float(e.get("offset", i * 10.0))
        cat    = str(e.get("category", "Flag"))
        msg    = str(e.get("message", ""))
        flag   = e.get("flag")
        if flag is not None:
            flag = str(flag)
        # pas de normalisation ici: le client s’en charge (ex. DOUBLE YELLOW -> YELLOW)
        norm.append({"offset": offset, "category": cat, "message": msg, "flag": flag})
    norm.sort(key=lambda x: x["offset"])
    total = norm[-1]["offset"] if norm else 0.0
    return {"events": norm, "total_offset": total}

class MockState:
    def __init__(self, script: Dict[str,Any], speed: float):
        self.script = script
        self.speed = max(0.01, float(speed))
        self.start_mono = time.monotonic()
        self.start_wall = datetime.now(timezone.utc)

    def reset(self):
        self.start_mono = time.monotonic()
        self.start_wall = datetime.now(timezone.utc)

    def rows_until_now(self, loop: bool=False) -> List[Dict[str,Any]]:
        evts = self.script["events"]
        if not evts:
            return []
        total = float(self.script["total_offset"])
        elapsed = (time.monotonic() - self.start_mono) * self.speed
        rows: List[Dict[str,Any]] = []

        if not loop or total <= 0:
            for e in evts:
                if e["offset"] <= elapsed:
                    dt = self.start_wall + timedelta(seconds=e["offset"]/self.speed)
                    rows.append({
                        "date": dt.isoformat().replace("+00:00","Z"),
                        "category": e["category"],
                        "message": e["message"],
                        "flag": e["flag"]
                    })
            return rows

        loops = int(elapsed // total)
        rem   = elapsed - loops * total
        for k in range(loops+1):
            cutoff = total if k < loops else rem
            for e in evts:
                if e["offset"] <= cutoff:
                    tsec = (k * total + e["offset"]) / self.speed
                    dt = self.start_wall + timedelta(seconds=tsec)
                    rows.append({
                        "date": dt.isoformat().replace("+00:00","Z"),
                        "category": e["category"],
                        "message": e["message"],
                        "flag": e["flag"]
                    })
        return rows

class Handler(BaseHTTPRequestHandler):
    state: MockState = None
    allow_loop: bool = False

    def _json(self, obj, code=200):
        data = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        u = urlparse(self.path)
        if u.path == "/v1/race_control":
            qs = parse_qs(u.query or "")
            if "reset" in qs:
                self.state.reset()
            rows = self.state.rows_until_now(loop=self.allow_loop)
            return self._json(rows)
        elif u.path == "/health":
            return self._json({"ok": True, "now": datetime.now(timezone.utc).isoformat()})
        else:
            self.send_error(404, "Not found")

def main():
    ap = argparse.ArgumentParser(description="Mock OpenF1 /v1/race_control")
    ap.add_argument("--port", type=int, default=8008)
    ap.add_argument("--script", type=str, default="mock_scripts/demo.yml")
    ap.add_argument("--speed", type=float, default=1.0, help=">1.0 = plus rapide (ex 2.0 : 2x)")
    ap.add_argument("--loop", action="store_true", help="rejouer en boucle le script (timestamps croissants)")
    args = ap.parse_args()

    script = load_script(args.script)
    state = MockState(script, speed=args.speed)

    Handler.state = state
    Handler.allow_loop = args.loop

    httpd = ThreadingHTTPServer(("0.0.0.0", args.port), Handler)
    print(f"✅ Mock OpenF1 prêt sur http://127.0.0.1:{args.port}")
    print(f"   • GET /v1/race_control   (ajoute '?reset=1' pour recommencer)")
    print(f"   • GET /health")
    print(f"   • Script: {args.script}  | speed: {args.speed}  | loop: {args.loop}")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n[INFO] Arrêt serveur mock…")
    finally:
        httpd.server_close()

if __name__ == "__main__":
    main()
