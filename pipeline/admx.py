"""ADMX/ADML catalogue: policies with resolved strings, categories, supportedOn and element models."""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Optional

from .xmlutil import child, children, iter_local, load_xml, local, normalize_text

_RES_REF = re.compile(r"^\$\((string|presentation)\.([^)]+)\)$")
_ELEMENT_TYPES = {"enum", "boolean", "decimal", "longDecimal", "text", "multiText", "list"}
_LABEL_FROM_TEXT = {"dropdownList", "checkBox", "decimalTextBox", "longDecimalTextBox", "listBox", "multiTextBox"}
_LABEL_FROM_CHILD = {"textBox", "comboBox"}

# Source tag by ADMX stem; anything else is a stock Windows template.
_SOURCE_BY_STEM = {
    "secguide": "secguide",
    "mss-legacy": "mss",
    "restrictedtrafficcustompolicy": "rtlfb-custom",
}


@dataclass
class ValueSpec:
    kind: str  # decimal | longDecimal | string | delete
    value: object = None


@dataclass
class ListItem:
    key: str
    value_name: str
    spec: ValueSpec


@dataclass
class EnumItem:
    display: str
    spec: Optional[ValueSpec]
    value_list: list[ListItem] = field(default_factory=list)


@dataclass
class Element:
    type: str
    id: str
    key: str
    value_name: Optional[str]
    label: str
    required: bool = False
    items: list[EnumItem] = field(default_factory=list)
    true_spec: Optional[ValueSpec] = None
    false_spec: Optional[ValueSpec] = None
    true_list: list[ListItem] = field(default_factory=list)
    false_list: list[ListItem] = field(default_factory=list)
    min_value: Optional[int] = None
    max_value: Optional[int] = None
    store_as_text: bool = False
    max_length: Optional[int] = None
    max_strings: Optional[int] = None
    expandable: bool = False
    value_prefix: Optional[str] = None
    additive: bool = False
    explicit_value: bool = False
    ui: dict = field(default_factory=dict)  # ADML control: ctl, def_num | def_bool | def_str, spin_step, no_sort


@dataclass
class Policy:
    uid: str
    name: str
    source: str
    admx: str
    namespace: str
    cls: str
    display_name: str
    explain: str
    key: str
    value_name: Optional[str]
    enabled_value: Optional[ValueSpec]
    disabled_value: Optional[ValueSpec]
    enabled_list: list[ListItem]
    disabled_list: list[ListItem]
    elements: list[Element]
    category_ref: tuple[str, str]
    supported_ref: Optional[tuple[str, str]]
    category_path: list[str] = field(default_factory=list)
    supported_on: str = ""
    client_extension: Optional[str] = None
    pres: list[dict] = field(default_factory=list)  # dialog order: {"text": caption} | {"ref": element id}


@dataclass
class Category:
    display: str
    parent: Optional[tuple[str, str]]
    admx: str


@dataclass
class Catalog:
    policies: dict[str, Policy] = field(default_factory=dict)
    categories: dict[tuple[str, str], Category] = field(default_factory=dict)
    supported: dict[tuple[str, str], str] = field(default_factory=dict)
    warnings: list[str] = field(default_factory=list)

    def warn(self, msg: str) -> None:
        if msg not in self.warnings:
            self.warnings.append(msg)


def find_adml(admx_path: Path) -> Optional[Path]:
    stem = admx_path.stem.lower()
    for folder in (admx_path.parent / "en-US", admx_path.parent):
        if folder.is_dir():
            for p in folder.iterdir():
                if p.suffix.lower() == ".adml" and p.stem.lower() == stem:
                    return p
    return None


