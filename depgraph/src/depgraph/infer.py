"""Pass 2: turn profiled columns into edges.

Candidate targets come from three independent signals — the type token inside
the value, the column's name, and dialect built-ins — and every candidate is
then *checked* by joining the staged identifier columns, so each edge carries a
measured resolve rate rather than a guess.
"""

from __future__ import annotations


import polars as pl

from .model import Edge, Graph, Node, Unresolved
from .profiles import Profile


def _sum_n(lf: pl.LazyFrame) -> int:
    val = lf.select(pl.col("n").sum()).collect().item()
    return int(val or 0)


def _resolve(src_path: str, tgt_path: str, token: str | None) -> tuple[float, int, int]:
    """Row-weighted share of source references that exist in the target key."""
    src = pl.scan_parquet(src_path)
    if token:
        src = src.filter(pl.col("v").str.slice(0, 3) == token)
    total = _sum_n(src)
    if total == 0:
        return 0.0, 0, 0
    tgt = pl.scan_parquet(tgt_path).select("v")
    matched = _sum_n(src.join(tgt, on="v", how="semi"))
    return matched / total, matched, total


#: At or above this resolve rate the relationship is treated as proven. Orphan
#: rows below it are a data-quality question, not a doubt about the schema.
VERIFIED_AT = 0.9


def _score(resolve: float, token_match: bool, name_match: bool, suffix: bool) -> float:
    """Confidence that this is the right target.

    The measured resolve rate dominates deliberately: whether a column *name*
    happens to contain the target's name is cosmetic, and weighting it heavily
    made fully-resolved edges look uncertain (Lead.ConvertedAccountId resolves
    100% but shares no name with Account).
    """
    conf = (
        0.70 * resolve
        + 0.20 * float(token_match)
        + 0.05 * float(name_match)
        + 0.05 * float(suffix)
    )
    return round(min(conf, 0.99), 3)


def infer(
    nodes: list[Node],
    profile: Profile,
    min_confidence: float = 0.5,
    min_token_share: float = 0.01,
    overlap_threshold: float = 0.8,
) -> Graph:
    graph = Graph(nodes=nodes, profile=profile.name)

    token_map: dict[str, str] = {}
    name_map: dict[str, str] = {}
    for n in nodes:
        if n.key_token and n.key:
            token_map.setdefault(n.key_token, n.id)
        name_map.setdefault(n.id.lower(), n.id)
        name_map.setdefault(n.id.lower().replace("_", ""), n.id)

    best: dict[tuple[str, str, str], Edge] = {}
    cache: dict[tuple[str, str, str | None], tuple[float, int, int]] = {}

    def resolve(src: str, tgt: str, token: str | None):
        key = (src, tgt, token)
        if key not in cache:
            cache[key] = _resolve(src, tgt, token)
        return cache[key]

    for node in nodes:
        for col in node.columns:
            if not col.is_id_like or col.name == node.key:
                continue
            staged_src = node.staged.get(col.name)
            if not staged_src:
                continue

            suffix = bool(profile.reference_base(col.name))
            base = profile.reference_base(col.name) or ""
            aliases = profile.name_aliases(base) if base else set()

            # --- candidate targets, remembering which signal produced each ---
            cands: dict[str, dict] = {}

            for token, rows in col.id_tokens.items():
                if rows / max(col.non_null, 1) < min_token_share:
                    continue
                target = token_map.get(token)
                if target:
                    c = cands.setdefault(target, {"token": token, "name": False})
                    c["token"] = token
                else:
                    guess = getattr(profile, "name_for_token", lambda _t: None)(token)
                    graph.unresolved.append(
                        Unresolved(
                            node=node.id,
                            column=col.name,
                            token=token,
                            rows=rows,
                            reason=(
                                f"key prefix {token} looks like {guess}, "
                                "which is not in this extract"
                                if guess
                                else f"key prefix {token} matches no sheet in this extract"
                            ),
                        )
                    )

            for alias in aliases:
                target = name_map.get(alias)
                if target:
                    cands.setdefault(target, {"token": None, "name": False})["name"] = True

            for builtin in profile.builtin_targets(col.name):
                target = name_map.get(builtin.lower())
                if target:
                    cands.setdefault(target, {"token": None, "name": False})["name"] = True

            # No signal from token or name: fall back to measuring value overlap
            # against every known key. The staged columns are tiny, so probing
            # them all is cheap, and this is what carries the generic profile.
            if not cands and overlap_threshold > 0:
                for tnode in nodes:
                    # The same node is a valid target: self-references like
                    # Account.ParentId -> Account.Id are real. The key column
                    # itself was already skipped above.
                    if not tnode.key:
                        continue
                    staged_tgt = tnode.staged.get(tnode.key)
                    if not staged_tgt:
                        continue
                    rate, _, _ = resolve(staged_src, staged_tgt, None)
                    if rate >= overlap_threshold:
                        cands[tnode.id] = {
                            "token": None, "name": False, "overlap": True
                        }

            if not cands and not col.id_tokens:
                graph.unresolved.append(
                    Unresolved(
                        node=node.id,
                        column=col.name,
                        token=None,
                        rows=col.non_null,
                        reason="identifier-shaped column with no matching sheet",
                    )
                )

            token_cands = [t for t, m in cands.items() if m["token"]]
            # A column whose values carry several type tokens is polymorphic even
            # if only one of those targets happens to be present in the extract.
            observed_tokens = sum(
                1
                for rows in col.id_tokens.values()
                if rows / max(col.non_null, 1) >= min_token_share
            )
            poly = (
                len(token_cands) > 1
                or observed_tokens > 1
                or (profile.is_polymorphic(col.name) and len(cands) > 1)
            )
            group = f"{node.id}.{col.name}" if poly else None

            for target, meta in cands.items():
                tnode = graph.node(target)
                if not tnode or not tnode.key:
                    continue
                staged_tgt = tnode.staged.get(tnode.key)
                if not staged_tgt:
                    continue

                rate, matched, total = resolve(staged_src, staged_tgt, meta["token"])
                conf = _score(rate, bool(meta["token"]), meta["name"], suffix)
                kind = "self" if target == node.id else "lookup"
                if rate == 0.0:
                    kind = "name-only"
                    conf = min(conf, 0.25)
                if conf < min_confidence:
                    continue

                ev = []
                if meta["token"]:
                    ev.append(f"key prefix {meta['token']} matches {target}.{tnode.key}")
                if meta["name"]:
                    ev.append(f"column name implies {target}")
                if meta.get("overlap"):
                    ev.append("matched by value overlap only, not by name or prefix")
                ev.append(f"{matched:,}/{total:,} references resolve ({rate:.1%})")
                if poly:
                    ev.append("column references more than one object")

                edge = Edge(
                    from_node=node.id,
                    from_column=col.name,
                    to_node=target,
                    to_column=tnode.key,
                    kind=kind,
                    cardinality="1:1" if col.unique else "N:1",
                    confidence=conf,
                    resolve_rate=round(rate, 4),
                    null_pct=round(col.nulls / max(col.rows, 1), 4),
                    verified=rate >= VERIFIED_AT,
                    evidence=ev,
                    polymorphic_group=group,
                )
                key = (node.id, col.name, target)
                if key not in best or best[key].confidence < conf:
                    best[key] = edge

    graph.edges = sorted(
        best.values(), key=lambda e: (-e.confidence, e.from_node, e.from_column)
    )
    graph.load_order, graph.cycles = _topology(graph)
    return graph


