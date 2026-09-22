"""Generate a synthetic Salesforce-shaped extract zip.

Deliberately includes the awkward cases: a self-reference (Account.ParentId),
polymorphic columns (Task.WhatId/WhoId), references to objects absent from the
extract (User.ProfileId), dangling ids that will not resolve, nullable
lookups, and a custom object with __c lookups.
"""

from __future__ import annotations

import random
import string
import zipfile
from pathlib import Path

B62 = string.ascii_letters + string.digits


def make_ids(prefix: str, n: int, rng: random.Random) -> list[str]:
    seen: set[str] = set()
    while len(seen) < n:
        seen.add(prefix + "".join(rng.choice(B62) for _ in range(12)))
    return sorted(seen)


def build(dest: Path, seed: int = 7) -> Path:
    import xlsxwriter

    rng = random.Random(seed)
    work = dest.parent / f"{dest.stem}-sheets"
    work.mkdir(parents=True, exist_ok=True)

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

    sheets: dict[str, tuple[list[str], list[list]]] = {}

    sheets["User"] = (
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

    sheets["Account"] = (
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

    sheets["Contact"] = (
        ["Id", "AccountId", "Name", "Email", "ReportsToId", "OwnerId", "CreatedById"],
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
            ]
            for i, c in enumerate(contacts)
        ],
    )

    sheets["Opportunity"] = (
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

    sheets["Task"] = (
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

    sheets["Custom_Project__c"] = (
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

    paths = []
    for name, (headers, rows) in sheets.items():
        path = work / f"{name}.xlsx"
        wb = xlsxwriter.Workbook(str(path), {"constant_memory": True})
        ws = wb.add_worksheet(name[:31])
        for c, h in enumerate(headers):
            ws.write(0, c, h)
        for r, row in enumerate(rows, start=1):
            for c, val in enumerate(row):
                if val is not None:
                    ws.write(r, c, val)
        wb.close()
        paths.append(path)

    dest.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as zf:
        for path in paths:
            zf.write(path, arcname=path.name)
    return dest


if __name__ == "__main__":
    import sys

    out = Path(sys.argv[1] if len(sys.argv) > 1 else "fixture-extract.zip")
    print(build(out))
