"""Command line entry point."""

from __future__ import annotations

import argparse
import shutil
import sys
import tempfile
import time
from pathlib import Path

from . import render
from .ingest import filter_members, ingest as run_ingest, list_sources, read_name_filter
from .infer import infer
from .profiles import PROFILES, get_profile


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="depgraph",
        description="Build an AI-agent-readable dependency graph from a zip "
        "(or directory) of tabular extracts.",
    )
    p.add_argument("source", type=Path, help="extract .zip, or a directory of sheets")
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
        "--dot", type=Path, help="Graphviz DOT diagram path (default: beside the JSON)"
    )
    p.add_argument(
        "--json-only", action="store_true", help="skip the two diagram files"
    )
    p.add_argument(
        "--skip-empty",
        action="store_true",
        help="leave out sheets with no rows; a full org export ships every "
        "object definition, and most of them are empty",
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
        help="read at most N rows per sheet (faster, approximate stats)",
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
    p.add_argument("--staging", type=Path, help="staging dir (default: a temp dir)")
    p.add_argument(
        "--keep-staging", action="store_true", help="keep staged parquet for debugging"
    )
    p.add_argument("--indent", type=int, default=1, help="JSON indent; 0 for compact")
    p.add_argument("-q", "--quiet", action="store_true")
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if not args.source.exists():
        print(f"depgraph: {args.source} does not exist", file=sys.stderr)
        return 2

    # By default every output is named after the source, so a bare
    # `depgraph extract.zip` writes extract.json, extract.mmd and extract.dot.
    out = args.out or Path(f"{args.source.stem or args.source.name}.json")
    mermaid = args.mermaid or (None if args.json_only else out.with_suffix(".mmd"))
    dot = args.dot or (None if args.json_only else out.with_suffix(".dot"))

    profile = get_profile(args.profile)
    log = (lambda *a: None) if args.quiet else (lambda *a: print(*a, file=sys.stderr))

    staging = args.staging or Path(tempfile.mkdtemp(prefix="depgraph-staging-"))
    started = time.perf_counter()

    def progress(done: int, total: int, member: str) -> None:
        log(f"  [{done}/{total}] {member}")

    try:
        log(f"reading {args.source} (profile: {profile.name})")

        members = None
        unmatched: list[str] = []
        if args.include:
            if not args.include.exists():
                print(f"depgraph: {args.include} does not exist", file=sys.stderr)
                return 2
            wanted = read_name_filter(args.include)
            _, discovered = list_sources(args.source)
            members, unmatched = filter_members(discovered, wanted)
            log(
                f"include list: {len(members)} of {len(discovered)} sheets selected"
            )
            if unmatched:
                log(
                    f"  warning: {len(unmatched)} name(s) matched nothing: "
                    + ", ".join(unmatched[:10])
                    + (" ..." if len(unmatched) > 10 else "")
                )
            if not members:
                raise SystemExit(
                    f"none of the {len(wanted)} names in {args.include} matched a "
                    f"sheet in {args.source}"
                )

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

        skipped_empty = 0
        if args.skip_empty:
            kept = [n for n in nodes if n.rows > 0]
            skipped_empty = len(nodes) - len(kept)
            nodes = kept
            log(f"skipped {skipped_empty} empty sheets")
            if not nodes:
                raise SystemExit("every sheet was empty; nothing to graph")

        log("inferring relationships")
        graph = infer(
            nodes,
            profile,
            min_confidence=args.min_confidence,
            overlap_threshold=args.overlap_threshold,
        )
        graph.source = str(args.source)
        total_rows = sum(n.rows for n in nodes)
        graph.stats = {
            "sheets": len(nodes),
            "rows": total_rows,
            "columns": sum(len(n.columns) for n in nodes),
            "edges": len(graph.edges),
            "unresolved": len(graph.unresolved),
            "read_seconds": round(read_s, 2),
            "total_seconds": round(time.perf_counter() - started, 2),
            "sampled": bool(args.max_rows),
            "skipped_empty": skipped_empty,
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
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
