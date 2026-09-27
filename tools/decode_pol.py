#!/usr/bin/env python3
"""Decode a registry.pol file into per-policy states as JSON. Read-only; used as a test oracle for the desktop app."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from pipeline.admx import load_catalog  # noqa: E402
from pipeline.baselines import PolSet, apply_baselines  # noqa: E402
from pipeline.preg import parse_pol  # noqa: E402
from pipeline.regindex import RegIndex  # noqa: E402


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("pol", help="path to a registry.pol file")
    ap.add_argument("scope", choices=["Machine", "User"])
    ap.add_argument("--base", default=str(ROOT), help="folder holding PolicyDefinitions and the two baselines")
    args = ap.parse_args()

    catalog = load_catalog(Path(args.base))
    path = Path(args.pol)
    sets = [PolSet("local", "local", args.scope, path, parse_pol(path))]
    per_policy, unmapped, _ = apply_baselines(sets, RegIndex(catalog), catalog)
    json.dump(
        {"policies": {uid: [{k: v for k, v in a.items() if k not in ("baseline", "gpo")} for a in applied]
                      for uid, applied in sorted(per_policy.items())},
         "unmapped": unmapped},
        sys.stdout, ensure_ascii=False, indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
