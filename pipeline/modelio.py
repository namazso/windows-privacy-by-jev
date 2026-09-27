"""Model request construction: per-policy state, the question texts, canonical hashing."""
from __future__ import annotations

import hashlib
import json

from .admx import Policy

MODEL = "jev-1.13-free"
EXPLAIN_CAP = 12000

_SCOPE_LABEL = {"Machine": "Computer", "User": "User", "Both": "Computer and User"}
_OPTION_TYPE = {
    "enum": "dropdown", "boolean": "checkbox", "decimal": "number", "longDecimal": "number",
    "text": "text", "multiText": "multiline_text", "list": "list",
}

QUESTIONS: dict[str, dict] = {
    "privacy": {
        "type": "noul",
        "instructions": (
            "A Windows 11 administrator changes this Group Policy from Not Configured to a configured state "
            "(Enabled or Disabled, with option values chosen to minimize data exposure). Would that improve the "
            "privacy of the device's users compared with the default behavior described in the explanation? "
            "Privacy means how much personal data, usage or diagnostic data, identifiers, location, content, or "
            "activity is collected, stored, transmitted to Microsoft or third parties, shared with apps or other "
            "users, or used for advertising or personalization."
        ),
        "criteria": {
            "true": (
                "Some configured state causes less collection, transmission, sharing, retention, or "
                "advertising/personalization use of user or device data than the not-configured behavior, turns "
                "off a cloud-connected or online feature that sends such data, locks a data-minimizing behavior so "
                "users or apps cannot re-enable it, or gives users more control over their data. Reducing network "
                "traffic to Microsoft or third-party services counts even if the data is not obviously sensitive."
            ),
            "false": (
                "No configured state changes what user or device data is collected, transmitted, shared, "
                "retained, or used. The policy only concerns matters such as performance, UI layout, installation "
                "or licensing, hardware or driver behavior, authentication strength, encryption, protocol "
                "settings, administrative delegation, scheduling, or logging that stays on the device."
            ),
        },
    },
    "security": {
        "type": "noul",
        "instructions": (
            "A Windows 11 administrator changes this Group Policy from Not Configured to a configured state "
            "(Enabled or Disabled, with option values chosen to harden the device). Would that make the device or "
            "its users more secure against attackers, malware, unauthorized access, privilege escalation, "
            "tampering, or data compromise compared with the default behavior described in the explanation? "
            "Setting a policy so that a protection cannot be turned off counts as a configured state."
        ),
        "criteria": {
            "true": (
                "Some configured state reduces attack surface (disables legacy, insecure, or unnecessary "
                "protocols, features, or code-execution paths), enforces stronger authentication, encryption, "
                "code integrity, isolation, or update behavior, restricts what untrusted users, apps, devices, "
                "networks, or removable media can do, prevents users from disabling or bypassing a protection, "
                "or improves security auditing."
            ),
            "false": (
                "No configured state materially changes resistance to attack, malware, unauthorized access, or "
                "data compromise. The policy only affects appearance, convenience, performance, telemetry or "
                "advertising with no security effect, or organizational preferences such as branding, default "
                "apps, or maintenance timing. Also false if every configured state is equally or less secure than "
                "the not-configured behavior."
            ),
        },
    },
    "ux_degrade": {
        "type": "noul",
        "instructions": (
            "A Windows 11 administrator configures this Group Policy in its most restrictive direction: the "
            "configured state that most limits features, actions, or data flows. Would ordinary end users of the "
            "device directly experience a worse experience as a result? Judge only direct, user-perceptible "
            "effects: features that stop working or disappear, actions that are blocked or need extra steps or "
            "prompts, content or settings no longer available, slower performance, or broken compatibility. Do "
            "not count less diagnostic, usage, or telemetry data sent to Microsoft or third parties, fewer or "
            "less-targeted ads, suggestions, tips, promotions, or tailored content, less personalization, or less "
            "background network traffic."
        ),
        "criteria": {
            "true": (
                "The restrictive configuration removes, disables, blocks, or breaks a feature, app, device class, "
                "sync, protocol, or workflow that users actively use (for example web search from Start, Copilot, "
                "the Store, OneDrive sync, camera, microphone, removable storage, printing, clipboard, or a legacy "
                "protocol some users depend on); makes the system noticeably slower or less reliable; adds "
                "prompts, restrictions, or manual steps; or hides settings or content users may want other than "
                "ads, promotions, tips, suggestions, and personalization. True even if the feature also sends data "
                "to Microsoft."
            ),
            "false": (
                "The restrictive configuration has no effect ordinary users would notice, or its only visible "
                "effects are less telemetry, diagnostic, usage, or error-report data; fewer or less-targeted ads, "
                "suggestions, tips, promotions, consumer-experience or tailored content; less personalization; "
                "less background network traffic; or changes only administrators, logs, servers, or management "
                "systems notice."
            ),
        },
    },
    "privacy_direction": {
        "type": "choice",
        "instructions": (
            "Which configured state of this policy improves user privacy the most compared with Not Configured? "
            "For Enabled, assume option values that minimize data exposure."
        ),
        "criteria": {
            "enabled": (
                "Enabled results in less data collection, transmission, sharing, or personalization than Not "
                "Configured, and at least as little as Disabled."
            ),
            "disabled": (
                "Disabled results in less data collection, transmission, sharing, or personalization than Not "
                "Configured, and less than Enabled."
            ),
            "neither": (
                "No configured state reduces data collection, transmission, sharing, or personalization compared "
                "with Not Configured."
            ),
        },
    },
    "security_direction": {
        "type": "choice",
        "instructions": (
            "Which configured state of this policy makes the device most secure compared with Not Configured? "
            "For Enabled, assume option values that harden the device."
        ),
        "criteria": {
            "enabled": (
                "Enabled reduces attack surface, strengthens protections, or prevents bypass of protections "
                "compared with Not Configured, and at least as much as Disabled."
            ),
            "disabled": (
                "Disabled reduces attack surface, strengthens protections, or prevents bypass of protections "
                "compared with Not Configured, and more than Enabled."
            ),
            "neither": (
                "No configured state reduces attack surface, strengthens protections, or prevents bypass of "
                "protections compared with Not Configured."
            ),
        },
    },
}


