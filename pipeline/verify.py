"""Sanity checks: counts, coverage, spot checks, gpreport and spreadsheet cross-checks, score histograms."""
from __future__ import annotations

import re
from collections import Counter, defaultdict
from pathlib import Path

from .admx import Catalog
from .baselines import SB_DIR
from .regindex import RegIndex
from .xmlutil import child, load_xml, local

GPREPORT_NS = "http://www.microsoft.com/GroupPolicy/Settings/Registry"
EXPECTED_WINDOWS = {"total": 3580, "Machine": 1717, "User": 794, "Both": 1069}
COVERAGE_FLOOR = {"rtlfb": 114, "sb": 316}
ALLOWED_UNMAPPED = re.compile(
    r"(\\Edge\b|\\EdgeUpdate|\\WindowsFirewall(\\|$)|W32Time|LexiconUpdate|Windows Mail|\\Dsh$|InputPersonalization)",
    re.I,
)
SPOT_CHECKS = [
    ("DataCollection:AllowTelemetry", "rtlfb", "Enabled; Allow Diagnostic Data = Diagnostic data off (not recommended)"),
    ("CloudContent:DisableWindowsConsumerFeatures", "rtlfb", "Enabled"),
    ("WindowsUpdate:CorpWuURL", "rtlfb", 'Set the intranet statistics server = " "'),
    ("WindowsDefender:SignatureUpdate_DefinitionUpdateFileSharesSources", "rtlfb", "Disabled"),
    ("WindowsDefender:SpynetReporting", "rtlfb", "Enabled; Join Microsoft MAPS = Disabled"),
    ("SecGuide:Pol_SecGuide_0002_SMBv1_ClientDriver", "sb", "Enabled; Configure MrxSmb10 driver = Disable driver (recommended)"),
    ("PowerShellExecutionPolicy:EnableScriptBlockLogging", "sb", "Log script block invocation start / stop events = No"),
    ("DeviceInstallation:DeviceInstall_Classes_Deny", "sb", "(replace existing) [{d48179be-ec20-11d1-b6b8-00c04fa372a7}]"),
    ("WindowsExplorer:EnableSmartScreen", "sb", "Warn and prevent bypass"),
    ("SmartScreen:ShellConfigureSmartScreen", "sb", "Warn and prevent bypass"),
    ("WindowsDefender:RealtimeProtection_RealtimeScanDirection", "sb", "bi-directional (full on-access)"),
]


class Report:
    def __init__(self):
        self.failures = 0

    def check(self, ok: bool, msg: str) -> None:
        print(("  ok   " if ok else "  FAIL ") + msg)
        if not ok:
            self.failures += 1

    def note(self, msg: str) -> None:
        print("  info " + msg)


def check_catalog(catalog: Catalog, rep: Report) -> None:
    print("catalog")
    windows = [p for p in catalog.policies.values() if p.source == "windows"]
    rep.check(len(windows) == EXPECTED_WINDOWS["total"], f"{len(windows)} stock Windows policies (expected {EXPECTED_WINDOWS['total']})")
    split = Counter(p.cls for p in windows)
    for cls in ("Machine", "User", "Both"):
        rep.check(split[cls] == EXPECTED_WINDOWS[cls], f"{cls}: {split[cls]} (expected {EXPECTED_WINDOWS[cls]})")
    rep.note("by source: " + ", ".join(f"{k}={v}" for k, v in sorted(Counter(p.source for p in catalog.policies.values()).items())))
    for name in ("Camera:", "Search:"):
        rep.check(any(u.startswith(name) for u in catalog.policies), f"UTF-16 template parsed ({name.rstrip(':')}.admx)")
    unresolved = [w for w in catalog.warnings if "unresolved" in w]
    rep.check(not unresolved, f"{len(unresolved)} unresolved string/presentation refs")
    dangling = [w for w in catalog.warnings if "dangling" in w]
    rep.check(len(dangling) <= 14, f"{len(dangling)} dangling category/supportedOn refs (13 known)")
    rep.check(all(p.display_name and p.explain and p.category_path for p in catalog.policies.values()), "every policy has name, explanation and category path")


