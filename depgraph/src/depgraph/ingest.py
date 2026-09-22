"""Pass 1: read every sheet once, profile its columns, stage only what pass 2 needs.

Memory is bounded by (workers x one sheet), not by the size of the extract.
That works because .xlsx caps a sheet at 1,048,576 rows, so a multi-GB extract
is always *many* bounded sheets rather than one huge one. Only the identifier
columns are staged to parquet, so the working set pass 2 joins over is tiny
even when the extract is enormous.
"""

from __future__ import annotations

import concurrent.futures as cf
import multiprocessing as mp
import os
import re
import shutil
import tempfile
import zipfile
from pathlib import Path

import polars as pl

from .model import ColumnStats, Node
from .profiles import Profile, get_profile

TABULAR = (".xlsx", ".xlsm", ".xlsb", ".csv", ".tsv")

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


def list_members(zip_path: Path) -> list[str]:
    """Tabular members of the zip, largest first so long jobs start early."""
    with zipfile.ZipFile(zip_path) as zf:
        infos = [
            i
            for i in zf.infolist()
            if not i.is_dir()
            and not i.filename.startswith(("__MACOSX", "."))
            and "/." not in i.filename
            and i.filename.lower().endswith(TABULAR)
        ]
    infos.sort(key=lambda i: -i.file_size)
    return [i.filename for i in infos]


def _safe(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]", "_", name)


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
    if dtype != "string" or nulls == rows:
        return stats

    non_null = s.drop_nulls()
    sample = non_null.head(sample_n).to_list()
    stats.samples = [str(v) for v in sample[:3]]
    stats.is_id_like = profile.looks_like_id([str(v) for v in sample])
    if not stats.is_id_like:
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


def _norm_name(raw: str) -> str:
    """Normalise a sheet name for matching: basename, no extension, lowercased.

    So "Account", "account.xlsx" and "exports/Account.XLSX" all match the zip
    member "Account.xlsx".
    """
    name = raw.strip().strip('"').strip("'").replace("\\", "/")
    name = name.rsplit("/", 1)[-1].lower()
    for ext in TABULAR:
        if name.endswith(ext):
            return name[: -len(ext)]
    return name


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


def filter_members(members: list[str], wanted: set[str]) -> tuple[list[str], list[str]]:
    """(members to read, names in the list that matched nothing)."""
    kept = [m for m in members if _norm_name(m) in wanted]
    matched = {_norm_name(m) for m in kept}
    return kept, sorted(wanted - matched)


def list_sources(path: Path) -> tuple[str | None, list[str]]:
    """(zip_path_or_None, members). A directory is read in place, no unpacking."""
    if path.is_dir():
        files = [
            str(f)
            for f in sorted(path.rglob("*"))
            if f.is_file()
            and f.suffix.lower() in TABULAR
            and not f.name.startswith((".", "~$"))
        ]
        return None, files
    return str(path), list_members(path)


def _ingest_member(
    zip_path: str | None,
    member: str,
    staging: str,
    profile_name: str,
    max_rows: int | None,
    sample_n: int,
) -> list[Node]:
    profile = get_profile(profile_name)

    if zip_path is None:
        return _ingest_file(
            Path(member), member, staging, profile, max_rows, sample_n
        )

    with tempfile.TemporaryDirectory(prefix="depgraph-") as tmp:
        local = Path(tmp) / Path(member).name
        with zipfile.ZipFile(zip_path) as zf, zf.open(member) as src, open(
            local, "wb"
        ) as dst:
            shutil.copyfileobj(src, dst, length=1 << 20)
        return _ingest_file(local, member, staging, profile, max_rows, sample_n)


