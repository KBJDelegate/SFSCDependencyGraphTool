# SFSC Dependency Graph Tool

Turns a Salesforce data extract — a zip of one `.csv` or `.xlsx` per object —
into a dependency graph an AI agent can read, instead of opening every file to
work out how the objects relate.

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

That writes `extract.json` (the graph) and `extract.mmd` (an ER diagram). Hand
the JSON to an agent, or query it — it carries a `reading_guide` describing its
own fields, so no other documentation is needed to interpret it.

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
read `confidence` correctly, how the three passes work, memory and scale, the
options, and how to add a profile for a non-Salesforce export dialect.

The Salesforce-specific knowledge lives entirely in
[`depgraph/src/depgraph/profiles.py`](depgraph/src/depgraph/profiles.py); the
engine itself is generic, and `--profile generic` runs on any zip of sheets.

## Notes

Extracts and generated graphs are gitignored — they contain real customer data
and must not be committed.