def parse_adml(path: Path) -> tuple[dict[str, str], dict[str, dict[str, str]], dict[str, list[dict]]]:
    root = load_xml(path)
    strings = {
        s.get("id", "").lower(): normalize_text("".join(s.itertext()))
        for s in iter_local(root, "string")
    }
    presentations: dict[str, dict[str, str]] = {}
    controls: dict[str, list[dict]] = {}
    for pres in iter_local(root, "presentation"):
        labels: dict[str, str] = {}
        preceding = ""
        for ctl in pres:
            kind = local(ctl.tag)
            ref = ctl.get("refId")
            if not ref:
                # A free-standing <text> usually captions the control that follows it.
                if kind == "text":
                    preceding = _clean_label("".join(ctl.itertext()))
                continue
            if kind in _LABEL_FROM_TEXT:
                text = "".join(ctl.itertext())
            elif kind in _LABEL_FROM_CHILD:
                lab = child(ctl, "label")
                text = "".join(lab.itertext()) if lab is not None else ""
            else:
                text = ""
            labels[ref.lower()] = _clean_label(text) or preceding
            preceding = ""
        presentations[pres.get("id", "").lower()] = labels
        controls[pres.get("id", "").lower()] = [c for c in (_parse_control(ctl) for ctl in pres) if c]
    return strings, presentations, controls


def _clean_label(text: str) -> str:
    return normalize_text(text).replace("\n", " ").strip().rstrip(":").strip()


def _parse_control(ctl) -> Optional[dict]:
    """One dialog control with the defaults gpedit pre-fills; attribute names vary in case across templates."""
    kind = local(ctl.tag)
    if kind == "text":
        caption = normalize_text("".join(ctl.itertext())).replace("\n", " ")
        return {"text": caption} if caption else None
    ref = ctl.get("refId")
    if not ref:
        return None
    attrs = {k.lower(): v for k, v in ctl.attrib.items()}
    out: dict = {"ref": ref, "ctl": kind}

    def flag(name: str) -> bool:
        return (attrs.get(name) or "").strip().lower() == "true"

    def number(name: str) -> Optional[int]:
        try:
            return int((attrs.get(name) or "").strip())
        except ValueError:
            return None

    if kind in ("decimalTextBox", "longDecimalTextBox"):
        if number("defaultvalue") is not None:
            out["def_num"] = number("defaultvalue")
        if number("spinstep") is not None:
            out["spin_step"] = number("spinstep")
    elif kind == "checkBox":
        if flag("defaultchecked"):
            out["def_bool"] = True
    elif kind == "dropdownList":
        if number("defaultitem") is not None:
            out["def_num"] = number("defaultitem")
        if flag("nosort"):
            out["no_sort"] = True
    elif kind in ("textBox", "comboBox"):
        default = child(ctl, "defaultValue" if kind == "textBox" else "default")
        if default is not None and (default.text or "").strip():
            out["def_str"] = default.text.strip()
        suggestions = [s.text.strip() for s in children(ctl, "suggestion") if (s.text or "").strip()]
        if suggestions:
            out["suggestions"] = suggestions
    return out


def _parse_spec(container) -> Optional[ValueSpec]:
    for c in container:
        kind = local(c.tag)
        if kind in ("decimal", "longDecimal"):
            return ValueSpec(kind, int(c.get("value")))
        if kind == "string":
            return ValueSpec("string", c.text or "")
        if kind == "delete":
            return ValueSpec("delete")
    return None


def _parse_list_items(list_el, default_key: str) -> list[ListItem]:
    default_key = list_el.get("defaultKey") or default_key
    out = []
    for it in children(list_el, "item"):
        value = child(it, "value")
        spec = (_parse_spec(value) if value is not None else None) or ValueSpec("decimal", 1)
        out.append(ListItem(it.get("key") or default_key, it.get("valueName", ""), spec))
    return out


def _int_attr(el, *names) -> Optional[int]:
    for n in names:
        v = el.get(n)
        if v is not None:
            return int(v)
    return None


def _bool_attr(el, name: str) -> bool:
    return (el.get(name) or "false").lower() == "true"