def check_baselines(per_policy, unmapped, stats, rep: Report) -> None:
    print("baselines")
    for name, (mapped, total) in sorted(stats.items()):
        rep.check(mapped >= COVERAGE_FLOOR[name], f"{name}: {mapped}/{total} registry entries mapped (floor {COVERAGE_FLOOR[name]})")
    unexpected = [u for u in unmapped if not ALLOWED_UNMAPPED.search(u["key"])]
    rep.check(not unexpected, f"{len(unexpected)} unexpected unmapped entries")
    for u in unexpected:
        rep.note(f"    {u['baseline']} {u['key']}!{u['value']}")
    for uid, baseline, expected in SPOT_CHECKS:
        hits = [a for a in per_policy.get(uid, []) if a["baseline"] == baseline]
        ok = any(expected in a["summary"] for a in hits)
        rep.check(ok, f"{uid} [{baseline}] contains {expected!r}" + ("" if ok else f" -> {[a['summary'] for a in hits]}"))
    flags = Counter(f for v in per_policy.values() for a in v for f in a["flags"])
    rep.note("flags: " + (", ".join(f"{k}={v}" for k, v in sorted(flags.items())) or "none"))


def _gpreport_policies(path: Path) -> dict[str, set[tuple[str, str]]]:
    """(name, state) pairs per scope from a gpreport.xml."""
    root = load_xml(path)
    out: dict[str, set[tuple[str, str]]] = {"Machine": set(), "User": set()}
    for top in root:
        scope = {"Computer": "Machine", "User": "User"}.get(local(top.tag))
        if scope is None:
            continue
        for el in top.iter():
            if el.tag == f"{{{GPREPORT_NS}}}Policy":
                name, state = child(el, "Name"), child(el, "State")
                out[scope].add(((name.text or "").strip(), (state.text or "").strip()))
    return out


def check_gpreport(base: Path, catalog: Catalog, per_policy, rep: Report) -> None:
    print("gpreport cross-check")
    resolved: dict[tuple[str, str], set[tuple[str, str]]] = defaultdict(set)
    for uid, applied in per_policy.items():
        for a in applied:
            if a["baseline"] == "sb":
                resolved[(a["gpo"], a["scope"])].add((catalog.policies[uid].display_name, a["state"]))
    for folder in sorted(p for p in (base / SB_DIR / "GPOs").iterdir() if p.is_dir()):
        report = folder / "gpreport.xml"
        if not report.exists():
            continue
        name_el = next((e for e in load_xml(report).iter() if local(e.tag) == "Name"), None)
        gpo = (name_el.text or "").strip() if name_el is not None else folder.name
        expected = _gpreport_policies(report)
        for scope in ("Machine", "User"):
            exp, got = expected[scope], resolved.get((gpo, scope), set())
            if not exp and not got:
                continue
            missing, extra = exp - got, got - exp
            rep.check(not missing, f"{gpo} [{scope}]: {len(got)} resolved vs {len(exp)} in gpreport; missing {len(missing)}, extra {len(extra)}")
            for item in sorted(missing):
                rep.note(f"    gpreport only: {item}")
            for item in sorted(extra):
                rep.note(f"    resolved only: {item}")


