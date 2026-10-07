import requests
import platform
import re
import socket
import subprocess

class HueBridgeConnectionError(RuntimeError):
    pass

class HueBridge:
    def __init__(self, ip: str, username: str, timeout=0.6):
        self.ip = ip; self.base = f"http://{ip}/api"; self.username = username; self.timeout = timeout
        self.session = requests.Session()
        self.session.trust_env = False  # Ne jamais envoyer la clé Hue à un proxy HTTP ambiant.
        self.session.mount("http://",
            requests.adapters.HTTPAdapter(pool_connections=8, pool_maxsize=16, max_retries=1))

    def close(self):
        try:
            self.session.close()
        except Exception:
            pass

    def _request(self, method: str, url: str, **kwargs):
        try:
            r = self.session.request(method, url, timeout=self.timeout, **kwargs)
            r.raise_for_status()
        except requests.RequestException as e:
            status = getattr(getattr(e, "response", None), "status_code", None)
            detail = f"HTTP {status}" if status else type(e).__name__
            raise HueBridgeConnectionError(f"Pont Hue {self.ip} : requête {method} échouée ({detail}).") from None
        return r

    def register(self, devicetype="f1-hue#raspi") -> str:
        r = self._request("POST", self.base, json={"devicetype": devicetype})
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
        raise HueBridgeConnectionError("Aucune lampe Hue sélectionnée.")

    def set_state(self, data: dict, light_ids=None, group_id=None):
        for url in self._targets(light_ids, group_id):
            self._check_write(self._request("PUT", url, json=data))

    # Par lampe
    def set_light_state(self, light_id: int, data: dict):
        url = f"{self.base}/{self.username}/lights/{int(light_id)}/state"
        self._check_write(self._request("PUT", url, json=data))

    def _check_write(self, response):
        data = response.json()
        if isinstance(data, list):
            for item in data:
                if isinstance(item, dict) and "error" in item:
                    raise HueBridgeConnectionError(f"Réponse du pont Hue : {item['error'].get('description', 'erreur API')}")
        return data

    def stop_alert(self, light_ids=None, group_id=None):
        self.set_state({"alert":"none"}, light_ids, group_id)

    def lights(self) -> dict:
        url = f"{self.base}/{self.username}/lights"
        r = self._request("GET", url)
        data = r.json()
        if isinstance(data, list) and data and "error" in data[0]:
            raise HueBridgeConnectionError(f"Réponse du pont Hue : {data[0]['error'].get('description', 'erreur API')}")
        if not isinstance(data, dict):
            raise HueBridgeConnectionError("Réponse inattendue du pont Hue lors de la lecture des lampes.")
        return data

    def groups(self) -> dict:
        url = f"{self.base}/{self.username}/groups"
        data = self._request("GET", url).json()
        if isinstance(data, list) and data and "error" in data[0]:
            raise HueBridgeConnectionError(f"Réponse du pont Hue : {data[0]['error'].get('description', 'erreur API')}")
        if not isinstance(data, dict):
            raise HueBridgeConnectionError("Réponse inattendue du pont Hue lors de la lecture des zones.")
        return data

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
                        self._request("PUT", url, json={"name": name, "lights": glist})
                    return int(gid)

            # sinon, crée le groupe
            url = f"{self.base}/{self.username}/groups"
            payload = {"name": name, "type": "LightGroup", "lights": glist}
            r = self._request("POST", url, json=payload)
            resp = r.json()
            # format Hue v1: [{"success":{"id":"7"}}]
            if isinstance(resp, list) and resp and "success" in resp[0]:
                new_id = resp[0]["success"].get("id")
                return int(new_id) if new_id is not None else None
        except Exception:
            return None
        return None

def _discover_bridge_ip_mdns() -> str | None:
    """Use macOS Bonjour's local Hue service when available."""
    if platform.system() != "Darwin":
        return None

    def dns_sd_output(*args):
        try:
            subprocess.run(["dns-sd", *args], capture_output=True, timeout=1.2, check=False)
        except subprocess.TimeoutExpired as e:
            return (e.stdout or b"").decode("utf-8", errors="replace")
        return ""

    browse = dns_sd_output("-B", "_hue._tcp", "local.")
    service = re.search(r"\s+_hue\._tcp\.\s+(.+)$", browse, re.MULTILINE)
    if not service:
        return None
    lookup = dns_sd_output("-L", service.group(1).strip(), "_hue._tcp", "local.")
    host = re.search(r"can be reached at ([^\s:]+):\d+", lookup)
    if not host:
        return None
    return socket.gethostbyname(host.group(1).rstrip("."))


def discover_bridge_ip() -> str | None:
    try:
        ip = _discover_bridge_ip_mdns()
        if ip:
            return ip
    except Exception:
        pass
    try:
        r = requests.get("https://discovery.meethue.com/", timeout=2.5); r.raise_for_status()
        arr = r.json()
        if isinstance(arr, list) and arr:
            return arr[0].get("internalipaddress")
    except Exception:
        pass
    return None


def connect_bridge(conf: dict) -> tuple[HueBridge, bool]:
    """Validate the configured Hue bridge; try discovery before giving up."""
    configured_ip = conf.get("bridge_ip")
    username = conf.get("username")
    if not configured_ip or not username:
        raise HueBridgeConnectionError("Pont Hue non configuré : bridge_ip ou username manquant.")

    def probe(ip):
        bridge = HueBridge(ip, username, timeout=2)
        try:
            bridge.lights()
            return bridge
        except (requests.RequestException, HueBridgeConnectionError, ValueError):
            bridge.close()
            return None

    bridge = probe(configured_ip)
    if bridge:
        return bridge, False

    discovered_ip = discover_bridge_ip()
    if discovered_ip and discovered_ip != configured_ip:
        bridge = probe(discovered_ip)
        if bridge:
            conf["bridge_ip"] = discovered_ip
            return bridge, True
        raise HueBridgeConnectionError(
            f"Pont Hue inaccessible à {configured_ip}. La découverte propose {discovered_ip}, "
            "mais il ne répond pas non plus. Vérifie son alimentation et sa connexion au réseau local."
        )
    raise HueBridgeConnectionError(
        f"Pont Hue inaccessible à {configured_ip}. Vérifie son adresse IP, son alimentation "
        "et sa connexion au même réseau local."
    )
