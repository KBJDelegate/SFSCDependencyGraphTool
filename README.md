# SFSC Dependency Graph Tool

Turns a Salesforce data extract (a zip of one `.csv` or `.xlsx` per object, or
several such zips) into a dependency graph an AI agent can read, plus a folder
of Markdown docs with one page per object. An agent can then answer questions
without opening every file to work out how the objects relate.

Nothing in an extract declares its relationships: it is just rows of opaque
ids. This works them out from three independent signals (the object key prefix
inside each id, the column name, and an actual join of the values), and records
which of them fired for every relationship it reports.

Measured on a real 865-sheet org export: **~2 GB of CSV reduced to a 500 KB
graph in under a minute**, covering 135 populated objects, 2,941 columns and
457 relationships, every one of them verified against the data.

## Quick start

```bash
uv tool install ./depgraph
depgraph path/to/extract.ZIP
```

That writes:

- `extract.json`: the graph. Hand it to an agent whole; it carries a
  `reading_guide` describing its own fields.
- `extract-docs/`: Markdown docs, starting at `README.md` with statistics and
  every entity, and one page per entity under `entities/` with its rows,
  key, relationships in and out, and every column's fill rate, distinct count,
  range and picklist values. Point an agent at the folder to look things up.
- `extract.mmd`: an ER diagram for humans.

Large exports come as several zips (each is capped at ~512 MB), sometimes inside
one outer zip, with big objects split across them. Pass whatever you have:

```bash
depgraph export.zip                   # zips inside a zip are opened
depgraph WE_00D_1.ZIP WE_00D_2.ZIP    # or the zips themselves
depgraph downloads/                   # or a folder of them
```

Files with the same name in different zips are parts of one object and are
merged into one entity, with exact row and distinct counts.

To cover only the objects a task needs, create a text file listing them, one
per line:

```text
# core.txt
Account
Contact
Opportunity
User
```

```bash
depgraph path/to/extract.ZIP --include core.txt
```

Unlisted files are never opened, so this cuts reading time as well as output
size. Names are matched case-insensitively and any extension is optional, so
`Account`, `account.csv` and `ACCOUNT.CSV` all select the same object. Blank
lines and `#` comments are ignored, and any name that matches nothing is
reported, so a typo cannot silently drop an object.

## Documentation

**[depgraph/README.md](depgraph/README.md)** covers the output format and how to
read `confidence` correctly, how split exports are merged, what the docs
contain, how the three passes work, memory and scale, the options, and how to
add a profile for a non-Salesforce export dialect.

The Salesforce-specific knowledge lives entirely in
[`depgraph/src/depgraph/profiles.py`](depgraph/src/depgraph/profiles.py); the
engine itself is generic, and `--profile generic` runs on any zip of sheets.

## Notes

Extracts, generated graphs and generated docs (`*-docs/`) are gitignored. They
contain real customer data and must not be committed. The docs in particular
quote values (ranges, picklist values); `--no-values` leaves them out.
