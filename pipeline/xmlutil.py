"""Namespace-agnostic XML loading for ADMX/ADML/gpreport files."""
from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from pathlib import Path

_XML_DECL = re.compile(r"^\s*<\?xml[^>]*\?>", re.S)
_HSPACE = re.compile(r"[ \t\f\v]+")


def read_text(path: Path | str) -> str:
    # Files mix UTF-8 with/without BOM and UTF-16LE with BOM; sniff instead of trusting the declaration.
    data = Path(path).read_bytes()
    if data[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return data.decode("utf-16")
    if data[:3] == b"\xef\xbb\xbf":
        return data.decode("utf-8-sig")
    return data.decode("utf-8")


def load_xml(path: Path | str) -> ET.Element:
    # Search.admx declares encoding='unicode', which ElementTree rejects, so the declaration is dropped.
    return ET.fromstring(_XML_DECL.sub("", read_text(path), count=1))


def local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def children(el: ET.Element | None, name: str) -> list[ET.Element]:
    if el is None:
        return []
    return [c for c in el if local(c.tag) == name]


def child(el: ET.Element | None, name: str) -> ET.Element | None:
    for c in children(el, name):
        return c
    return None


def iter_local(root: ET.Element, name: str):
    for el in root.iter():
        if local(el.tag) == name:
            yield el


def normalize_text(text: str | None) -> str:
    """Collapse ADML indentation and CRLF while keeping line and paragraph breaks."""
    if not text:
        return ""
    lines = [_HSPACE.sub(" ", ln).strip() for ln in text.replace("\r\n", "\n").replace("\r", "\n").split("\n")]
    out: list[str] = []
    for ln in lines:
        if ln:
            out.append(ln)
        elif out and out[-1] != "":
            out.append("")
    while out and out[-1] == "":
        out.pop()
    return "\n".join(out)
