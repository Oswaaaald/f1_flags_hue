"""Resolve a configured selection into one synchronized Hue light group."""


def prepare_sync_group(conf: dict, bridge) -> tuple[int | None, int]:
    conf.pop("_sync_group_id", None)
    conf.pop("_resolved_light_ids", None)
    if conf.get("group_id") is not None:
        return None, 0

    selected = set()
    group_ids = conf.get("group_ids") or []
    if group_ids:
        groups = bridge.groups()
        for group_id in group_ids:
            if int(group_id) == 0:
                selected.update(int(light_id) for light_id in bridge.lights())
            else:
                selected.update(int(light_id) for light_id in
                                groups.get(str(int(group_id)), {}).get("lights", []))
    else:
        selected.update(int(light_id) for light_id in conf.get("light_ids") or [])

    if not selected:
        raise RuntimeError("Aucune lampe trouvée dans la sélection Hue.")
    conf["_resolved_light_ids"] = sorted(selected)
    group_id = bridge.ensure_lightgroup("F1 Hue (sync)", sorted(selected))
    if group_id is None:
        return None, len(selected)
    conf["_sync_group_id"] = group_id
    return group_id, len(selected)