def _ingest_file(
    local: Path,
    member: str,
    staging: str,
    profile: Profile,
    max_rows: int | None,
    sample_n: int,
) -> list[Node]:
    try:
        frames = _read_frames(local, max_rows)
    except Exception as exc:
        return [
            Node(
                id=Path(member).stem,
                source=member,
                sheet="",
                rows=0,
                warnings=[f"unreadable: {type(exc).__name__}: {exc}"],
            )
        ]

    nodes: list[Node] = []
    for sheet_name, df in frames:
        node_id = profile.node_name(member, sheet_name, len(frames))
        node = Node(id=node_id, source=member, sheet=sheet_name, rows=df.height)
        if df.height == 0 or df.width == 0:
            node.warnings.append("empty sheet")
            nodes.append(node)
            continue

        node.columns = [
            _profile_column(c, df[c], profile, sample_n) for c in df.columns
        ]
        node.key = _pick_key(node_id, node.columns, profile)
        if node.key:
            kc = node.column(node.key)
            if kc and kc.id_tokens:
                node.key_token = max(kc.id_tokens, key=kc.id_tokens.get)

        for col in node.columns:
            if not col.is_id_like:
                continue
            path = Path(staging) / f"{_safe(node_id)}__{_safe(col.name)}.parquet"
            _stage(df[col.name], path)
            node.staged[col.name] = str(path)

        nodes.append(node)
        del df

    return nodes


def ingest(
    source: Path,
    staging: Path,
    profile_name: str,
    workers: int | None = None,
    max_rows: int | None = None,
    sample_n: int = 500,
    progress=lambda *_: None,
    members: list[str] | None = None,
) -> list[Node]:
    zip_path, discovered = list_sources(source)
    if members is None:
        members = discovered
    if not members:
        raise SystemExit(f"no {', '.join(TABULAR)} files found in {source}")

    staging.mkdir(parents=True, exist_ok=True)
    workers = workers or min(len(members), (os.cpu_count() or 4))
    if len(members) == 1:
        workers = 1
    nodes: list[Node] = []
    args = (str(staging), profile_name, max_rows, sample_n)

    if workers == 1:
        for i, m in enumerate(members, 1):
            nodes += _ingest_member(zip_path, m, *args)
            progress(i, len(members), m)
        return _dedupe(nodes)

    # "spawn", not the POSIX default "fork": polars starts a Rayon threadpool on
    # import, and forking a parent that already holds its locks deadlocks the
    # children inside n_unique/write_parquet. Costs ~0.2s of one-off interpreter
    # startup, which is noise next to reading a large extract.
    try:
        with cf.ProcessPoolExecutor(
            max_workers=workers, mp_context=mp.get_context("spawn")
        ) as pool:
            futures = {
                pool.submit(_ingest_member, zip_path, m, *args): m for m in members
            }
            for i, fut in enumerate(cf.as_completed(futures), 1):
                nodes += fut.result()
                progress(i, len(members), futures[fut])
    except (RuntimeError, cf.process.BrokenProcessPool) as exc:
        # "spawn" re-imports the caller's main module, so a script that calls
        # ingest() at import time without an `if __name__ == "__main__"` guard
        # cannot start workers. Degrade to sequential rather than failing.
        progress(
            0,
            len(members),
            f"parallel read unavailable ({type(exc).__name__}), reading "
            "sequentially; guard your entry point with "
            'if __name__ == "__main__" to restore it',
        )
        nodes = []
        for i, m in enumerate(members, 1):
            nodes += _ingest_member(zip_path, m, *args)
            progress(i, len(members), m)

    return _dedupe(nodes)


def _dedupe(nodes: list[Node]) -> list[Node]:
    """Two files can imply the same node id; keep ids unique and stable."""
    seen: dict[str, int] = {}
    for n in sorted(nodes, key=lambda n: (n.source, n.sheet)):
        if n.id in seen:
            seen[n.id] += 1
            original = n.id
            n.id = f"{n.id}~{seen[original]}"
            n.warnings.append(f"duplicate node id, renamed from {original!r}")
        else:
            seen[n.id] = 0
    return nodes
