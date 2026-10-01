"""Pass 1: read every sheet once, profile its columns, stage only what pass 2 needs.

Memory is bounded by (workers x one file), not by the size of the extract.
That works because .xlsx caps a sheet at 1,048,576 rows and Salesforce caps
each zip of a data export at ~512 MB, so a multi-GB extract is always *many*
bounded files rather than one huge one. Only the identifier columns are staged
to parquet, so the working set pass 2 joins over is tiny even when the extract
is enormous.

An extract may be a zip of zips, a directory of zips, or several zips, and the
same object can be split across them (``Account.csv`` in both ``WE_1.zip`` and
``WE_2.zip``). Each part is still read on its own; the parts are combined
afterwards from mergeable statistics, so splitting never costs memory.
"""

from __future__ import annotations

import concurrent.futures as cf
import multiprocessing as mp
import os
import re
import shutil
import tempfile
import zipfile
from collections import Counter, defaultdict
from pathlib import Path

import polars as pl

from .model import ColumnStats, Member, Node
from .profiles import Profile, get_profile

TABULAR = (".xlsx", ".xlsm", ".xlsb", ".csv", ".tsv")
ARCHIVE = ".zip"

#: Zips inside zips are unpacked this many levels deep and no further.
MAX_NESTING = 4

#: A column with at most this many distinct values has every value counted, so
#: the docs can list picklist values. Kept small because it ships between
#: processes for every column.
TOP_VALUES = 20

#: Fixed seeds, so every worker process hashes a value to the same number.
_HASH_SEEDS = dict(seed=0, seed_1=1, seed_2=2, seed_3=3)

_DATE = re.compile(r"\d{4}-\d{2}-\d{2}")
_DATETIME = re.compile(
    r"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?"
)

_DTYPE = [
    (pl.Boolean, "bool"),
    (pl.Utf8, "string"),
    (pl.Null, "empty"),
]


def _dtype_name(dt: pl.DataType) -> str:
    if dt.is_integer():
        return "int"
    if dt.is_float():
        return "float"
    if dt.is_temporal():
        return "datetime"
    for pl_t, name in _DTYPE:
        if dt == pl_t:
            return name
    return str(dt).lower()


def _safe(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]", "_", name)


def _hidden(name: str) -> bool:
    name = name.replace("\\", "/")
    return (
        name.startswith(("__MACOSX", "."))
        or "/." in name
        or name.rsplit("/", 1)[-1].startswith("~$")
    )


# --- finding the files -------------------------------------------------------


def _walk_zip(
    path: Path, prefix: str, scratch: Path | None, depth: int, log
) -> list[Member]:
    """Tabular members of a zip, unpacking any zips inside it to ``scratch``."""
    out: list[Member] = []
    with zipfile.ZipFile(path) as zf:
        for info in zf.infolist():
            if info.is_dir() or _hidden(info.filename):
                continue
            low = info.filename.lower()
            label = prefix + info.filename
            if low.endswith(TABULAR):
                out.append(
                    Member(str(path), info.filename, label, info.file_size, info.CRC)
                )
            elif low.endswith(ARCHIVE):
                if depth >= MAX_NESTING:
                    log(f"  warning: not opening {label}: zips nested too deep")
                    continue
                if scratch is None:
                    raise ValueError(
                        f"{label} is a zip inside a zip; pass a scratch directory "
                        "to unpack it into"
                    )
                # Opened in place, a compressed inner zip would be decompressed
                # again for every backwards seek. One copy to disk is far cheaper.
                scratch.mkdir(parents=True, exist_ok=True)
                fd, local = tempfile.mkstemp(dir=scratch, suffix=".zip")
                log(f"  unpacking {label} ({info.file_size / 1e6:,.1f} MB)")
                with os.fdopen(fd, "wb") as dst, zf.open(info) as src:
                    shutil.copyfileobj(src, dst, length=1 << 20)
                try:
                    out += _walk_zip(Path(local), label + "!", scratch, depth + 1, log)
                except zipfile.BadZipFile:
                    log(f"  warning: {label} is not a readable zip, skipped")
    return out


