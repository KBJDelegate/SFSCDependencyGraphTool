"""Generate a synthetic Salesforce-shaped extract zip.

Deliberately includes the awkward cases: a self-reference (Account.ParentId),
polymorphic columns (Task.WhatId/WhoId), references to objects absent from the
extract (User.ProfileId), dangling ids that will not resolve, nullable
lookups, and a custom object with __c lookups. ``build_split`` lays the same
org out the way a large Salesforce data export does: zips inside a zip, with
objects split across them.
"""

from __future__ import annotations

import csv
import random
import string
import time
import zipfile
from pathlib import Path

B62 = string.ascii_letters + string.digits


def make_ids(prefix: str, n: int, rng: random.Random) -> list[str]:
    seen: set[str] = set()
    while len(seen) < n:
        seen.add(prefix + "".join(rng.choice(B62) for _ in range(12)))
    return sorted(seen)


def sheets(seed: int = 7) -> dict[str, tuple[list[str], list[list]]]:
    """Object name -> (headers, rows) for the whole synthetic org."""
    rng = random.Random(seed)

    users = make_ids("005", 100, rng)
    accounts = make_ids("001", 500, rng)
    contacts = make_ids("003", 2000, rng)
    opps = make_ids("006", 1500, rng)
    tasks = make_ids("00T", 3000, rng)
    projects = make_ids("a01", 200, rng)
    leads = make_ids("00Q", 300, rng)          # referenced, never exported
    profiles = make_ids("00e", 8, rng)         # referenced, never exported
    pricebooks = make_ids("01s", 3, rng)       # referenced, never exported
    ghost_accounts = make_ids("001", 40, rng)  # look real, resolve to nothing

    def pick(pool):
        return rng.choice(pool)

    out: dict[str, tuple[list[str], list[list]]] = {}
    # A real Salesforce CSV export carries dates as ISO text.
    epoch = 1_546_300_800  # 2019-01-01

    def stamp() -> str:
        t = time.gmtime(epoch + rng.randrange(5 * 365 * 86400))
        return time.strftime("%Y-%m-%dT%H:%M:%S.000Z", t)

    out["User"] = (
        ["Id", "Name", "Email", "ManagerId", "ProfileId", "IsActive"],
        [
            [
                u,
                f"User {i}",
                f"user{i}@example.com",
                pick(users) if i > 5 else None,
                pick(profiles),
                rng.random() > 0.1,
            ]
            for i, u in enumerate(users)
        ],
    )

    out["Account"] = (
        ["Id", "Name", "Type", "OwnerId", "ParentId", "AnnualRevenue"],
        [
            [
                a,
                f"Account {i}",
                pick(["Customer", "Partner", "Prospect"]),
                pick(users),
                pick(accounts) if rng.random() < 0.3 else None,
                round(rng.uniform(1e4, 1e8), 2),
            ]
            for i, a in enumerate(accounts)
        ],
    )

    out["Contact"] = (
        [
            "Id", "AccountId", "Name", "Email", "ReportsToId", "OwnerId",
            "CreatedById", "CreatedDate",
        ],
        [
            [
                c,
                # ~2% dangling, ~3% null: exercises resolve_rate and null_pct
                None
                if rng.random() < 0.03
                else (pick(ghost_accounts) if rng.random() < 0.02 else pick(accounts)),
                f"Contact {i}",
                f"contact{i}@example.com",
                pick(contacts) if rng.random() < 0.25 else None,
                pick(users),
                pick(users),
                stamp(),
            ]
            for i, c in enumerate(contacts)
        ],
    )

    out["Opportunity"] = (
        ["Id", "Name", "AccountId", "OwnerId", "StageName", "Amount", "Pricebook2Id"],
        [
            [
                o,
                f"Opp {i}",
                pick(accounts),
                pick(users),
                pick(["Prospecting", "Closed Won", "Closed Lost"]),
                round(rng.uniform(500, 500000), 2),
                pick(pricebooks),
            ]
            for i, o in enumerate(opps)
        ],
    )

    out["Task"] = (
        ["Id", "Subject", "WhatId", "WhoId", "OwnerId", "Status"],
        [
            [
                t,
                pick(["Call", "Email", "Meeting"]),
                pick(accounts) if rng.random() < 0.6 else pick(opps),
                pick(contacts) if rng.random() < 0.7 else pick(leads),
                pick(users),
                pick(["Open", "Completed"]),
            ]
            for t in tasks
        ],
    )

    out["Custom_Project__c"] = (
        ["Id", "Name", "Account__c", "Primary_Contact__c", "OwnerId", "Budget__c"],
        [
            [
                p,
                f"Project {i}",
                pick(accounts),
                pick(contacts) if rng.random() < 0.8 else None,
                pick(users),
                round(rng.uniform(1000, 900000), 2),
            ]
            for i, p in enumerate(projects)
        ],
    )

    return out


