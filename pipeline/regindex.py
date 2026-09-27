"""Map registry.pol entries back to ADMX policies and derive the configured state."""
from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass, field
from typing import Optional

from .admx import Catalog, Element, Policy, ValueSpec
from .preg import Entry

PRIMARY_ROLES = ("main", "element")


@dataclass(frozen=True)
class Hit:
    uid: str
    role: str  # main | element | enabled_list | disabled_list | enum_valuelist | bool_list | list
    elem_id: Optional[str] = None


@dataclass
class BaselineState:
    scope: str
    state: str  # Enabled | Disabled
    summary: str
    values: list[tuple[str, str]] = field(default_factory=list)
    raw: list[str] = field(default_factory=list)
    flags: list[str] = field(default_factory=list)


def norm_key(key: str) -> str:
    return key.strip("\\").lower()


def scopes_for(cls: str) -> tuple[str, ...]:
    return ("Machine", "User") if cls == "Both" else (cls,)


def spec_matches(spec: Optional[ValueSpec], entry: Entry) -> bool:
    if spec is None:
        return False
    if spec.kind == "delete":
        return entry.marker == "del"
    if entry.marker:
        return False
    data = entry.data
    if spec.kind in ("decimal", "longDecimal"):
        if isinstance(data, int):
            return data == spec.value
        if isinstance(data, str):  # storeAsText decimals arrive as REG_SZ
            try:
                return int(data.strip()) == spec.value
            except ValueError:
                return False
        return False
    if spec.kind == "string":
        return isinstance(data, str) and data.strip().lower() == str(spec.value).strip().lower()
    return False


class RegIndex:
    def __init__(self, catalog: Catalog):
        self.values: dict[tuple[str, str, str], list[Hit]] = defaultdict(list)
        self.lists: dict[tuple[str, str], list[Hit]] = defaultdict(list)
        for pol in catalog.policies.values():
            for scope in scopes_for(pol.cls):
                self._add_policy(scope, pol)

    def _add(self, scope: str, key: str, name: Optional[str], hit: Hit) -> None:
        if not name:
            return
        bucket = self.values[(scope, norm_key(key), name.lower())]
        if hit not in bucket:
            bucket.append(hit)

    def _add_policy(self, scope: str, pol: Policy) -> None:
        self._add(scope, pol.key, pol.value_name, Hit(pol.uid, "main"))
        for it in pol.enabled_list:
            self._add(scope, it.key, it.value_name, Hit(pol.uid, "enabled_list"))
        for it in pol.disabled_list:
            self._add(scope, it.key, it.value_name, Hit(pol.uid, "disabled_list"))
        for el in pol.elements:
            if el.type == "list":
                bucket = self.lists[(scope, norm_key(el.key))]
                hit = Hit(pol.uid, "list", el.id)
                if hit not in bucket:
                    bucket.append(hit)
                continue
            self._add(scope, el.key, el.value_name, Hit(pol.uid, "element", el.id))
            for item in el.items:
                for li in item.value_list:
                    self._add(scope, li.key, li.value_name, Hit(pol.uid, "enum_valuelist", el.id))
            for li in el.true_list + el.false_list:
                self._add(scope, li.key, li.value_name, Hit(pol.uid, "bool_list", el.id))

    def resolve(self, scope: str, entry: Entry) -> list[Hit]:
        key = norm_key(entry.key)
        hits: list[Hit] = []
        if entry.marker in (None, "del"):
            hits = list(self.values.get((scope, key, entry.name.lower()), []))
        # A value owned directly by a policy or element outranks catch-all enabled/disabled lists.
        primary = [h for h in hits if h.role in PRIMARY_ROLES]
        if primary:
            return primary
        for h in self.lists.get((scope, key), []):
            if h not in hits:
                hits.append(h)
        return hits


def _all_present(items, entries: list[Entry]) -> bool:
    if not items:
        return False
    return all(
        any(norm_key(e.key) == norm_key(it.key) and e.name.lower() == it.value_name.lower() and spec_matches(it.spec, e)
            for e in entries)
        for it in items
    )