def list_sources(
    sources: Path | str | list[Path],
    scratch: Path | None = None,
    log=lambda *_: None,
) -> list[Member]:
    """Every tabular file in the given zips and directories, nested zips included.

    ``scratch`` receives the unpacked copies of zips found inside zips; it is
    only touched when there are any.
    """
    if isinstance(sources, (str, Path)):
        sources = [Path(sources)]
    sources = [Path(s) for s in sources]
    # With several sources, prefix labels so Account.csv from each stays apart.
    several = len(sources) > 1

    out: list[Member] = []
    for src in sources:
        prefix = f"{src.name}!" if several else ""
        if src.is_dir():
            dprefix = f"{src.name}/" if several else ""
            for f in sorted(src.rglob("*")):
                rel = f.relative_to(src).as_posix()
                if not f.is_file() or _hidden(rel):
                    continue
                low = f.name.lower()
                if low.endswith(TABULAR):
                    out.append(Member(str(f), None, dprefix + rel, f.stat().st_size))
                elif low.endswith(ARCHIVE):
                    try:
                        out += _walk_zip(f, f"{dprefix}{rel}!", scratch, 1, log)
                    except zipfile.BadZipFile:
                        log(f"  warning: {dprefix}{rel} is not a readable zip, skipped")
        elif src.name.lower().endswith(TABULAR):  # before is_zipfile: xlsx is a zip
            out.append(Member(str(src), None, src.name, src.stat().st_size))
        elif src.suffix.lower() == ARCHIVE or zipfile.is_zipfile(src):
            out += _walk_zip(src, prefix, scratch, 0, log)
    return out


def _norm_name(raw: str) -> str:
    """Normalise a sheet name for matching: basename, no extension, lowercased.

    So "Account", "account.xlsx" and "exports/Account.XLSX" all match the zip
    member "Account.xlsx".
    """
    name = raw.strip().strip('"').strip("'").replace("\\", "/")
    name = name.rsplit("!", 1)[-1].rsplit("/", 1)[-1].lower()
    for ext in TABULAR:
        if name.endswith(ext):
            return name[: -len(ext)]
    return name


def _member_key(m: Member | str) -> str:
    return _norm_name(m.basename if isinstance(m, Member) else m)


def read_name_filter(path: Path) -> set[str]:
    """Read a plain-text list of wanted sheets, one per line.

    Blank lines are skipped and everything after a '#' is a comment.
    """
    wanted: set[str] = set()
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        line = line.split("#", 1)[0]
        name = _norm_name(line)
        if name:
            wanted.add(name)
    return wanted


def filter_members(members: list, wanted: set[str]) -> tuple[list, list[str]]:
    """(members to read, names in the list that matched nothing)."""
    kept = [m for m in members if _member_key(m) in wanted]
    matched = {_member_key(m) for m in kept}
    return kept, sorted(wanted - matched)


def plan_parts(
    members: list[Member],
) -> tuple[dict[str, list[Member]], dict[str, list[str]]]:
    """Group files by the object they hold, dropping byte-identical copies.

    Returns (object key -> parts to read, object key -> labels of skipped
    copies). Two files are the same copy when the zip directory gives them the
    same CRC-32 and size; reading both would double every count.
    """
    groups: dict[str, list[Member]] = defaultdict(list)
    skipped: dict[str, list[str]] = defaultdict(list)
    for m in sorted(members, key=lambda m: m.label):
        key = _member_key(m)
        if m.crc is not None and any(
            (p.crc, p.size) == (m.crc, m.size) for p in groups[key]
        ):
            skipped[key].append(m.label)
            continue
        groups[key].append(m)
    return dict(groups), dict(skipped)


# --- reading one file ---------------------------------------------------------


