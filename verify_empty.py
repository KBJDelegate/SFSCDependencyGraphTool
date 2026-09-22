"""Independently count data rows in every sheet of an extract zip.

Deliberately uses nothing but the standard library: it counts <row> elements in
the raw worksheet XML, so it does not trust depgraph, polars or fastexcel.

    python verify_empty.py "extract.ZIP"
    python verify_empty.py "extract.ZIP" --list-empty
"""

import sys
import zipfile
from io import BytesIO


def data_rows(xlsx_bytes: bytes) -> int:
    """Rows in the first worksheet, minus the header."""
    with zipfile.ZipFile(BytesIO(xlsx_bytes)) as wb:
        sheets = sorted(
            n for n in wb.namelist() if n.startswith("xl/worksheets/sheet")
        )
        if not sheets:
            return 0
        total = 0
        with wb.open(sheets[0]) as fh:
            tail = b""
            while chunk := fh.read(1 << 20):
                buf = tail + chunk
                total += buf.count(b"<row ") + buf.count(b"<row>")
                tail = buf[-8:]
    return max(total - 1, 0)  # drop the header row


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__)
        return 2
    path, show = argv[0], "--list-empty" in argv
    empty, populated, rows = [], [], 0
    with zipfile.ZipFile(path) as zf:
        members = [
            i for i in zf.infolist()
            if not i.is_dir() and i.filename.lower().endswith((".xlsx", ".xlsm"))
        ]
        for i, info in enumerate(members, 1):
            n = data_rows(zf.read(info.filename))
            name = info.filename.rsplit("/", 1)[-1].rsplit(".", 1)[0]
            (populated if n else empty).append((name, n))
            rows += n
            if i % 100 == 0:
                print(f"  ...{i}/{len(members)}", file=sys.stderr)

    print(f"\nsheets in zip:     {len(members)}")
    print(f"with data:         {len(populated)}")
    print(f"empty (0 rows):    {len(empty)}")
    print(f"total data rows:   {rows:,}")

    print("\nlargest sheets:")
    for name, n in sorted(populated, key=lambda t: -t[1])[:10]:
        print(f"  {n:>10,}  {name}")

    watch = ["Case", "Campaign", "Order", "Asset", "Contract",
             "Account", "Contact", "Lead", "Opportunity"]
    print("\nspot-check:")
    lookup = dict(populated + empty)
    for w in watch:
        if w in lookup:
            print(f"  {w:<14} {lookup[w]:>10,} rows")
        else:
            print(f"  {w:<14} (no such sheet in the zip)")

    if show:
        print(f"\nall {len(empty)} empty sheets:")
        for name, _ in sorted(empty):
            print(f"  {name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