def _topology(graph: Graph) -> tuple[list[list[str]], list[list[str]]]:
    ids = [n.id for n in graph.nodes]
    deps: dict[str, set[str]] = {i: set() for i in ids}
    for e in graph.edges:
        if e.to_node != e.from_node and e.to_node in deps and e.from_node in deps:
            deps[e.from_node].add(e.to_node)

    placed: set[str] = set()
    layers: list[list[str]] = []
    remaining = set(ids)
    while remaining:
        layer = sorted(n for n in remaining if deps[n] <= placed)
        if not layer:
            break  # everything left is tangled in a cycle
        layers.append(layer)
        placed |= set(layer)
        remaining -= set(layer)

    cycles = _sccs({n: deps[n] & remaining for n in remaining}) if remaining else []
    if remaining:
        layers.append(sorted(remaining))
    return layers, cycles


def _sccs(adj: dict[str, set[str]]) -> list[list[str]]:
    """Tarjan, iterative, so a deep graph cannot blow the stack."""
    index: dict[str, int] = {}
    low: dict[str, int] = {}
    on_stack: set[str] = set()
    stack: list[str] = []
    out: list[list[str]] = []
    counter = 0

    for root in adj:
        if root in index:
            continue
        work: list[tuple[str, list[str]]] = [(root, sorted(adj[root]))]
        index[root] = low[root] = counter
        counter += 1
        stack.append(root)
        on_stack.add(root)

        while work:
            node, children = work[-1]
            if children:
                child = children.pop()
                if child not in index:
                    index[child] = low[child] = counter
                    counter += 1
                    stack.append(child)
                    on_stack.add(child)
                    work.append((child, sorted(adj.get(child, set()))))
                elif child in on_stack:
                    low[node] = min(low[node], index[child])
            else:
                work.pop()
                if work:
                    low[work[-1][0]] = min(low[work[-1][0]], low[node])
                if low[node] == index[node]:
                    comp = []
                    while True:
                        w = stack.pop()
                        on_stack.discard(w)
                        comp.append(w)
                        if w == node:
                            break
                    if len(comp) > 1:
                        out.append(sorted(comp))
    return out