def _read_frames(path: Path, max_rows: int | None) -> list[tuple[str, pl.DataFrame]]:
    """(sheet_name, frame) for one file. One frame in memory at a time."""
    suffix = path.suffix.lower()
    if suffix in (".csv", ".tsv"):
        sep = "\t" if suffix == ".tsv" else ","
        df = pl.read_csv(
            path,
            separator=sep,
            n_rows=max_rows,
            infer_schema_length=10_000,
            ignore_errors=True,
            truncate_ragged_lines=True,
        )
        return [(path.stem, df)]

    import fastexcel

    wb = fastexcel.read_excel(str(path))
    out: list[tuple[str, pl.DataFrame]] = []
    for name in wb.sheet_names:
        sheet = wb.load_sheet_by_name(name, header_row=0, n_rows=max_rows)
        df = sheet.to_polars()
        if isinstance(df, pl.Series):
            df = df.to_frame()
        out.append((name, df))
    return out


def _date_kind(values: list[str]) -> str | None:
    """'date' or 'datetime' when the sampled text is ISO dates, else None.

    CSV exports carry dates as text; recognising them lets the docs give a
    date range instead of calling CreatedDate a string.
    """
    if not values:
        return None
    if all(_DATE.fullmatch(v) for v in values):
        return "date"
    hits = sum(1 for v in values if _DATETIME.fullmatch(v) or _DATE.fullmatch(v))
    return "datetime" if hits / len(values) >= 0.95 else None


def _profile_column(
    name: str, s: pl.Series, profile: Profile, sample_n: int
) -> ColumnStats:
    rows = s.len()
    nulls = int(s.null_count())
    distinct = int(s.n_unique()) - (1 if nulls else 0)
    dtype = _dtype_name(s.dtype)

    stats = ColumnStats(
        name=name, dtype=dtype, rows=rows, nulls=nulls, distinct=max(distinct, 0)
    )
    if nulls == rows:
        return stats

    non_null = s.drop_nulls()
    if 0 < stats.distinct <= TOP_VALUES:
        vc = non_null.value_counts(sort=True)
        stats.top_values = {
            str(v): int(n) for v, n in zip(vc[vc.columns[0]], vc[vc.columns[1]])
        }
    if dtype in ("int", "float", "datetime"):
        try:
            stats.min, stats.max = non_null.min(), non_null.max()
        except Exception:  # durations and other exotic types have no useful range
            pass
    if dtype != "string":
        return stats

    sample = [str(v) for v in non_null.head(sample_n).to_list()]
    stats.samples = sample[:3]
    stats.max_length = int(non_null.str.len_chars().max() or 0)
    stats.is_id_like = profile.looks_like_id(sample)
    if not stats.is_id_like:
        kind = _date_kind(sample)
        if kind:
            stats.dtype = kind
            stats.min, stats.max = non_null.min(), non_null.max()
        return stats

    stats.dtype = "id"
    if profile.id_token("0" * 18) is not None or profile.id_token("0" * 15) is not None:
        # Dialect encodes the target type in the value: count tokens over all rows.
        tok = (
            non_null.filter(non_null.str.len_chars().is_in([15, 18]))
            .str.slice(0, 3)
            .value_counts(sort=True)
            .head(12)
        )
        if tok.height:
            cols = tok.columns
            stats.id_tokens = {
                str(r[cols[0]]): int(r[cols[1]]) for r in tok.iter_rows(named=True)
            }
    return stats


def _stage(s: pl.Series, path: Path) -> None:
    """Write distinct value -> row count. Shrinks a 1M-row column to its cardinality."""
    vc = s.drop_nulls().cast(pl.Utf8).value_counts()
    vc.columns = ["v", "n"]
    vc.write_parquet(path, compression="zstd")


def _stage_hashes(s: pl.Series, path: Path) -> None:
    """Write the distinct value hashes, so parts can be counted together exactly."""
    h = s.drop_nulls().cast(pl.Utf8).hash(**_HASH_SEEDS).unique()
    pl.DataFrame({"h": h}).write_parquet(path, compression="zstd")