def _parse_elements(elements_el, policy_key: str, labels: dict[str, str], resolve: Callable[[str], str]) -> list[Element]:
    out = []
    for el in elements_el:
        kind = local(el.tag)
        if kind not in _ELEMENT_TYPES:
            continue
        eid = el.get("id", "")
        key = el.get("key") or policy_key
        e = Element(kind, eid, key, el.get("valueName"), labels.get(eid.lower()) or eid, _bool_attr(el, "required"))
        if kind == "enum":
            for it in children(el, "item"):
                value = child(it, "value")
                vlist = child(it, "valueList")
                e.items.append(EnumItem(
                    resolve(it.get("displayName", "")),
                    _parse_spec(value) if value is not None else None,
                    _parse_list_items(vlist, key) if vlist is not None else [],
                ))
        elif kind == "boolean":
            tv, fv = child(el, "trueValue"), child(el, "falseValue")
            # GPMC writes DWORD 1 for a checked box and deletes the value for an unchecked one unless told otherwise.
            e.true_spec = (_parse_spec(tv) if tv is not None else None) or ValueSpec("decimal", 1)
            e.false_spec = (_parse_spec(fv) if fv is not None else None) or ValueSpec("delete")
            tl, fl = child(el, "trueList"), child(el, "falseList")
            e.true_list = _parse_list_items(tl, key) if tl is not None else []
            e.false_list = _parse_list_items(fl, key) if fl is not None else []
        elif kind in ("decimal", "longDecimal"):
            e.min_value = _int_attr(el, "minValue", "minvalue")
            e.max_value = _int_attr(el, "maxValue", "maxvalue")
            e.store_as_text = _bool_attr(el, "storeAsText")
        elif kind == "text":
            e.max_length = _int_attr(el, "maxLength")
            e.expandable = _bool_attr(el, "expandable")
        elif kind == "multiText":
            e.max_length = _int_attr(el, "maxLength")
            e.max_strings = _int_attr(el, "maxStrings")
        elif kind == "list":
            e.value_prefix = el.get("valuePrefix")
            e.additive = _bool_attr(el, "additive")
            e.explicit_value = _bool_attr(el, "explicitValue")
            e.expandable = _bool_attr(el, "expandable")
        out.append(e)
    return out


def parse_admx(path: Path, catalog: Catalog, source: Optional[str] = None) -> int:
    """Parse one ADMX (+ its en-US ADML) into the catalog; returns the number of policies added."""
    source = source or _SOURCE_BY_STEM.get(path.stem.lower(), "windows")
    adml = find_adml(path)
    if adml is None:
        raise FileNotFoundError(f"no en-US ADML for {path}")
    strings, presentations, controls = parse_adml(adml)
    root = load_xml(path)
    stem = path.stem

    def resolve(ref: str) -> str:
        m = _RES_REF.match(ref or "")
        if not m:
            return ref or ""
        text = strings.get(m.group(2).lower())
        if text is None:
            catalog.warn(f"{path.name}: unresolved {ref}")
            return m.group(2)
        return text

    nsmap: dict[str, str] = {}
    own_ns = ""
    for c in children(child(root, "policyNamespaces"), "target"):
        own_ns = c.get("namespace", "")
        nsmap[c.get("prefix", "")] = own_ns
    for c in children(child(root, "policyNamespaces"), "using"):
        nsmap[c.get("prefix", "")] = c.get("namespace", "")

    def ref_of(r: str) -> tuple[str, str]:
        if ":" in r:
            prefix, name = r.split(":", 1)
            ns = nsmap.get(prefix)
            if ns is None:
                catalog.warn(f"{path.name}: unknown prefix in ref {r}")
                ns = prefix
            return ns, name
        return own_ns, r

    for d in children(child(child(root, "supportedOn"), "definitions"), "definition"):
        catalog.supported[(own_ns, d.get("name", ""))] = resolve(d.get("displayName", ""))

    for c in children(child(root, "categories"), "category"):
        pc = child(c, "parentCategory")
        parent = ref_of(pc.get("ref", "")) if pc is not None else None
        catalog.categories[(own_ns, c.get("name", ""))] = Category(resolve(c.get("displayName", "")), parent, path.name)

    added = 0
    for p in children(child(root, "policies"), "policy"):
        name = p.get("name", "")
        uid = f"{stem}:{name}"
        if uid in catalog.policies:
            n = 2
            while f"{uid}#{n}" in catalog.policies:
                n += 1
            catalog.warn(f"{path.name}: duplicate policy name {name}")
            uid = f"{uid}#{n}"
        key = p.get("key", "")
        ev, dv = child(p, "enabledValue"), child(p, "disabledValue")
        el, dl = child(p, "enabledList"), child(p, "disabledList")
        pc, so = child(p, "parentCategory"), child(p, "supportedOn")
        m = _RES_REF.match(p.get("presentation") or "")
        labels = presentations.get(m.group(2).lower(), {}) if m else {}
        elements_el = child(p, "elements")
        elements = _parse_elements(elements_el, key, labels, resolve) if elements_el is not None else []
        # A lone unlabeled control (e.g. a dropdown with empty caption) is best described by the policy name.
        if len(elements) == 1 and elements[0].label == elements[0].id:
            elements[0].label = resolve(p.get("displayName", ""))
        pres = _attach_controls(elements, controls.get(m.group(2).lower(), []) if m else [])
        catalog.policies[uid] = Policy(
            uid=uid, name=name, source=source, admx=path.name, namespace=own_ns,
            cls=p.get("class", "Machine"),
            display_name=resolve(p.get("displayName", "")),
            explain=resolve(p.get("explainText", "")),
            key=key, value_name=p.get("valueName"),
            enabled_value=_parse_spec(ev) if ev is not None else None,
            disabled_value=_parse_spec(dv) if dv is not None else None,
            enabled_list=_parse_list_items(el, key) if el is not None else [],
            disabled_list=_parse_list_items(dl, key) if dl is not None else [],
            elements=elements,
            category_ref=ref_of(pc.get("ref", "")) if pc is not None else (own_ns, ""),
            supported_ref=ref_of(so.get("ref", "")) if so is not None else None,
            client_extension=p.get("clientExtension"),
            pres=pres,
        )
        added += 1
    return added


