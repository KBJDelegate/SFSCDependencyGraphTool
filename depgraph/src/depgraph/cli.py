"""Command line entry point."""

from __future__ import annotations

import argparse
import os
import re
import shutil
import sys
import tempfile
import time
from pathlib import Path

from . import render
from .docs import write_docs
from .ingest import filter_members, ingest as run_ingest, list_sources, plan_parts
from .ingest import read_name_filter
from .infer import infer
from .profiles import PROFILES, get_profile


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="depgraph",
        description="Build an AI-agent-readable dependency graph, and a folder of "
        "Markdown docs per entity, from a zip (or directory) of tabular extracts. "
        "Zips inside zips are opened, and an object split across several zips is "
        "merged back into one.",
    )
    p.add_argument(
        "source",
        type=Path,
        nargs="+",
        help="extract .zip (which may hold more zips), a directory of sheets or "
        "zips, or several zips of one export",
    )
    p.add_argument(
        "-o",
        "--out",
        type=Path,
        help="JSON graph path (default: <source name>.json in the current directory)",
    )
    p.add_argument(
        "--mermaid", type=Path, help="Mermaid ER diagram path (default: beside the JSON)"
    )
    p.add_argument(
        "--dot",
        type=Path,
        metavar="PATH",
        help="also write a Graphviz DOT diagram here (off unless asked for)",
    )
    p.add_argument(
        "--docs",
        type=Path,
        metavar="DIR",
        help="folder for the Markdown docs, one page per entity "
        "(default: <JSON name>-docs beside the JSON)",
    )
    p.add_argument("--no-docs", action="store_true", help="do not write the docs folder")
    p.add_argument(
        "--no-values",
        action="store_true",
        help="keep data values out of the docs: no ranges, no picklist values, "
        "only structure and counts",
    )
    p.add_argument(
        "--json-only",
        action="store_true",
        help="write only the JSON: no diagrams, no docs",
    )
    p.add_argument(
        "--include-empty",
        action="store_true",
        help="keep sheets with no rows as graph nodes. Off by default: they "
        "carry no columns, key or references, so nothing is built from them.",
    )
    p.add_argument(
        "--skip-empty",
        action="store_true",
        help=argparse.SUPPRESS,  # now the default; accepted so old commands work
    )
    p.add_argument(
        "--include",
        type=Path,
        metavar="FILE",
        help="a text file listing the sheets to read, one per line "
        "('Account', 'Account.xlsx', blank lines and # comments allowed). "
        "Everything else is skipped without being read.",
    )
    p.add_argument(
        "--profile",
        default="salesforce",
        choices=sorted(PROFILES),
        help="export dialect (default: salesforce)",
    )
    p.add_argument("-j", "--workers", type=int, help="parallel readers (default: cpus)")
    p.add_argument(
        "--max-rows",
        type=int,
        help="read at most N rows per file (faster, approximate stats)",
    )
    p.add_argument(
        "--sample",
        type=int,
        default=500,
        help="rows sampled to decide if a column holds identifiers (default: 500)",
    )
    p.add_argument(
        "--min-confidence",
        type=float,
        default=0.5,
        help="drop inferred edges below this confidence (default: 0.5)",
    )
    p.add_argument(
        "--overlap-threshold",
        type=float,
        default=0.8,
        help="when name and prefix give no candidate, accept a target whose key "
        "covers at least this share of the values; 0 disables (default: 0.8)",
    )
    p.add_argument(
        "--staging",
        type=Path,
        help="staging dir, which also receives unpacked inner zips "
        "(default: a temp dir)",
    )
    p.add_argument(
        "--keep-staging", action="store_true", help="keep staged parquet for debugging"
    )
    p.add_argument("--indent", type=int, default=1, help="JSON indent; 0 for compact")
    p.add_argument("-q", "--quiet", action="store_true")
    return p