def check_spreadsheet(base: Path, catalog: Catalog, index: RegIndex, rep: Report) -> None:
    print("spreadsheet cross-check")
    try:
        import openpyxl
    except ImportError:
        rep.note("openpyxl not installed; skipped")
        return
    path = base / SB_DIR / "Documentation" / "MS Security Baseline Windows 11 v24H2.xlsx"
    if not path.exists():
        rep.note("spreadsheet not found; skipped")
        return
    wb = openpyxl.load_workbook(path, read_only=True)
    starts_re = re.compile(r"HK(?:LM|CU)\\")

    def parse_refs(text: str) -> list[tuple[str, str, str]]:
        # Keys and value names may contain spaces, so split at each hive prefix rather than on whitespace.
        refs = []
        positions = [m.start() for m in starts_re.finditer(text)]
        for i, start in enumerate(positions):
            frag = text[start:positions[i + 1] if i + 1 < len(positions) else len(text)].strip().rstrip(";").strip()
            if "!" not in frag:
                continue
            key, value = frag[5:].rsplit("!", 1)
            refs.append((frag[2:4], key.strip(), value.strip()))
        return refs

    def norm(s: str) -> str:
        return re.sub(r"[\s,]+", " ", s or "").strip().lower()

    for sheet, scope in (("Computer", "Machine"), ("User", "User")):
        ws = wb[sheet]
        rows = ws.iter_rows(values_only=True)
        header = [str(h or "").strip() for h in next(rows)]
        col = {h: i for i, h in enumerate(header)}
        i_name, i_value, i_reg = col["Policy Setting Name"], col["Policy Value"], col["Registry Information"]
        configured = matched = 0
        misses = []
        for row in rows:
            if not row or not row[i_value]:
                continue
            configured += 1
            refs = parse_refs(str(row[i_reg] or ""))
            names = set()
            for hive, key, value in refs:
                from .preg import Entry
                fake = Entry(key, value, 4, None, b"", None)
                for hit in index.resolve("Machine" if hive == "LM" else "User", fake):
                    names.add(norm(catalog.policies[hit.uid].display_name))
            if norm(str(row[i_name])) in names:
                matched += 1
            else:
                misses.append((str(row[i_name]), refs[:2], sorted(names)[:3]))
        rep.check(matched >= configured * 0.9, f"{sheet}: {matched}/{configured} configured rows resolve to the named policy")
        for m in misses[:12]:
            rep.note(f"    {m[0]!r} refs={m[1]} resolved={m[2]}")
        if len(misses) > 12:
            rep.note(f"    ... {len(misses) - 12} more")


def check_ml(catalog: Catalog, results: dict, rep: Report) -> None:
    print("model answers")
    total = len(catalog.policies)
    rep.check(len(results) == total, f"{len(results)}/{total} policies have answers for the current question wording")
    if not results:
        return
    for q in ("privacy", "security", "ux_degrade"):
        vals = [r["answers"][q]["noul"] for r in results.values()]
        bins = Counter(min(int(v * 10), 9) for v in vals)
        hist = " ".join(f"{bins[i]:4d}" for i in range(10))
        mid = sum(1 for v in vals if 0.45 <= v <= 0.55) / len(vals)
        rep.check(mid < 0.6, f"{q:10} hist[0.0..1.0]: {hist}  (>=0.5: {sum(v >= 0.5 for v in vals)}, in 0.45-0.55: {mid:.0%})")
    for q in ("privacy_direction", "security_direction"):
        rep.note(f"{q}: " + ", ".join(f"{k}={v}" for k, v in sorted(Counter(r["answers"][q]["choice"] for r in results.values()).items())))
    # Machine/User twins share explanation text; their answers should stay close.
    by_text: dict[tuple[str, str], list[float]] = defaultdict(list)
    for uid, rec in results.items():
        pol = catalog.policies[uid]
        by_text[(pol.display_name, pol.explain)].append(rec["answers"]["privacy"]["noul"])
    spreads = [max(v) - min(v) for v in by_text.values() if len(v) > 1]
    if spreads:
        rep.note(f"{len(spreads)} twin groups; mean privacy spread {sum(spreads) / len(spreads):.3f}, max {max(spreads):.2f}")


def run(base: Path, catalog: Catalog, index: RegIndex, per_policy, unmapped, stats, results) -> int:
    rep = Report()
    check_catalog(catalog, rep)
    check_baselines(per_policy, unmapped, stats, rep)
    check_gpreport(base, catalog, per_policy, rep)
    check_spreadsheet(base, catalog, index, rep)
    check_ml(catalog, results, rep)
    print(f"verify: {rep.failures} failure(s)")
    return rep.failures