def _ingest_member(
    member: Member,
    part_no: int,
    split: bool,
    staging: str,
    profile_name: str,
    max_rows: int | None,
    sample_n: int,
) -> list[Node]:
    profile = get_profile(profile_name)

    if member.name is None:
        return _ingest_file(
            Path(member.path), member, part_no, split, staging, profile, max_rows,
            sample_n,
        )

    with tempfile.TemporaryDirectory(prefix="depgraph-") as tmp:
        local = Path(tmp) / _safe(member.basename)
        with zipfile.ZipFile(member.path) as zf, zf.open(member.name) as src, open(
            local, "wb"
        ) as dst:
            shutil.copyfileobj(src, dst, length=1 << 20)
        return _ingest_file(
            local, member, part_no, split, staging, profile, max_rows, sample_n
        )


def _ingest_file(
    local: Path,
    member: Member,
    part_no: int,
    split: bool,
    staging: str,
    profile: Profile,
    max_rows: int | None,
    sample_n: int,
) -> list[Node]:
    base = dict(source=member.label, parts=[member.label], size_bytes=member.size)
    try:
        frames = _read_frames(local, max_rows)
    except Exception as exc:
        return [
            Node(
                id=Path(member.basename).stem,
                sheet="",
                rows=0,
                warnings=[f"unreadable: {type(exc).__name__}: {exc}"],
                **base,
            )
        ]

    nodes: list[Node] = []
    for sheet_no, (sheet_name, df) in enumerate(frames):
        node_id = profile.node_name(member.basename, sheet_name, len(frames))
        node = Node(id=node_id, sheet=sheet_name, rows=df.height, **base)
        if df.height == 0 or df.width == 0:
            node.warnings.append("empty sheet")
            nodes.append(node)
            continue

        node.columns = [
            _profile_column(c, df[c], profile, sample_n) for c in df.columns
        ]
        # Column index, not just name, keeps "A b" and "A_b" from colliding.
        stem = f"p{part_no:05d}s{sheet_no}_{_safe(node_id)}"
        for i, col in enumerate(node.columns):
            if col.is_id_like:
                path = Path(staging) / f"{stem}__{i}_{_safe(col.name)}.parquet"
                _stage(df[col.name], path)
                node.staged[col.name] = str(path)
            if split and col.non_null:
                path = Path(staging) / f"{stem}__{i}_{_safe(col.name)}.hash.parquet"
                _stage_hashes(df[col.name], path)
                node.hashed[col.name] = str(path)

        nodes.append(node)
        del df

    return nodes


# --- combining the parts of a split object -----------------------------------


def _pick_key(node_id: str, columns: list[ColumnStats], profile: Profile) -> str | None:
    ranked = sorted(
        ((profile.key_rank(c.name, node_id), i, c) for i, c in enumerate(columns)),
        key=lambda t: (t[0], t[1]),
    )
    for rank, _, col in ranked:
        if rank < 99 and col.unique and col.non_null:
            return col.name
    # Fall back to the first unique identifier-shaped column.
    for col in columns:
        if col.is_id_like and col.unique and col.non_null:
            return col.name
    return None


def _extreme(values: list, pick):
    values = [v for v in values if v is not None]
    if not values:
        return None
    try:
        return pick(values)
    except TypeError:  # a date read as text in one part, as a datetime in another
        return pick(values, key=str)


def _merge_dtype(dtypes: list[str], id_like: bool) -> str:
    kinds = set(dtypes) - {"empty"}
    if id_like:
        return "id"
    kinds.discard("id")
    if not kinds:
        return dtypes[0] if dtypes else "empty"
    if len(kinds) == 1:
        return kinds.pop()
    if kinds <= {"int", "float"}:
        return "float"
    if kinds <= {"date", "datetime"}:
        return "datetime"
    return "string"


