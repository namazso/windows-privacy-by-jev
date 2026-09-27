#!/usr/bin/env python3
"""Build pipeline: parse templates, map baselines, classify with the model, emit the dataset and site data."""
from __future__ import annotations

import argparse
import json
import sys
from dataclasses import asdict
from pathlib import Path

from pipeline.admx import load_catalog
from pipeline.baselines import apply_baselines, load_rtlfb, load_sb
from pipeline.regindex import RegIndex

STAGES = ["parse", "baselines", "classify", "dataset", "desktop", "verify"]
BUILD = Path("build")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("stages", nargs="*", default=["all"], help=f"one or more of {STAGES + ['all']}")
    ap.add_argument("--base", default=".", help="folder holding PolicyDefinitions and the two baselines")
    ap.add_argument("--limit", type=int, help="classify at most N policies (smoke tests)")
    ap.add_argument("--uid", nargs="*", help="classify only these policy uids")
    ap.add_argument("--dry-run", action="store_true", help="print request bodies instead of calling the model")
    ap.add_argument("--workers", type=int, default=16, help="concurrency ceiling for model requests")
    ap.add_argument("--model", default=None, help="model alias to request (default: jev-1.13-free; jev-1.13 is the paid alias)")
    args = ap.parse_args()

    stages = set(STAGES) if "all" in args.stages else set(args.stages)
    unknown = stages - set(STAGES)
    if unknown:
        ap.error(f"unknown stage(s): {sorted(unknown)}")
    base = Path(args.base)
    BUILD.mkdir(exist_ok=True)

    catalog = load_catalog(base)
    print(f"parse: {len(catalog.policies)} policies from {len({p.admx for p in catalog.policies.values()})} templates, "
          f"{len(catalog.warnings)} warnings", file=sys.stderr)
    if "parse" in stages:
        for w in catalog.warnings:
            print("  warning: " + w, file=sys.stderr)
        (BUILD / "catalog.json").write_text(
            json.dumps({u: asdict(p) for u, p in catalog.policies.items()}, ensure_ascii=False, indent=1), encoding="utf-8")

    index = RegIndex(catalog)
    sets = load_rtlfb(base) + load_sb(base)
    per_policy, unmapped, stats = apply_baselines(sets, index, catalog)
    print("baselines: " + ", ".join(f"{k} {m}/{t} entries mapped" for k, (m, t) in sorted(stats.items())), file=sys.stderr)
    if "baselines" in stages:
        (BUILD / "baselines.json").write_text(json.dumps(per_policy, ensure_ascii=False, indent=1), encoding="utf-8")
        with (BUILD / "unmapped.txt").open("w", encoding="utf-8") as f:
            for u in unmapped:
                f.write(f"{u['baseline']}\t{u['gpo']}\t{u['scope']}\t{u['key']}!{u['value']}\t{u['type']}\t{u['data']}\n")

    results: dict = {}
    if "classify" in stages:
        from pipeline.classify import classify
        from pipeline.modelio import MODEL
        results = classify(catalog, uids=args.uid, limit=args.limit, workers=args.workers, dry_run=args.dry_run,
                           model=args.model or MODEL)
        if args.dry_run:
            return 0
    if "dataset" in stages or "verify" in stages:
        from pipeline.classify import load_results
        results = load_results(catalog)
        print(f"answers: {len(results)}/{len(catalog.policies)} policies", file=sys.stderr)
    if "dataset" in stages:
        from pipeline.dataset import DATA_PATH, SITE_DATA_PATH, build_dataset, write_dataset
        write_dataset(build_dataset(catalog, per_policy, results, stats))
        print(f"dataset: wrote {DATA_PATH} and {SITE_DATA_PATH}", file=sys.stderr)
    if "desktop" in stages:
        from pipeline.desktop import DESKTOP_PATH, build_desktop, write_desktop
        write_desktop(build_desktop(catalog, per_policy, sets, index))
        print(f"desktop: wrote {DESKTOP_PATH}", file=sys.stderr)
    if "verify" in stages:
        from pipeline import verify
        return 1 if verify.run(base, catalog, index, per_policy, unmapped, stats, results) else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