def canonical(obj) -> str:
    return json.dumps(obj, sort_keys=True, ensure_ascii=False, separators=(",", ":"))


def sha(obj) -> str:
    return hashlib.sha256(canonical(obj).encode("utf-8")).hexdigest()


def questions_sha() -> str:
    return sha(QUESTIONS)


def build_options(pol: Policy) -> list[dict]:
    out = []
    for el in pol.elements:
        opt: dict = {"label": el.label, "type": _OPTION_TYPE[el.type]}
        if el.type == "enum":
            opt["choices"] = [item.display for item in el.items]
        elif el.type in ("decimal", "longDecimal") and (el.min_value is not None or el.max_value is not None):
            opt["range"] = [el.min_value, el.max_value]
        out.append(opt)
    return out


def build_state(pol: Policy) -> dict:
    scope = _SCOPE_LABEL[pol.cls]
    explanation = pol.explain
    if len(explanation) > EXPLAIN_CAP:
        explanation = explanation[:EXPLAIN_CAP] + " [...]"
    state = {
        "policy_name": pol.display_name,
        "path": " > ".join([f"{scope} Configuration", "Administrative Templates", *pol.category_path]),
        "applies_to": scope,
        "supported_on": pol.supported_on,
        "possible_states": "Not Configured (default), Enabled, Disabled",
        "explanation": explanation,
    }
    options = build_options(pol)
    if options:
        state["options"] = options
    return state


def build_request(pol: Policy) -> dict:
    return {"model": MODEL, "state": build_state(pol), "questions": QUESTIONS}


def request_sha(body: dict) -> str:
    # The alias is left out so answers from the free and paid aliases of the same model are shared.
    return sha({"state": body["state"], "questions": body["questions"]})