def default_stem(sources: list[Path]) -> str:
    """Output name for the sources: the source's own name, or for several parts of
    one export ("WE_00D_1.ZIP", "WE_00D_2.ZIP") the name they share ("WE_00D")."""
    stems = [s.stem or s.name for s in sources]
    if len(stems) == 1:
        return stems[0]
    common = os.path.commonprefix(stems)
    common = re.sub(r"[\s._-]*\d*[\s._-]*$", "", common)
    return common or "extract"


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    for src in args.source:
        if not src.exists():
            print(f"depgraph: {src} does not exist", file=sys.stderr)
            return 2

    # By default the outputs are named after the source, so a bare
    # `depgraph extract.zip` writes extract.json, extract.mmd and extract-docs/.
    out = args.out or Path(f"{default_stem(args.source)}.json")
    mermaid = args.mermaid or out.with_suffix(".mmd")
    dot = args.dot
    docs_dir = args.docs or out.with_name(f"{out.stem}-docs")
    if args.json_only:
        mermaid = dot = docs_dir = None
    if args.no_docs:
        docs_dir = None

    profile = get_profile(args.profile)
    log = (lambda *a: None) if args.quiet else (lambda *a: print(*a, file=sys.stderr))

    staging = args.staging or Path(tempfile.mkdtemp(prefix="depgraph-staging-"))
    started = time.perf_counter()

    def progress(done: int, total: int, member: str) -> None:
        log(f"  [{done}/{total}] {member}")

    try:
        names = ", ".join(str(s) for s in args.source)
        log(f"reading {names} (profile: {profile.name})")

        discovered = list_sources(args.source, staging / "archives", log)
        archives = len({m.path for m in discovered if m.name is not None})
        members = discovered
        unmatched: list[str] = []
        if args.include:
            if not args.include.exists():
                print(f"depgraph: {args.include} does not exist", file=sys.stderr)
                return 2
            wanted = read_name_filter(args.include)
            members, unmatched = filter_members(discovered, wanted)
            log(f"include list: {len(members)} of {len(discovered)} files selected")
            if unmatched:
                log(
                    f"  warning: {len(unmatched)} name(s) matched nothing: "
                    + ", ".join(unmatched[:10])
                    + (" ..." if len(unmatched) > 10 else "")
                )
            if not members:
                raise SystemExit(
                    f"none of the {len(wanted)} names in {args.include} matched a "
                    f"sheet in {names}"
                )
        if not members:
            raise SystemExit(f"no tabular files found in {names}")

        groups, skipped = plan_parts(members)
        split = {k: v for k, v in groups.items() if len(v) > 1}
        copies = sum(len(v) for v in skipped.values())
        log(
            f"{len(members)} files"
            + (f" in {archives} zip archives" if archives > 1 else "")
        )
        if split:
            log(
                f"{len(split)} objects are split across several files; their "
                f"{sum(len(v) for v in split.values())} parts are merged"
            )
        if copies:
            log(f"skipping {copies} file(s) that are identical copies of another part")

        nodes = run_ingest(
            args.source,
            staging,
            profile.name,
            workers=args.workers,
            max_rows=args.max_rows,
            sample_n=args.sample,
            progress=progress,
            members=members,
        )
        read_s = time.perf_counter() - started

        unreadable = [
            n for n in nodes if n.rows == 0 and any("unreadable" in w for w in n.warnings)
        ]
        for n in unreadable:
            log(f"  warning: could not read {n.source}: {n.warnings[-1]}")
        bad = {id(n) for n in unreadable}
        empty_names = sorted(n.id for n in nodes if n.rows == 0 and id(n) not in bad)
        if not args.include_empty:
            nodes = [n for n in nodes if n.rows > 0]
            if empty_names:
                log(f"excluded {len(empty_names)} sheets with no rows")
        if not nodes:
            raise SystemExit("every sheet was empty; nothing to graph")

        log("inferring relationships")
        graph = infer(
            nodes,
            profile,
            min_confidence=args.min_confidence,
            overlap_threshold=args.overlap_threshold,
        )
        graph.source = names
        total_rows = sum(n.rows for n in nodes)
        graph.stats = {
            "sheets": len(nodes),
            "rows": total_rows,
            "columns": sum(len(n.columns) for n in nodes),
            "edges": len(graph.edges),
            "unresolved": len(graph.unresolved),
            "files": len(members) - copies,
            "split_objects": sum(1 for n in nodes if len(n.parts) > 1),
            "read_seconds": round(read_s, 2),
            "total_seconds": round(time.perf_counter() - started, 2),
            "sampled": bool(args.max_rows),
            "include_list": str(args.include) if args.include else None,
            "unmatched_include_names": unmatched,
        }

        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(
            render.to_json(graph, indent=args.indent or None), encoding="utf-8"
        )
        written = [out]
        if mermaid:
            mermaid.write_text(
                render.to_mermaid(graph, args.min_confidence), encoding="utf-8"
            )
            written.append(mermaid)
        if dot:
            dot.write_text(render.to_dot(graph, args.min_confidence), encoding="utf-8")
            written.append(dot)
        if docs_dir:
            pages = write_docs(
                graph,
                docs_dir,
                values=not args.no_values,
                context={
                    "files": len(members) - copies,
                    "bytes": sum(m.size for p in groups.values() for m in p),
                    "archives": archives,
                    "copies": copies,
                    "excluded_empty": 0 if args.include_empty else len(empty_names),
                    "unreadable": [n.source for n in unreadable],
                },
            )
    finally:
        if not args.keep_staging and args.staging is None:
            shutil.rmtree(staging, ignore_errors=True)

    s = graph.stats
    log(
        f"\n{s['sheets']} sheets / {s['rows']:,} rows / {s['columns']} columns "
        f"in {s['total_seconds']}s"
    )
    log(f"{s['edges']} relationships, {s['unresolved']} pointing outside the extract")
    if graph.cycles:
        log(f"cycles: {', '.join(' <-> '.join(c) for c in graph.cycles)}")
    for path in written:
        log(f"wrote {path} ({path.stat().st_size:,} bytes)")
    if docs_dir:
        log(f"wrote {docs_dir}/ ({len(pages)} pages; start at {docs_dir / 'README.md'})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
