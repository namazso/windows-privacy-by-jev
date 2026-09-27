"""Parser for Group Policy registry.pol files (PReg version 1)."""
from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path

REG_SZ, REG_EXPAND_SZ, REG_BINARY, REG_DWORD, REG_DWORD_BE, REG_MULTI_SZ, REG_QWORD = 1, 2, 3, 4, 5, 7, 11
TYPE_NAMES = {
    0: "REG_NONE", 1: "REG_SZ", 2: "REG_EXPAND_SZ", 3: "REG_BINARY", 4: "REG_DWORD",
    5: "REG_DWORD_BIG_ENDIAN", 6: "REG_LINK", 7: "REG_MULTI_SZ", 11: "REG_QWORD",
}

# Value-name prefixes with special meaning to the registry CSE (MS-GPREG 2.2.1).
_MARKERS = (
    ("**del.", "del"),
    ("**delvals.", "delvals"),
    ("**deletevalues", "deletevalues"),
    ("**deletekeys", "deletekeys"),
    ("**securekey", "securekey"),
    ("**softreboot", "softreboot"),
)


@dataclass
class Entry:
    key: str
    value_name: str
    reg_type: int
    data: object
    raw: bytes
    marker: str | None = None

    @property
    def name(self) -> str:
        """Value name the entry acts on (the target for `**del.` markers)."""
        if self.marker == "del":
            return self.value_name[len("**del."):]
        return self.value_name

    @property
    def type_name(self) -> str:
        return TYPE_NAMES.get(self.reg_type, f"REG_{self.reg_type}")

    def render(self) -> str:
        if self.marker:
            return self.value_name
        if isinstance(self.data, list):
            return " | ".join(self.data)
        return repr(self.data) if isinstance(self.data, str) else str(self.data)


def _classify(value_name: str) -> str | None:
    low = value_name.lower()
    for prefix, marker in _MARKERS:
        if low.startswith(prefix):
            return marker
    return None


def decode_data(reg_type: int, raw: bytes) -> object:
    if not raw:
        return None
    if reg_type in (REG_SZ, REG_EXPAND_SZ):
        return raw.decode("utf-16-le", errors="replace").rstrip("\0")
    if reg_type == REG_MULTI_SZ:
        parts = raw.decode("utf-16-le", errors="replace").split("\0")
        while parts and parts[-1] == "":
            parts.pop()
        return parts
    if reg_type == REG_DWORD and len(raw) == 4:
        return struct.unpack("<I", raw)[0]
    if reg_type == REG_DWORD_BE and len(raw) == 4:
        return struct.unpack(">I", raw)[0]
    if reg_type == REG_QWORD and len(raw) == 8:
        return struct.unpack("<Q", raw)[0]
    return raw.hex()


def parse_pol(path: Path | str) -> list[Entry]:
    buf = Path(path).read_bytes()
    if buf[:4] != b"PReg" or struct.unpack_from("<I", buf, 4)[0] != 1:
        raise ValueError(f"{path}: not a PReg v1 file")
    pos = 8
    entries: list[Entry] = []

    def expect(token: bytes) -> None:
        nonlocal pos
        if buf[pos:pos + len(token)] != token:
            raise ValueError(f"{path}: expected {token!r} at byte {pos}")
        pos += len(token)

    def read_utf16z() -> str:
        nonlocal pos
        end = pos
        while buf[end:end + 2] != b"\0\0":
            end += 2
            if end >= len(buf):
                raise ValueError(f"{path}: unterminated string at byte {pos}")
        text = buf[pos:end].decode("utf-16-le")
        pos = end + 2
        return text

    while pos < len(buf):
        expect(b"[\0")
        key = read_utf16z()
        expect(b";\0")
        value_name = read_utf16z()
        expect(b";\0")
        reg_type = struct.unpack_from("<I", buf, pos)[0]
        pos += 4
        expect(b";\0")
        size = struct.unpack_from("<I", buf, pos)[0]
        pos += 4
        expect(b";\0")
        raw = buf[pos:pos + size]
        pos += size
        expect(b"]\0")
        entries.append(Entry(key, value_name, reg_type, decode_data(reg_type, raw), raw, _classify(value_name)))
    return entries