def _render_list(el: Element, entries: list[Entry]) -> str:
    prefix = ""
    parts: list[tuple[str, Entry]] = []
    for e in entries:
        if e.marker == "delvals":
            prefix = "(replace existing) "
        elif not e.marker:
            parts.append((e.name, e))

    def sort_key(item):
        name = item[0]
        if el.value_prefix and name.lower().startswith(el.value_prefix.lower()):
            name = name[len(el.value_prefix):]
        return (0, int(name)) if name.isdigit() else (1, name.lower())

    parts.sort(key=sort_key)
    rendered = [f"{name} = {e.data}" if el.explicit_value else str(e.data) for name, e in parts]
    return prefix + ("[" + "; ".join(rendered) + "]" if rendered else "(empty)")


def _render_element(el: Element, entries: list[Entry]) -> str:
    if el.type == "list":
        return _render_list(el, entries)
    e = next((x for x in entries if x.name.lower() == (el.value_name or "").lower()), entries[0])
    if el.type == "enum":
        for item in el.items:
            if spec_matches(item.spec, e):
                return item.display
        return "(removed)" if e.marker else e.render()
    if el.type == "boolean":
        if spec_matches(el.true_spec, e):
            return "Yes"
        if spec_matches(el.false_spec, e) or e.marker == "del":
            return "No"
        return e.render()
    if e.marker:
        return "(removed)"
    if el.type == "multiText":
        return " | ".join(e.data) if isinstance(e.data, list) else str(e.data)
    if el.type == "text":
        text = "" if e.data is None else str(e.data)
        return repr(text) if text.strip() == "" else text
    return str(e.data)


def derive_state(pol: Policy, pairs: list[tuple[Entry, Hit]], scope: str) -> BaselineState:
    flags: list[str] = []

    def entries(role: str) -> list[Entry]:
        return [e for e, h in pairs if h.role == role]

    state: Optional[str] = None
    main = entries("main")
    # Some policies name the same registry value as their only element; the value then carries the
    # option rather than an on/off flag, so any concrete value means Enabled.
    self_valued = pol.enabled_value is None and pol.disabled_value is None and any(
        el.value_name and el.value_name.lower() == (pol.value_name or "").lower() and norm_key(el.key) == norm_key(pol.key)
        for el in pol.elements
    )
    if pol.value_name:
        if main:
            e = main[0]
            enabled = pol.enabled_value or ValueSpec("decimal", 1)
            disabled = pol.disabled_value or ValueSpec("delete")
            if self_valued:
                state = "Disabled" if e.marker == "del" else "Enabled"
            elif spec_matches(enabled, e):
                state = "Enabled"
            elif spec_matches(disabled, e):
                state = "Disabled"
            elif e.marker == "del" or (pol.disabled_value is None and e.data == 0):
                state = "Disabled"
                flags.append("nonstandard_disable")
            else:
                state = "Enabled"
                flags.append("unrecognized_value")
        else:
            flags.append("main_value_missing")
    if state is None and (pol.enabled_list or pol.disabled_list):
        en, dis = entries("enabled_list"), entries("disabled_list")
        if _all_present(pol.enabled_list, en):
            state = "Enabled"
        elif _all_present(pol.disabled_list, dis):
            state = "Disabled"
        elif en or dis:
            state = "Enabled" if en else "Disabled"
            flags.append("partial_list")
    if state is None:
        element_entries = [e for e, h in pairs if h.role in ("element", "list", "enum_valuelist", "bool_list")]
        if element_entries and all(e.marker for e in element_entries):
            state = "Disabled"
        elif element_entries:
            state = "Enabled"
        else:
            state = "Enabled"
            flags.append("unrecognized_value")

    values: list[tuple[str, str]] = []
    if state == "Enabled":
        for el in pol.elements:
            ents = [e for e, h in pairs if h.elem_id == el.id and h.role in ("element", "list")]
            if ents:
                values.append((el.label, _render_element(el, ents)))

    hive = "HKLM" if scope == "Machine" else "HKCU"
    raw: list[str] = []
    for e, _ in pairs:
        line = f"{hive}\\{e.key}!{e.value_name}" + ("" if e.marker else f" = {e.type_name} {e.render()}")
        if line not in raw:
            raw.append(line)

    summary = state
    if values:
        summary += "; " + "; ".join(f"{label} = {val}" for label, val in values)
    return BaselineState(scope, state, summary, values, raw, flags)