def _attach_controls(elements: list[Element], controls: list[dict]) -> list[dict]:
    """Store each control on the element it edits and return the dialog order."""
    by_id = {e.id.lower(): e for e in elements}
    pres: list[dict] = []
    for c in controls:
        if "text" in c:
            pres.append(c)
            continue
        el = by_id.get(c["ref"].lower())
        if el is None or el.ui:
            continue
        el.ui = {k: v for k, v in c.items() if k != "ref"}
        pres.append({"ref": el.id})
    return pres


def _humanize(name: str) -> str:
    return re.sub(r"^SUPPORTED_", "", name).replace("_", " ")


def finalize(catalog: Catalog) -> None:
    """Resolve category paths and supportedOn text once every template is loaded."""
    for pol in catalog.policies.values():
        path: list[str] = []
        cur: Optional[tuple[str, str]] = pol.category_ref
        for _ in range(10):
            if cur is None:
                break
            cat = catalog.categories.get(cur)
            if cat is None:
                catalog.warn(f"{pol.admx}: dangling category ref {cur[0]}:{cur[1]}")
                path.append(cur[1])
                break
            path.append(cat.display)
            cur = cat.parent
        pol.category_path = list(reversed(path))
        if pol.supported_ref is not None:
            text = catalog.supported.get(pol.supported_ref)
            if text is None:
                catalog.warn(f"{pol.admx}: dangling supportedOn ref {pol.supported_ref[0]}:{pol.supported_ref[1]}")
                text = _humanize(pol.supported_ref[1])
            pol.supported_on = text


def template_paths(base: Path) -> list[Path]:
    folders = [
        base / "PolicyDefinitions",
        base / "Security Baseline" / "Templates",
        base / "Restricted Traffic Limited Functionality Baseline" / "Templates",
    ]
    paths: list[Path] = []
    for folder in folders:
        paths.extend(sorted(p for p in folder.iterdir() if p.suffix.lower() == ".admx"))
    return paths


def load_catalog(base: Path) -> Catalog:
    catalog = Catalog()
    for path in template_paths(base):
        parse_admx(path, catalog)
    finalize(catalog)
    return catalog
