"""Pluggable profiles.

A profile teaches the engine how a particular export dialect encodes identity
and references. The engine itself knows nothing about Salesforce: swap the
profile and the same machinery works on any zip of tabular files.

Register a new profile with ``@register`` and it becomes available as
``--profile <name>``.
"""

from __future__ import annotations

import re

PROFILES: dict[str, type["Profile"]] = {}


def register(cls: type["Profile"]) -> type["Profile"]:
    PROFILES[cls.name] = cls
    return cls


def get_profile(name: str) -> "Profile":
    try:
        return PROFILES[name]()
    except KeyError:
        raise SystemExit(
            f"unknown profile {name!r}; available: {', '.join(sorted(PROFILES))}"
        ) from None


class Profile:
    """Base profile: name-based matching plus value overlap, nothing more."""

    name = "base"

    # --- naming -----------------------------------------------------------
    def node_name(self, member: str, sheet: str, sheet_count: int) -> str:
        """Human-stable node id for one sheet of one file in the zip."""
        stem = member.rsplit("/", 1)[-1].rsplit(".", 1)[0]
        if sheet_count == 1 or sheet.lower() in {"sheet1", "sheet", stem.lower()}:
            return stem
        return f"{stem}.{sheet}"

    # --- identity ---------------------------------------------------------
    def id_token(self, value: str) -> str | None:
        """Type token embedded in a value, or None if the dialect has none."""
        return None

    def looks_like_id(self, values: list[str]) -> bool:
        """Do these sampled values look like opaque identifiers?"""
        if not values:
            return False
        hits = sum(1 for v in values if re.fullmatch(r"[A-Za-z0-9_\-]{6,64}", v))
        return hits / len(values) >= 0.95

    def key_rank(self, column: str, node: str) -> int:
        """Lower is a better primary-key candidate; None-ish = 99."""
        c, n = column.lower(), node.lower().replace(" ", "_")
        if c in {"id", "pk", "key", "uuid", "guid"}:
            return 0
        if c in {f"{n}_id", f"{n}id", f"{n}_key"}:
            return 1
        return 99

    # --- references -------------------------------------------------------
    def reference_base(self, column: str) -> str | None:
        """Strip a foreign-key suffix to the implied target name, else None."""
        m = re.fullmatch(r"(?i)(.+?)[_\- ]?id", column)
        if not m:
            return None
        base = m.group(1)
        return base or None

    def name_aliases(self, base: str) -> set[str]:
        """Candidate node names a reference base could mean."""
        b = base.lower()
        out = {b, b.replace("_", ""), b.replace("_", " ")}
        for v in list(out):
            out.add(v + "s")
            if v.endswith("s"):
                out.add(v[:-1])
            if v.endswith("y"):
                out.add(v[:-1] + "ies")
        return out

    def builtin_targets(self, column: str) -> list[str]:
        """Well-known references the dialect defines regardless of naming."""
        return []

    def is_polymorphic(self, column: str) -> bool:
        return False


@register
class GenericProfile(Profile):
    name = "generic"


SF_ID = re.compile(r"[A-Za-z0-9]{15}|[A-Za-z0-9]{18}")

#: Standard objects whose key prefix is fixed, so we can name a reference even
#: when the object itself was not included in the extract.
SF_KNOWN_PREFIXES = {
    "001": "Account", "003": "Contact", "005": "User", "006": "Opportunity",
    "00Q": "Lead", "500": "Case", "00T": "Task", "00U": "Event",
    "0Q0": "Quote", "801": "Order", "701": "Campaign", "00G": "Group",
    "012": "RecordType", "00e": "Profile", "0Hn": "ContentDocument",
    "068": "ContentVersion", "015": "Document", "00P": "Attachment",
}

#: Columns Salesforce always points at a fixed object.
SF_BUILTIN = {
    "ownerid": ["User", "Group"],
    "createdbyid": ["User"],
    "lastmodifiedbyid": ["User"],
    "recordtypeid": ["RecordType"],
    "contactid": ["Contact"],
    "accountid": ["Account"],
}

#: Columns that legitimately point at more than one object.
SF_POLYMORPHIC = {
    "whatid", "whoid", "parentid", "ownerid", "relatedtoid",
    "targetobjectid", "linkedentityid", "subjectid",
}


@register
class SalesforceProfile(Profile):
    """Salesforce report/data exports.

    The key insight this profile exploits: the first three characters of every
    Salesforce record Id are the object's key prefix. That makes reference
    targets readable straight off the value, so the graph can be built without
    joining every table against every other table.
    """

    name = "salesforce"

    def id_token(self, value: str) -> str | None:
        if len(value) in (15, 18) and value.isalnum():
            return value[:3]
        return None

    def looks_like_id(self, values: list[str]) -> bool:
        if not values:
            return False
        hits = sum(1 for v in values if self.id_token(v) is not None)
        return hits / len(values) >= 0.9

    def key_rank(self, column: str, node: str) -> int:
        c = column.lower()
        if c == "id":
            return 0
        if c in {f"{node.lower()}id", f"{node.lower()}__c"}:
            return 1
        return 99

    def reference_base(self, column: str) -> str | None:
        # Custom lookups: My_Account__c -> My_Account
        m = re.fullmatch(r"(?i)(.+?)__c", column)
        if m:
            return m.group(1)
        m = re.fullmatch(r"(?i)(.+?)id", column)
        if m and m.group(1):
            return m.group(1)
        return None

    def name_aliases(self, base: str) -> set[str]:
        out = super().name_aliases(base)
        out.add(f"{base.lower()}__c")
        out.add(base.lower().replace("_", ""))
        return out

    def builtin_targets(self, column: str) -> list[str]:
        return SF_BUILTIN.get(column.lower(), [])

    def is_polymorphic(self, column: str) -> bool:
        return column.lower() in SF_POLYMORPHIC

    def name_for_token(self, token: str) -> str | None:
        return SF_KNOWN_PREFIXES.get(token)
