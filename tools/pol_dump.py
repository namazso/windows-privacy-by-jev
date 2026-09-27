#!/usr/bin/env python3
"""Print the entries of a registry.pol file as JSON. Read-only; used as a test oracle for the desktop app."""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from pipeline.preg import parse_pol  # noqa: E402


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: pol_dump.py <registry.pol>", file=sys.stderr)
        return 2
    entries = parse_pol(sys.argv[1])
    json.dump(
        [{"key": e.key, "name": e.value_name, "type": e.reg_type, "hex": e.raw.hex(), "marker": e.marker,
          "rendered": e.render()} for e in entries],
        sys.stdout, ensure_ascii=False, indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
