# config.py
import os
import sys
import shutil
import yaml

# 100% relatif au repo
APP_DIR   = "."
CONF_PATH = "config.yml"
EXAMPLES  = ["config.example.yml", "config.yml.example", "config.sample.yml"]


def _load_yaml(path: str) -> dict:
    with open(path, "r") as f:
        return yaml.safe_load(f) or {}


def _normalize_conf_in_place(conf: dict) -> None:
    """
    Normalise uniquement ce qui peut poser problème sans imposer de valeurs par défaut.
    - group_id: '' ou 'null' -> None ; '0' -> 0 ; nombres -> int
    - group_ids / light_ids: éléments '1','2' -> int(…)
    """
    # group_id
    gid = conf.get("group_id", None)
    if isinstance(gid, str):
        s = gid.strip().lower()
        if s in ("", "null", "none"):
            gid = None
        elif s.isdigit():
            gid = int(s)
        else:
            gid = None
    conf["group_id"] = gid

    # group_ids
    gids = conf.get("group_ids")
    if isinstance(gids, list):
        norm = []
        for x in gids:
            try:
                norm.append(int(x))
            except Exception:
                # on ignore les entrées non convertibles
                pass
        conf["group_ids"] = norm

    # light_ids
    lids = conf.get("light_ids")
    if isinstance(lids, list):
        norm = []
        for x in lids:
            try:
                norm.append(int(x))
            except Exception:
                pass
        conf["light_ids"] = norm


def ensure_conf() -> dict:
    """
    Charge config.yml s'il existe.
    Sinon, tente de copier un fichier d'exemple (config.example.yml, …) vers config.yml.
    Si aucun exemple n'est présent, on arrête avec une erreur explicite.
    """
    os.makedirs(APP_DIR, exist_ok=True)

    # 1) config.yml présent → on le lit tel quel
    if os.path.exists(CONF_PATH):
        os.chmod(CONF_PATH, 0o600)
        conf = _load_yaml(CONF_PATH)
        _normalize_conf_in_place(conf)
        return conf

    # 2) pas de config.yml → on cherche un exemple à copier (copie "brute" pour garder les commentaires)
    for ex in EXAMPLES:
        if os.path.exists(ex):
            shutil.copyfile(ex, CONF_PATH)
            os.chmod(CONF_PATH, 0o600)
            print(f"[CONF] Copié {ex} -> {CONF_PATH}")
            conf = _load_yaml(CONF_PATH)
            _normalize_conf_in_place(conf)
            return conf

    # 3) aucun exemple → on échoue proprement
    msg = (
        "❌ Aucun 'config.yml' trouvé et aucun fichier d’exemple présent.\n"
        "   Crée d’abord un fichier de conf à partir d’un exemple (ex: config.example.yml),\n"
        "   ou lance:  make init-config"
    )
    print(msg, file=sys.stderr)
    raise SystemExit(1)


def save_conf(conf: dict):
    with open(CONF_PATH, "w") as f:
        yaml.safe_dump(conf, f, sort_keys=False)
    os.chmod(CONF_PATH, 0o600)


def selection_missing(conf: dict) -> bool:
    """
    Renvoie True si aucune sélection explicite n'est présente (ni group_id, ni group_ids, ni light_ids).
    """
    gid = conf.get("group_id", None)
    # (au cas où l'appelant n'a pas encore normalisé)
    if isinstance(gid, str):
        s = gid.strip().lower()
        if s in ("", "null", "none"):
            gid = None
        elif s.isdigit():
            gid = int(s)
        else:
            gid = None

    gids = conf.get("group_ids") or []
    lids = conf.get("light_ids") or []
    return (gid is None and not gids and not lids)
