"""Merge the catalogue, baseline values and model answers into data/policies.json and site/data.js."""
from __future__ import annotations

import datetime as dt
import json
from pathlib import Path

from .admx import Catalog, Policy
from .modelio import MODEL, build_options, questions_sha

DATA_PATH = Path("data/policies.json")
SITE_DATA_PATH = Path("site/data.js")

_HIVE = {"Machine": "HKLM", "User": "HKCU", "Both": "HKLM/HKCU"}


def registry_values(pol: Policy) -> list[str]:
    hive = _HIVE[pol.cls]
    out: list[str] = []

    def add(key: str, name: str | None) -> None:
        text = f"{hive}\\{key}!{name}" if name else f"{hive}\\{key}\\*"
        if text not in out:
            out.append(text)

    if pol.value_name:
        add(pol.key, pol.value_name)
    for it in pol.enabled_list + pol.disabled_list:
        add(it.key, it.value_name)
    for el in pol.elements:
        add(el.key, None if el.type == "list" else el.value_name)
    return out


def ml_fields(rec: dict | None) -> dict | None:
    if not rec:
        return None
    a = rec["answers"]
    return {
        "privacy": round(a["privacy"]["noul"], 3),
        "security": round(a["security"]["noul"], 3),
        "ux_degrade": round(a["ux_degrade"]["noul"], 3),
        "privacy_dir": a["privacy_direction"]["choice"],
        "privacy_dir_conf": round(a["privacy_direction"]["confidence"], 3),
        "security_dir": a["security_direction"]["choice"],
        "security_dir_conf": round(a["security_direction"]["confidence"], 3),
    }


def build_rows(catalog: Catalog, per_policy: dict[str, list[dict]], results: dict[str, dict]) -> list[dict]:
    rows = []
    for uid in sorted(catalog.policies):
        pol = catalog.policies[uid]
        applied = per_policy.get(uid, [])
        rtlfb = [a for a in applied if a["baseline"] == "rtlfb"]
        sb = [a for a in applied if a["baseline"] == "sb"]
        flags = sorted({f for a in applied for f in a["flags"]})
        ml = ml_fields(results.get(uid))
        if ml is None:
            flags.append("ml_missing")

        def strip(a: dict) -> dict:
            return {k: v for k, v in a.items() if k != "baseline"}

        rows.append({
            "uid": uid,
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
            "ml": ml,
            "rtlfb": [strip(a) for a in rtlfb],
            "sb": [strip(a) for a in sb],
            "flags": flags,
        })
    return rows


def build_dataset(catalog: Catalog, per_policy, results, stats: dict) -> dict:
    rows = build_rows(catalog, per_policy, results)
    by_source: dict[str, int] = {}
    for r in rows:
        by_source[r["source"]] = by_source.get(r["source"], 0) + 1
    meta = {
        "generated": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"),
        "model": MODEL,
        "questions_sha": questions_sha(),
        "counts": {
            "policies": len(rows),
            "by_source": by_source,
            "with_ml": sum(1 for r in rows if r["ml"]),
            "in_rtlfb": sum(1 for r in rows if r["rtlfb"]),
            "in_sb": sum(1 for r in rows if r["sb"]),
            "rtlfb_entries_mapped": list(stats.get("rtlfb", (0, 0))),
            "sb_entries_mapped": list(stats.get("sb", (0, 0))),
        },
    }
    return {"meta": meta, "policies": rows}


def write_dataset(dataset: dict) -> None:
    DATA_PATH.parent.mkdir(parents=True, exist_ok=True)
    DATA_PATH.write_text(json.dumps(dataset, ensure_ascii=False, indent=1), encoding="utf-8")
    SITE_DATA_PATH.parent.mkdir(parents=True, exist_ok=True)
    compact = json.dumps(dataset, ensure_ascii=False, separators=(",", ":"))
    # `</script>` inside explanation text would end the script tag early.
    compact = compact.replace("</", "<\\/")
    SITE_DATA_PATH.write_text("window.GPO_DATA = " + compact + ";\n", encoding="utf-8")
