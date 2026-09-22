"""Pass 3: emit the graph.

The JSON is the canonical artifact and is written for an agent to read in one
shot: it carries a short reading guide, every column is annotated inline with
the reference it participates in, and empty fields are dropped so the file
stays small enough to paste into context.
"""

from __future__ import annotations

import json
import re
from collections import defaultdict

from .model import Graph

SCHEMA_VERSION = "1.0"

READING_GUIDE = (
    "nodes[] are tables (one per sheet); edges[] are references between them. "
    "READ THIS BEFORE JUDGING RELIABILITY: resolve_rate is the authority. It is "
    "the measured share of non-null source values actually found in the target "
    "key, so 1.0 means every single reference was checked against real rows. "
    "verified=true means resolve_rate >= 0.9, i.e. the relationship is proven by "
    "data; report it as established, not as likely. A confidence below 1.0 does "
    "NOT mean unverified: confidence only ranks how the target was identified, "
    "and an edge loses a little of it merely because the column name does not "
    "contain the target's name (Lead.ConvertedAccountId resolves 100% to "
    "Account.Id but shares no name with it). Only kind='name-only' is a guess - "
    "there, nothing resolved. Where resolve_rate is between 0 and 0.9 the "
    "relationship is real but some rows point at records missing from the "
    "extract. polymorphic_group marks one column pointing at several tables. "
    "load_order lists tables in dependency order, targets first; its last layer "
    "holds anything unorderable because of cycles[]. unresolved[] are references "
    "pointing outside this extract, which are not errors. Sheets the export "
    "shipped with no rows are excluded entirely."
)


def _clean(obj):
    """Drop empty/zero fields so the JSON stays token-lean."""
    if isinstance(obj, dict):
        return {
            k: _clean(v)
            for k, v in obj.items()
            if v not in (None, "", [], {}, 0, 0.0)
            or k in ("rows", "confidence", "verified")
        }
    if isinstance(obj, list):
        return [_clean(v) for v in obj]
    return obj


def to_dict(graph: Graph) -> dict:
    refs: dict[tuple[str, str], list[str]] = defaultdict(list)
    for e in graph.edges:
        refs[(e.from_node, e.from_column)].append(f"{e.to_node}.{e.to_column}")

    nodes = []
    for n in graph.nodes:
        cols = []
        for c in n.columns:
            col = {
                "name": c.name,
                "type": c.dtype,
                "distinct": c.distinct,
                "nulls_pct": round(c.nulls / c.rows, 4) if c.rows else 0.0,
            }
            if c.name == n.key:
                col["key"] = True
            token = max(c.id_tokens, key=c.id_tokens.get) if c.id_tokens else None
            if token and len(c.id_tokens) == 1:
                col["token"] = token
            elif c.id_tokens:
                col["tokens"] = c.id_tokens
            target = refs.get((n.id, c.name))
            if target:
                col["ref"] = target if len(target) > 1 else target[0]
            cols.append(col)

        nodes.append(
            {
                "id": n.id,
                "source": n.source,
                "sheet": n.sheet if n.sheet != n.id else None,
                "rows": n.rows,
                "key": n.key,
                "key_token": n.key_token,
                "columns": cols,
                "warnings": n.warnings,
            }
        )

    edges = [
        {
            "from": f"{e.from_node}.{e.from_column}",
            "to": f"{e.to_node}.{e.to_column}",
            "kind": e.kind,
            "cardinality": e.cardinality,
            "confidence": e.confidence,
            "resolve_rate": e.resolve_rate,
            "verified": e.verified,
            "null_pct": e.null_pct,
            "polymorphic_group": e.polymorphic_group,
            "evidence": e.evidence,
        }
        for e in graph.edges
    ]

    return _clean(
        {
            "schema_version": SCHEMA_VERSION,
            "generator": "depgraph 0.1.0",
            "profile": graph.profile,
            "source": graph.source,
            "stats": graph.stats,
            "reading_guide": READING_GUIDE,
            "load_order": graph.load_order,
            "cycles": graph.cycles,
            "nodes": nodes,
            "edges": edges,
            "unresolved": [
                {
                    "from": f"{u.node}.{u.column}",
                    "token": u.token,
                    "rows": u.rows,
                    "reason": u.reason,
                }
                for u in graph.unresolved
            ],
        }
    )


def to_json(graph: Graph, indent: int | None = 1) -> str:
    return json.dumps(to_dict(graph), indent=indent, ensure_ascii=False)


def _ident(name: str) -> str:
    """Mermaid/DOT entity names allow a narrower character set than sheet names."""
    out = re.sub(r"[^A-Za-z0-9_]", "_", name)
    return out if out and not out[0].isdigit() else f"n_{out}"


def to_mermaid(graph: Graph, min_confidence: float = 0.5) -> str:
    lines = ["erDiagram"]
    interesting: dict[str, set[str]] = defaultdict(set)
    for n in graph.nodes:
        if n.key:
            interesting[n.id].add(n.key)
    for e in graph.edges:
        interesting[e.from_node].add(e.from_column)

    for n in graph.nodes:
        if not n.columns:
            continue
        lines.append(f"  {_ident(n.id)} {{")
        for c in n.columns:
            if c.name not in interesting[n.id]:
                continue
            marker = "PK" if c.name == n.key else "FK"
            lines.append(f"    {c.dtype} {_ident(c.name)} {marker}")
        lines.append("  }")

    for e in graph.edges:
        if e.confidence < min_confidence:
            continue
        # optional on the source side when the reference is nullable
        left = "||" if e.null_pct < 0.001 else "|o"
        right = "||" if e.cardinality == "1:1" else "o{"
        label = e.from_column + ("?" if e.kind == "name-only" else "")
        lines.append(
            f"  {_ident(e.to_node)} {left}--{right} {_ident(e.from_node)} : \"{label}\""
        )
    return "\n".join(lines)


def to_dot(graph: Graph, min_confidence: float = 0.5) -> str:
    lines = [
        "digraph dependencies {",
        "  rankdir=LR;",
        "  node [shape=box, style=rounded, fontname=Helvetica, fontsize=10];",
        "  edge [fontname=Helvetica, fontsize=8];",
    ]
    for n in graph.nodes:
        label = f"{n.id}\\n{n.rows:,} rows"
        if n.key:
            label += f"\\nkey: {n.key}"
        lines.append(f'  {_ident(n.id)} [label="{label}"];')
    for e in graph.edges:
        if e.confidence < min_confidence:
            continue
        style = "dashed" if e.kind == "name-only" else "solid"
        lines.append(
            f'  {_ident(e.from_node)} -> {_ident(e.to_node)} '
            f'[label="{e.from_column} ({e.confidence:.2f})", style={style}];'
        )
    lines.append("}")
    return "\n".join(lines)