def _merge_column(
    name: str, rows: int, present: list[tuple[Node, ColumnStats]], out_stem: Path
) -> tuple[ColumnStats, str | None]:
    """One column's statistics over every part, plus its merged staging file."""
    cols = [c for _, c in present]
    filled = [c for c in cols if c.non_null]
    non_null = sum(c.non_null for c in filled)

    id_rows = sum(c.non_null for c in filled if c.is_id_like)
    id_like = bool(filled) and id_rows * 2 >= non_null
    stats = ColumnStats(
        name=name,
        dtype=_merge_dtype([c.dtype for c in filled] or [cols[0].dtype], id_like),
        rows=rows,
        # A part without this column contributes its rows as empty.
        nulls=rows - non_null,
        distinct=0,
        is_id_like=id_like,
        samples=next((c.samples for c in filled if c.samples), []),
        min=_extreme([c.min for c in filled], min),
        max=_extreme([c.max for c in filled], max),
        max_length=max((c.max_length for c in filled if c.max_length), default=None),
    )

    tokens: Counter[str] = Counter()
    for c in filled:
        tokens.update(c.id_tokens)
    stats.id_tokens = dict(tokens.most_common(12))

    if filled and all(c.top_values is not None for c in filled):
        values: Counter[str] = Counter()
        for c in filled:
            values.update(c.top_values)
        if len(values) <= TOP_VALUES:
            stats.top_values = dict(values.most_common())

    hashes = [p.hashed[name] for p, _ in present if name in p.hashed]
    if hashes:
        stats.distinct = int(
            pl.scan_parquet(hashes).select(pl.col("h").n_unique()).collect().item()
        )
    elif filled:  # not staged (should not happen); a lower bound beats nothing
        stats.distinct = max(c.distinct for c in filled)

    staged = [p.staged[name] for p, _ in present if name in p.staged]
    if not id_like or not staged:
        return stats, None
    if len(staged) == 1:
        return stats, staged[0]
    path = f"{out_stem}__{_safe(name)}.parquet"
    (
        pl.scan_parquet(staged)
        .group_by("v")
        .agg(pl.col("n").sum())
        .collect()
        .write_parquet(path, compression="zstd")
    )
    return stats, path


def _merge(parts: list[Node], staging: Path, group_no: int) -> Node:
    parts = sorted(parts, key=lambda n: n.source)
    first = parts[0]
    node = Node(
        id=first.id,
        source=first.source,
        sheet=first.sheet,
        rows=sum(p.rows for p in parts),
        parts=[p.source for p in parts],
        size_bytes=sum(p.size_bytes for p in parts),
    )
    for p in parts:
        node.warnings += [f"{p.source}: {w}" for w in p.warnings if w != "empty sheet"]

    by_name = [{c.name: c for c in p.columns} for p in parts]
    names = list(dict.fromkeys(c.name for p in parts for c in p.columns))
    out_stem = staging / f"m{group_no:05d}_{_safe(node.id)}"
    for name in names:
        present = [(p, cols[name]) for p, cols in zip(parts, by_name) if name in cols]
        stats, staged = _merge_column(name, node.rows, present, out_stem)
        node.columns.append(stats)
        if staged:
            node.staged[name] = staged

    if node.rows == 0 and any("empty sheet" in p.warnings for p in parts):
        node.warnings.insert(0, "empty sheet")
    readable = [cols for p, cols in zip(parts, by_name) if p.columns]
    missing = [n for n in names if any(n not in cols for cols in readable)]
    if missing:
        node.warnings.append(
            f"{len(missing)} column(s) missing from some parts, counted as empty "
            f"there: {', '.join(missing[:10])}" + (" ..." if len(missing) > 10 else "")
        )
    return node


def _finish(node: Node, profile: Profile, by_part: list[Node]) -> None:
    """Pick the key once the statistics cover the whole object."""
    node.key = _pick_key(node.id, node.columns, profile)
    if node.key is None and len(by_part) > 1:
        # Unique within every part but not across them: the parts overlap. Still
        # the key, and worth saying so, because it means rows are duplicated.
        for col in node.columns:
            if profile.key_rank(col.name, node.id) >= 99 or not col.non_null:
                continue
            if all(
                (pc := p.column(col.name)) is None or pc.unique or not pc.non_null
                for p in by_part
            ):
                node.key = col.name
                node.warnings.append(
                    f"key {col.name} repeats {col.non_null - col.distinct:,} "
                    "value(s) across parts, so the parts overlap and some rows are "
                    "counted twice"
                )
                break
    if node.key:
        kc = node.column(node.key)
        if kc and kc.id_tokens:
            node.key_token = max(kc.id_tokens, key=kc.id_tokens.get)