def write_xlsx(path: Path, name: str, headers: list[str], rows: list[list]) -> Path:
    import xlsxwriter

    wb = xlsxwriter.Workbook(str(path), {"constant_memory": True})
    ws = wb.add_worksheet(name[:31])
    for c, h in enumerate(headers):
        ws.write(0, c, h)
    for r, row in enumerate(rows, start=1):
        for c, val in enumerate(row):
            if val is not None:
                ws.write(r, c, val)
    wb.close()
    return path


def write_csv(path: Path, headers: list[str], rows: list[list]) -> Path:
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(headers)
        w.writerows(["" if v is None else v for v in row] for row in rows)
    return path


def build(dest: Path, seed: int = 7) -> Path:
    work = dest.parent / f"{dest.stem}-sheets"
    work.mkdir(parents=True, exist_ok=True)
    paths = [
        write_xlsx(work / f"{name}.xlsx", name, headers, rows)
        for name, (headers, rows) in sheets(seed).items()
    ]

    dest.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as zf:
        for path in paths:
            zf.write(path, arcname=path.name)
    return dest


def build_split(
    dest: Path,
    seed: int = 7,
    overlap: int = 0,
    drop_column: str | None = None,
    nested: bool = True,
) -> Path:
    """The same org as ``build``, shaped like a large Salesforce data export.

    Two zips, ``WE_1.zip`` and ``WE_2.zip``, with Contact (as CSV) and Task (as
    xlsx) each split between them, and a byte-identical copy of User in both.
    ``nested`` wraps the two in one outer zip at ``dest``; otherwise ``dest`` is
    a directory holding them. ``overlap`` repeats that many Contact rows in both
    parts; ``drop_column`` leaves a Contact column out of the second part.
    """
    work = dest.parent / f"{dest.stem}-split"
    work.mkdir(parents=True, exist_ok=True)
    data = sheets(seed)

    def halves(name):
        headers, rows = data[name]
        mid = len(rows) // 2
        return headers, rows[: mid + (overlap if name == "Contact" else 0)], rows[mid:]

    user = write_xlsx(work / "User.xlsx", "User", *data["User"])
    zip1 = {"User.xlsx": user}
    zip2 = {"User.xlsx": user}
    for name in ("Account", "Custom_Project__c"):
        zip1[f"{name}.xlsx"] = write_xlsx(work / f"{name}.xlsx", name, *data[name])
    zip2["Opportunity.xlsx"] = write_xlsx(
        work / "Opportunity.xlsx", "Opportunity", *data["Opportunity"]
    )

    headers, first, second = halves("Contact")
    (work / "1").mkdir(exist_ok=True)
    (work / "2").mkdir(exist_ok=True)
    zip1["Contact.csv"] = write_csv(work / "1" / "Contact.csv", headers, first)
    if drop_column:
        i = headers.index(drop_column)
        headers = headers[:i] + headers[i + 1 :]
        second = [row[:i] + row[i + 1 :] for row in second]
    zip2["Contact.csv"] = write_csv(work / "2" / "Contact.csv", headers, second)

    headers, first, second = halves("Task")
    zip1["Task.xlsx"] = write_xlsx(work / "1" / "Task.xlsx", "Task", headers, first)
    zip2["Task.xlsx"] = write_xlsx(work / "2" / "Task.xlsx", "Task", headers, second)

    inner = dest.with_suffix("") if not nested else work
    inner.mkdir(parents=True, exist_ok=True)
    zips = []
    for i, members in enumerate((zip1, zip2), 1):
        path = inner / f"WE_{i}.zip"
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as zf:
            for arcname, src in members.items():
                zf.write(src, arcname=arcname)
        zips.append(path)
    if not nested:
        return inner

    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as zf:
        for path in zips:
            zf.write(path, arcname=path.name)
    return dest


if __name__ == "__main__":
    import sys

    out = Path(sys.argv[1] if len(sys.argv) > 1 else "fixture-extract.zip")
    print(build(out))
