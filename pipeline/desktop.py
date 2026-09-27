"""Desktop app dataset: full policy definitions, model answers and raw baseline entries in one gzipped file."""
from __future__ import annotations

import gzip
import json
from collections import defaultdict
from pathlib import Path
from typing import Optional

from .admx import Catalog, Element, ListItem, Policy, ValueSpec
from .baselines import PolSet
from .dataset import DATA_PATH, registry_values
from .modelio import build_options
from .preg import Entry
from .regindex import RegIndex

DESKTOP_PATH = Path("data/desktop.json.gz")
SCHEMA = 1


def _compact(d: dict) -> dict:
    # "" and 0 carry meaning (valuePrefix="", minValue=0), so only absent and false values are dropped.
    return {k: v for k, v in d.items() if v is not None and v is not False and v != [] and v != {}}


def _spec(spec: Optional[ValueSpec]) -> Optional[dict]:
    if spec is None:
        return None
    if spec.kind == "delete":
        return {"kind": "delete"}
    if spec.kind == "string":
        return {"kind": "string", "str": spec.value}
    return {"kind": spec.kind, "num": spec.value}


def _items(items: list[ListItem]) -> list[dict]:
    return [{"key": it.key, "name": it.value_name, "spec": _spec(it.spec)} for it in items]


def _element(el: Element) -> dict:
    return _compact({
        "type": el.type, "id": el.id, "key": el.key, "value_name": el.value_name, "label": el.label,
        "required": el.required,
        "items": [_compact({"display": it.display, "spec": _spec(it.spec), "value_list": _items(it.value_list)})
                  for it in el.items],
        "true_spec": _spec(el.true_spec), "false_spec": _spec(el.false_spec),
        "true_list": _items(el.true_list), "false_list": _items(el.false_list),
        "min_value": el.min_value, "max_value": el.max_value, "store_as_text": el.store_as_text,
        "max_length": el.max_length, "max_strings": el.max_strings, "expandable": el.expandable,
        "value_prefix": el.value_prefix, "additive": el.additive, "explicit_value": el.explicit_value,
        "ui": el.ui,
    })


def _entry(e: Entry) -> dict:
    # Raw bytes keep every registry type lossless; the app decodes them the way preg.decode_data does.
    return {"key": e.key, "name": e.value_name, "type": e.reg_type, "hex": e.raw.hex()}


def baseline_entries(sets: list[PolSet], index: RegIndex) -> dict[tuple[str, str, str, str], list[Entry]]:
    """(uid, baseline, gpo, scope) -> the registry.pol entries that mapped to the policy, in file order."""
    out: dict[tuple[str, str, str, str], list[Entry]] = defaultdict(list)
    for ps in sets:
        for e in ps.entries:
            for h in index.resolve(ps.scope, e):
                bucket = out[(h.uid, ps.baseline, ps.gpo, ps.scope)]
                # One entry can reach the same policy through two roles.
                if not bucket or bucket[-1] is not e:
                    bucket.append(e)
    return out


def _row(pol: Policy, applied: list[dict], entries: dict, ml: Optional[dict]) -> dict:
    def baseline(name: str) -> list[dict]:
        return [
            {**{k: v for k, v in a.items() if k != "baseline"},
             "entries": [_entry(e) for e in entries[(pol.uid, name, a["gpo"], a["scope"])]]}
            for a in applied if a["baseline"] == name
        ]

    flags = sorted({f for a in applied for f in a["flags"]})
    if ml is None:
        flags.append("ml_missing")
    return {
        "uid": pol.uid,
        "name": pol.display_name,
        "internal_name": pol.name,
        "admx": pol.admx,
        "source": pol.source,
        "class": pol.cls,
        "category_path": " > ".join(pol.category_path),
        "supported_on": pol.supported_on,
        "explanation": pol.explain,
        "registry_key": pol.key,
        "value_name": pol.value_name,
        "registry_values": registry_values(pol),
        "options": build_options(pol),
        "enabled_value": _spec(pol.enabled_value),
        "disabled_value": _spec(pol.disabled_value),
        "enabled_list": _items(pol.enabled_list),
        "disabled_list": _items(pol.disabled_list),
        "elements": [_element(el) for el in pol.elements],
        "pres": pol.pres,
        "client_extension": pol.client_extension,
        "ml": ml,
        "rtlfb": baseline("rtlfb"),
        "sb": baseline("sb"),
        "flags": flags,
    }


def build_desktop(catalog: Catalog, per_policy: dict[str, list[dict]], sets: list[PolSet], index: RegIndex) -> dict:
    if not DATA_PATH.exists():
        raise SystemExit(f"desktop: {DATA_PATH} is missing; run the dataset stage first")
    # Model answers come from the published dataset so this stage needs neither the cache nor the model SDK.
    site = json.loads(DATA_PATH.read_text(encoding="utf-8"))
    ml = {r["uid"]: r["ml"] for r in site["policies"]}
    missing = sorted(set(catalog.policies) - set(ml))
    if missing:
        raise SystemExit(f"desktop: {len(missing)} policies are not in {DATA_PATH} (first: {missing[0]}); rerun the dataset stage")
    entries = baseline_entries(sets, index)
    rows = [_row(catalog.policies[uid], per_policy.get(uid, []), entries, ml[uid]) for uid in sorted(catalog.policies)]
    return {"schema": SCHEMA, "meta": site["meta"], "policies": rows}


def write_desktop(dataset: dict) -> None:
    DESKTOP_PATH.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(dataset, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    # mtime=0 keeps the file byte-identical across runs over the same inputs.
    with DESKTOP_PATH.open("wb") as f, gzip.GzipFile(filename="", mode="wb", fileobj=f, mtime=0) as gz:
        gz.write(payload)