def _combine(
    part_nodes: list[Node],
    staging: Path,
    profile: Profile,
    skipped: dict[str, list[str]],
) -> list[Node]:
    groups: dict[str, list[Node]] = defaultdict(list)
    for n in part_nodes:
        groups[n.id.lower()].append(n)

    out: list[Node] = []
    for gi, key in enumerate(sorted(groups)):
        parts = groups[key]
        node = parts[0] if len(parts) == 1 else _merge(parts, staging, gi)
        _finish(node, profile, parts)
        copies = {c for p in parts for c in skipped.get(_member_key(p.source), [])}
        for label in sorted(copies):
            node.warnings.append(f"skipped {label}: an identical copy of another part")
        out.append(node)
    return sorted(out, key=lambda n: n.id.lower())


def ingest(
    source: Path | list[Path],
    staging: Path,
    profile_name: str,
    workers: int | None = None,
    max_rows: int | None = None,
    sample_n: int = 500,
    progress=lambda *_: None,
    members: list[Member] | None = None,
) -> list[Node]:
    """Read every object in ``source`` into a profiled Node.

    ``members`` (from ``list_sources``, perhaps filtered) skips the discovery
    step; nested zips are unpacked into ``staging``.
    """
    staging.mkdir(parents=True, exist_ok=True)
    if members is None:
        members = list_sources(source, staging / "archives")
    if not members:
        raise SystemExit(f"no {', '.join(TABULAR)} files found in {source}")

    groups, skipped = plan_parts(members)
    tasks = [(m, len(parts) > 1) for parts in groups.values() for m in parts]
    # Largest first, so the long jobs start early.
    tasks.sort(key=lambda t: -t[0].size)
    profile = get_profile(profile_name)

    workers = workers or min(len(tasks), (os.cpu_count() or 4))
    if len(tasks) == 1:
        workers = 1
    nodes: list[Node] = []
    args = (str(staging), profile_name, max_rows, sample_n)

    def sequential() -> list[Node]:
        out: list[Node] = []
        for i, (m, split) in enumerate(tasks, 1):
            out += _ingest_member(m, i, split, *args)
            progress(i, len(tasks), m.label)
        return out

    if workers == 1:
        return _combine(sequential(), staging, profile, skipped)

    # "spawn", not the POSIX default "fork": polars starts a Rayon threadpool on
    # import, and forking a parent that already holds its locks deadlocks the
    # children inside n_unique/write_parquet. Costs ~0.2s of one-off interpreter
    # startup, which is noise next to reading a large extract.
    try:
        with cf.ProcessPoolExecutor(
            max_workers=workers, mp_context=mp.get_context("spawn")
        ) as pool:
            futures = {
                pool.submit(_ingest_member, m, i, split, *args): m
                for i, (m, split) in enumerate(tasks, 1)
            }
            for i, fut in enumerate(cf.as_completed(futures), 1):
                nodes += fut.result()
                progress(i, len(tasks), futures[fut].label)
    except (RuntimeError, cf.process.BrokenProcessPool) as exc:
        # "spawn" re-imports the caller's main module, so a script that calls
        # ingest() at import time without an `if __name__ == "__main__"` guard
        # cannot start workers. Degrade to sequential rather than failing.
        progress(
            0,
            len(tasks),
            f"parallel read unavailable ({type(exc).__name__}), reading "
            "sequentially; guard your entry point with "
            'if __name__ == "__main__" to restore it',
        )
        nodes = sequential()

    return _combine(nodes, staging, profile, skipped)
