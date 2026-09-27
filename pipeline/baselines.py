"""Load the RTLFB and Security Baseline registry.pol sets and attach configured values to policies."""
from __future__ import annotations

from collections import defaultdict
from dataclasses import asdict, dataclass
from pathlib import Path

from .admx import Catalog
from .preg import Entry, parse_pol
from .regindex import PRIMARY_ROLES, Hit, RegIndex, derive_state
from .xmlutil import iter_local, load_xml

RTLFB_DIR = "Restricted Traffic Limited Functionality Baseline"
SB_DIR = "Security Baseline"


@dataclass
class PolSet:
    baseline: str  # rtlfb | sb
    gpo: str
    scope: str  # Machine | User
    path: Path
    entries: list[Entry]


def load_rtlfb(base: Path) -> list[PolSet]:
    root = base / RTLFB_DIR / "GPO"
    out = []
    for scope in ("Machine", "User"):
        path = root / scope / "registry.pol"
        if path.exists():
            out.append(PolSet("rtlfb", "RTLFB", scope, path, parse_pol(path)))
    return out


def _gpo_name(folder: Path) -> str:
    info = folder / "bkupInfo.xml"
    if info.exists():
        for el in iter_local(load_xml(info), "GPODisplayName"):
            if el.text and el.text.strip():
                return el.text.strip()
    return folder.name


def load_sb(base: Path) -> list[PolSet]:
    root = base / SB_DIR / "GPOs"
    out = []
    for folder in sorted(p for p in root.iterdir() if p.is_dir()):
        name = _gpo_name(folder)
        for scope in ("Machine", "User"):
            path = folder / "DomainSysvol" / "GPO" / scope / "registry.pol"
            if path.exists():
                out.append(PolSet("sb", name, scope, path, parse_pol(path)))
    return sorted(out, key=lambda s: (s.gpo, s.scope))


def apply_baselines(sets: list[PolSet], index: RegIndex, catalog: Catalog):
    """Returns (per_policy: uid -> [applied dicts], unmapped: [dicts], stats: baseline -> (mapped, total))."""
    per_policy: dict[str, list[dict]] = defaultdict(list)
    unmapped: list[dict] = []
    stats: dict[str, list[int]] = defaultdict(lambda: [0, 0])
    for ps in sets:
        by_uid: dict[str, list[tuple[Entry, Hit]]] = defaultdict(list)
        for e in ps.entries:
            stats[ps.baseline][1] += 1
            hits = index.resolve(ps.scope, e)
            if not hits:
                unmapped.append({"baseline": ps.baseline, "gpo": ps.gpo, "scope": ps.scope,
                                 "key": e.key, "value": e.value_name, "type": e.type_name, "data": e.render()})
                continue
            stats[ps.baseline][0] += 1
            for h in hits:
                by_uid[h.uid].append((e, h))
        for uid, pairs in by_uid.items():
            st = derive_state(catalog.policies[uid], pairs, ps.scope)
            # Flag only values that two policies own outright; catch-all list sharing is routine.
            shared = any(len({h.uid for h in hits}) > 1 and all(h.role in PRIMARY_ROLES for h in hits)
                         for hits in (index.resolve(ps.scope, e) for e, _ in pairs))
            entry = {"baseline": ps.baseline, "gpo": ps.gpo, **asdict(st)}
            if shared and "shared_value" not in entry["flags"]:
                entry["flags"].append("shared_value")
            per_policy[uid].append(entry)
    return per_policy, unmapped, {k: tuple(v) for k, v in stats.items()}
