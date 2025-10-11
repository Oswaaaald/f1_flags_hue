import requests

class HueBridge:
    def __init__(self, ip: str, username: str, timeout=0.6):
        self.base = f"http://{ip}/api"; self.username = username; self.timeout = timeout
        self.session = requests.Session()
        self.session.mount("http://",
            requests.adapters.HTTPAdapter(pool_connections=8, pool_maxsize=16, max_retries=1))

    def register(self, devicetype="f1-hue#raspi") -> str:
        r = self.session.post(self.base, json={"devicetype": devicetype}, timeout=self.timeout); r.raise_for_status()
        resp = r.json()
        if isinstance(resp,list) and "success" in resp[0]:
            self.username = resp[0]["success"]["username"]; return self.username
        if isinstance(resp,list) and "error" in resp[0]:
            raise RuntimeError(resp[0]["error"]["description"])
        raise RuntimeError("Unexpected Hue response")

    def _targets(self, light_ids, group_id):
        if group_id is not None:
            return [f"{self.base}/{self.username}/groups/{group_id}/action"]
        if light_ids:
            return [f"{self.base}/{self.username}/lights/{lid}/state" for lid in light_ids]
        return [f"{self.base}/{self.username}/groups/0/action"]

    def set_state(self, data: dict, light_ids=None, group_id=None):
        for url in self._targets(light_ids, group_id):
            self.session.put(url, json=data, timeout=self.timeout)

    # Par lampe
    def set_light_state(self, light_id: int, data: dict):
        url = f"{self.base}/{self.username}/lights/{int(light_id)}/state"
        self.session.put(url, json=data, timeout=self.timeout)

    def stop_alert(self, light_ids=None, group_id=None):
        self.set_state({"alert":"none"}, light_ids, group_id)

    def lights(self) -> dict:
        url = f"{self.base}/{self.username}/lights"
        r = self.session.get(url, timeout=self.timeout); r.raise_for_status(); return r.json()

    def groups(self) -> dict:
        url = f"{self.base}/{self.username}/groups"
        r = self.session.get(url, timeout=self.timeout); r.raise_for_status(); return r.json()

    # --------- NEW: groupe "virtuel" pour synchroniser des light_ids ---------

    def ensure_lightgroup(self, name: str, lights: list[int]) -> int | None:
        """
        Crée (ou met à jour) un groupe type LightGroup avec cette liste de lampes.
        Renvoie son id, ou None si impossible.
        """
        try:
            glist = [str(int(x)) for x in lights]
            groups = self.groups()
            # cherche un groupe existant au même nom
            for gid, g in groups.items():
                if (g.get("type") == "LightGroup") and (g.get("name") == name):
                    current = sorted(g.get("lights", []))
                    if sorted(glist) != current:
                        # met à jour la composition
                        url = f"{self.base}/{self.username}/groups/{gid}"
                        self.session.put(url, json={"name": name, "lights": glist}, timeout=self.timeout)
                    return int(gid)

            # sinon, crée le groupe
            url = f"{self.base}/{self.username}/groups"
            payload = {"name": name, "type": "LightGroup", "lights": glist}
            r = self.session.post(url, json=payload, timeout=self.timeout); r.raise_for_status()
            resp = r.json()
            # format Hue v1: [{"success":{"id":"7"}}]
            if isinstance(resp, list) and resp and "success" in resp[0]:
                new_id = resp[0]["success"].get("id")
                return int(new_id) if new_id is not None else None
        except Exception:
            return None
        return None

def discover_bridge_ip() -> str | None:
    try:
        r = requests.get("https://discovery.meethue.com/", timeout=2.5); r.raise_for_status()
        arr = r.json()
        if isinstance(arr, list) and arr:
            return arr[0].get("internalipaddress")
    except Exception:
        pass
    return None
