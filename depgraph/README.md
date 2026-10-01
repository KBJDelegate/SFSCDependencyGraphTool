# depgraph

Turn a zip of tabular extracts into a dependency graph an AI agent can read in
one shot, instead of opening every file to work out how the tables relate, plus
a folder of Markdown docs with one page per entity.

Built for Salesforce extracts (a zip of `.csv` or `.xlsx`, one per object, often
split over several zips), but the Salesforce knowledge lives in a swappable
**profile**, so the same engine works on any zip or directory of sheets.

```bash
uv run depgraph extract.zip
```

```
6 sheets / 1,175,000 rows / 38 columns in 3.04s
16 relationships, 3 pointing outside the extract
wrote big.json (11,599 bytes)
wrote big.mmd (1,110 bytes)
wrote big-docs/ (8 pages; start at big-docs/README.md)
```

Every output is named after the source, so there is nothing to pass. Use `-o`
to put the JSON elsewhere (the diagrams and docs follow it), `--no-docs` to skip
the docs, or `--json-only` to write nothing but the JSON.

The output size tracks the **schema**, not the data: that 1.2M-row extract and a
7,300-row one both produce an ~11 KB graph, small enough to paste into a prompt.

## Why not just let the agent read the files

An agent answering "what does `Contact.AccountId` point at?" would otherwise
open every sheet and scan columns. This does that once, in parallel, and answers
in a form that is cheap to re-read — with a measured `resolve_rate` per edge, so
the agent knows which relationships are verified and which are guesses.

## Output

`graph.json` is the canonical artifact. It carries a `reading_guide` so an agent
does not need this README:

```json
{
  "profile": "salesforce",
  "load_order": [["User"], ["Account"], ["Contact", "Opportunity"],
                 ["Custom_Project__c", "Task"]],
  "nodes": [
    {"id": "Contact", "rows": 300000, "key": "Id", "key_token": "003",
     "columns": [
       {"name": "Id", "type": "id", "distinct": 300000, "key": true, "token": "003"},
       {"name": "AccountId", "type": "id", "nulls_pct": 0.0299,
        "distinct": 51735, "token": "001", "ref": "Account.Id"},
       {"name": "Email", "type": "string", "distinct": 300000}
     ]}
  ],
  "edges": [
    {"from": "Contact.AccountId", "to": "Account.Id", "kind": "lookup",
     "cardinality": "N:1", "confidence": 0.986, "resolve_rate": 0.9795,
     "verified": true, "null_pct": 0.0299,
     "evidence": ["key prefix 001 matches Account.Id",
                  "column name implies Account",
                  "285,050/291,017 references resolve (97.9%)"]}
  ],
  "unresolved": [
    {"from": "Task.WhoId", "token": "00Q", "rows": 180304,
     "reason": "key prefix 00Q looks like Lead, which is not in this extract"}
  ]
}
```

Key fields:

| Field | Meaning |
|---|---|
| `resolve_rate` | **The authority on reliability.** Measured share of non-null source values found in the target key. `1.0` = every reference verified against real rows. |
| `verified` | `resolve_rate >= 0.9` — the relationship is proven by data. Report these as established, not as likely. |
| `confidence` | Only ranks *how the target was identified*. Below 1.0 does **not** mean unverified: an edge loses a little merely because the column name lacks the target's name. Judge reliability from `resolve_rate`. |
| `kind` | `lookup`, `self` (self-reference), or `name-only` (names line up, no value matched — a guess). |
| `polymorphic_group` | One column pointing at several tables (`Task.WhatId` → Account *and* Opportunity). |
| `load_order` | Tables in dependency order, targets first; every table appears exactly once. A layer holding several tables that also appear together in `cycles` references itself circularly and must be handled as one unit. |
| `unresolved` | References pointing outside the extract, named where the prefix is a known standard object. |
| `ref` (on a column) | Denormalised pointer, so a column answers "what does this point at" without cross-referencing `edges`. |

The `.mmd` file is a Mermaid ER diagram for humans reviewing what was inferred;
it renders inline on GitHub and in most markdown viewers. Pass `--dot PATH` if
you also want Graphviz DOT, where dashed edges are `name-only` guesses.

## Input: zips of zips, and objects split across them

A Salesforce data export of a large org does not come as one zip. Each zip is
capped at about 512 MB, so the export is several of them (`WE_00D..._1.ZIP`,
`_2.ZIP`, ...), sometimes delivered inside one outer zip, and **a large object
is split across them**: `Task.csv` in `_1.ZIP` holds some rows and `Task.csv` in
`_2.ZIP` holds the rest. All of these shapes are read:

```bash
depgraph export.zip                    # a zip, which may hold more zips
depgraph WE_00D_1.ZIP WE_00D_2.ZIP     # several zips of one export
depgraph downloads/                    # a directory of sheets and/or zips
```

On Windows, a quoted path must not end in a backslash: `"C:\My dir\"` reaches
the program as `C:\My dir"`, because `\"` reads as an escaped quote.
PowerShell's tab-completion adds that backslash. `depgraph` recognises the stray
quote and strips it, but other tools won't.

Before reading, the log says what was found in each zip and which objects are
split across which zips. Then one line is written per file as it finishes, so
no line ever needs updating. A part says it is a part, and the merged totals
follow:

```
found 9 files:
  WE_1.zip: 5 files, 0.3 MB
  WE_2.zip: 4 files, 0.3 MB
2 objects are split across several files; their 4 parts are merged:
  Contact.csv in WE_1.zip, WE_2.zip
  Task.xlsx in WE_1.zip, WE_2.zip
skipping 1 file(s) that are identical copies of another part:
  WE_2.zip!User.xlsx
  [1/8] WE_2.zip!Contact.csv: 1,000 rows in 0.0s (one of 2 parts of Contact)
  [2/8] WE_1.zip!Contact.csv: 1,000 rows in 0.0s (one of 2 parts of Contact)
  [3/8] WE_2.zip!Opportunity.xlsx: 1,500 rows in 0.0s
  ...
merged 2 split objects:
  Contact: 2,000 rows from 2 files (1,000 + 1,000)
  Task: 3,000 rows from 2 files (1,500 + 1,500)
```

An empty file's line says what becomes of it: `empty, excluded`, or `empty part`
when other parts of the same object may still have rows. A split entity's docs
page lists every part with its row count, and every figure on the page covers
all the parts.

Files are read in parallel, one worker process per CPU by default (`-j N` to
change it; each worker holds one file in memory), and inner zips are unpacked
on parallel threads.

Zips inside zips are opened up to four levels deep. Each inner zip is copied to
the staging directory first, since reading a compressed zip in place means
decompressing it again on every seek. That costs disk space equal to the
inner zips' size while the run lasts.

**Files with the same name are parts of one object and are merged into one
entity.** Names match case-insensitively, ignoring folders and extension. The
parts are still read one at a time, so memory stays bounded by one part. Their
statistics are then combined exactly, not estimated:

- Rows, empty counts and key-prefix counts add up.
- Distinct counts come from hashes of the values staged per part, so a value
  that appears in two parts is counted once.
- Identifier columns are merged into one staged column before relationships
  are checked, so `resolve_rate` covers every part.

The test suite checks this by reading the same org whole and split. Every
statistic of every column comes out identical.

A merged entity lists its files in `parts` in the JSON. Things that can go wrong
with parts are reported rather than hidden:

| Situation | What happens |
|---|---|
| The same file appears in two zips, byte for byte (same CRC-32 and size) | Read once. The copy is named in the entity's `warnings`. |
| The parts repeat rows, so the key is no longer unique overall | The key is kept (it is unique within each part), with a warning giving the number of repeated values. |
| A column is missing from some parts | The column is kept. Rows from those parts count as empty, and a warning names the column. |
| A file cannot be read | Reported on the console and in the docs, never dropped silently. |

The JSON's `source` is the name of the first part. With several sources, each
`parts` label is prefixed with the zip it came from, e.g.
`WE_00D_2.ZIP!Task.csv`. Several sources are named after what their names
share: `WE_00D_1.ZIP WE_00D_2.ZIP` writes `WE_00D.json`.

## Docs: one Markdown page per entity

Alongside the JSON, `depgraph` writes a folder of Markdown meant to be opened
one entity at a time, by an agent or a person. The JSON stays lean enough to
paste into a prompt; the docs carry the detail:

```
extract-docs/
  README.md             start here: statistics, every entity, load order, cycles, warnings
  relationships.md      every relationship and every outside reference in one table
  entities/Account.md   one page per entity
```

An entity page gives:

- **Facts:** row count, column count (with data / always empty), primary key and
  its key prefix, source file(s) and size, load-order layer and what to load
  first, and any cycle the entity is part of.
- **Points at:** each reference column, its target, cardinality, resolve rate
  and empty share. Polymorphic columns list every target, including ones
  outside the extract ("also points at Lead (outside the extract)").
- **Referenced by:** every column in other entities that points here.
- **References outside this extract**, with the object each key prefix
  belongs to, where it is known.
- **Columns:** type, filled %, distinct count (marked *unique*), and details:
  number and date ranges, maximum text length, key prefixes of polymorphic
  columns, and, for columns with at most 20 distinct values, every value with
  its row count (picklists, flags, statuses).
- **Always empty:** columns with no value in any row, listed compactly. A full
  org export has many.

Dates exported as ISO text (`2024-01-31T10:00:00.000Z`, as Salesforce CSVs
carry them) are typed `date` or `datetime` and given a range, rather than being
called strings.

**The docs contain data values** (ranges and picklist values), so treat them
like the extract itself. `--no-values` leaves every value out, keeping
structure and counts only. The JSON never contains values.

`--docs DIR` writes the folder somewhere else (`--docs docs` for a plain
`docs/`). Every page starts with a `<!-- generated by depgraph` marker. A rerun
deletes marked pages it no longer writes, so an entity that has left the
extract leaves no stale page, and it never touches any other file in the
folder.

## Narrowing a full org export

### Sheets with no rows are excluded entirely

A full Salesforce export ships *every* object definition, most of which hold no
records at all — on a real export, **730 of 865 sheets**.

**Nothing is built from an empty sheet.** It is not a node, it produces no
edges, it is not an entity in the diagram, and it is not named or counted
anywhere in the JSON. An empty sheet has no columns, no key and no references,
so there is nothing to build from; including one would only pad `load_order`,
whose whole job is to give a processing order.

The consequence to be aware of: **a missing object and an object exported with
zero rows look identical in the output.** If you need to tell them apart, run
with `--include-empty`, which keeps them as nodes carrying
`warnings: ["empty sheet"]`. The run always prints how many were excluded:

```
excluded 730 sheets with no rows
```

(`--skip-empty` is still accepted and does nothing, since this is the default.)

### Audit fields are kept

Salesforce audit lookups — `CreatedById`, `LastModifiedById`, `OwnerId` — are
real references and are included as normal edges. Be aware they dominate: on
that export they are **287 of 457 edges**, all pointing at `User`, which gives
`User` 316 incoming arrows and makes a whole-org diagram hard to read. Use
`--include` for a legible one.

`--include` is also the lever for cutting the populated objects down. Give it a text file listing the sheets you
want, one per line, and everything else is skipped **without being read**:

Create a text file listing the objects, one per line:

```text
# wanted.txt - core objects only
Account
Contact
Opportunity.xlsx     # the extension is optional
User

# FinServ__Alert__c    <- commented out, so not read
```

Then point `--include` at it:

```bash
depgraph extract.ZIP --include wanted.txt
```

Names are matched case-insensitively against the file name, with any extension
and directory prefix ignored, so `Account`, `account.xlsx` and
`exports/ACCOUNT.XLSX` all select the same sheet. Blank lines and `#` comments
are skipped. Names that match nothing are reported and recorded in
`stats.unmatched_include_names`, so a typo does not silently drop an object.

For scale, on that same export: the 10 largest objects come to ~78 KB
(~20k tokens), 20 objects ~154 KB, 40 objects ~237 KB.

## Reading confidence correctly

`resolve_rate` is the reliability signal; `confidence` only ranks how the target
was identified. They are separate on purpose, because blending them misleads:
`Lead.ConvertedAccountId` resolves **100%** to `Account.Id` yet shares no name
with it, so a naming-weighted score made a fully proven edge look like a guess.
Scoring is `0.70*resolve + 0.20*key-prefix + 0.05*name + 0.05*suffix`, so the
measured data dominates. Use `verified` (`resolve_rate >= 0.9`) as the yes/no.

Only `kind: "name-only"` is a genuine guess — there, nothing resolved at all.

## How it works

Three passes, designed so cost scales with the number of *identifier* columns
rather than the size of the data.

1. **Ingest** (parallel, one worker per file). Read each sheet once, profile
   every column, and stage **only the identifier-shaped columns** to parquet as
   `distinct value -> row count`. A 600k-row id column becomes a few hundred KB.
2. **Infer**. Propose targets from three independent signals — the type token
   inside the value, the column name, and dialect built-ins — then *check* each
   proposal with an exact semi-join over the staged columns. Every edge carries
   a measured resolve rate, not a guess.
3. **Render**. JSON, plus optional Mermaid/DOT. Load order by Kahn layering,
   cycles by Tarjan. Cycles are condensed into a single unit before layering,
   because Salesforce has genuine circular references (Account -> User ->
   Contact -> Account) and almost every object carries an `OwnerId`: without
   condensing, one tangle involving `User` leaves nothing placeable and
   `load_order` collapses into a single meaningless layer.

The Salesforce profile's leverage is that the first three characters of a record
Id are the object's **key prefix**, so a reference names its own target. That is
why the graph can be built without joining every table against every other one,
and why references to objects *missing* from the extract can still be named.

### Memory and scale

Memory is bounded by `workers × one file`, not by the extract, because `.xlsx`
caps a sheet at 1,048,576 rows and Salesforce caps each export zip at ~512 MB.
A multi-GB extract is always *many* bounded files, never one huge one, and an
object split across zips is still read one part at a time. Measured on 1.175M
rows / 81 MB of xlsx across 6 sheets: **3.0s wall, 452 MB peak per worker**.
On a 1M-row Task split three ways across nested zips, peak memory fell from 711
MB (read whole) to 437 MB, with identical results. Scale roughly linearly in
total bytes; use `-j` to trade memory for speed.

For a very wide or very long sheet, `--max-rows N` profiles a prefix instead
(stats become approximate; `stats.sampled` records it).

## Profiles

`--profile salesforce` (default) or `--profile generic`.

The generic profile knows nothing about Salesforce: it matches on column names
and, where names give nothing, falls back to measuring value overlap against
every known key. On the test fixture it recovers 13 of the 16 relationships the
Salesforce profile finds, scoring them lower (max 0.7 vs 0.99) because the
key-prefix signal is absent. It misses only the polymorphic columns, whose mixed
values fall below the overlap threshold — correct conservatism for a dialect
with no notion of polymorphism.

Add a dialect by subclassing `Profile` and registering it:

```python
from depgraph.profiles import Profile, register

@register
class MyExportProfile(Profile):
    name = "my-export"

    def id_token(self, value: str) -> str | None:
        """Type token embedded in the value, or None if the dialect has none."""
        return value.split("-")[0] if "-" in value else None

    def key_rank(self, column: str, node: str) -> int:
        """Lower = better primary-key candidate; 99 = not a candidate."""
        return 0 if column == "row_guid" else 99
```

It then appears as `--profile my-export`. The hooks worth overriding are
`id_token`, `looks_like_id`, `key_rank`, `reference_base`, `name_aliases`,
`builtin_targets` and `is_polymorphic`; see `profiles.py`.

## Options

```
    --include-empty       keep sheets with no rows as nodes (off by default)
    --include FILE        text file listing the sheets to read, one per line
-o, --out PATH            JSON graph path (default: <source name>.json)
    --mermaid PATH        Mermaid ER diagram path (default: beside the JSON)
    --dot PATH            also write a Graphviz DOT diagram (off by default)
    --docs DIR            Markdown docs folder (default: <JSON name>-docs)
    --no-docs             do not write the docs folder
    --no-values           keep data values (ranges, picklist values) out of the docs
    --json-only           write only the JSON: no diagrams, no docs
    --profile NAME        export dialect (default: salesforce)
-j, --workers N           parallel readers (default: cpus)
    --max-rows N          read at most N rows per sheet (approximate stats)
    --sample N            rows sampled to classify a column (default: 500)
    --min-confidence F    drop inferred edges below this (default: 0.5)
    --overlap-threshold F accept a value-overlap match at or above this (default: 0.8)
    --staging DIR         staged parquet and unpacked inner zips (default: a temp dir)
    --indent N            JSON indent; 0 for compact
```

`source` is one or more extract `.zip` files (which may hold more zips) or
directories. `.xlsx`, `.xlsm`, `.xlsb`, `.csv` and `.tsv` are read.

## Library use

```python
from depgraph import ingest, infer, get_profile, to_json, write_docs

if __name__ == "__main__":  # required: workers use the "spawn" start method
    nodes = ingest(Path("extract.zip"), Path("staging"), "salesforce")
    graph = infer(nodes, get_profile("salesforce"))
    print(to_json(graph))
    write_docs(graph, Path("extract-docs"))
```

`ingest` also takes a list of paths. To choose files first, call
`list_sources(paths, scratch_dir)`, which returns a `Member` for every tabular
file (nested zips are unpacked into `scratch_dir`). Filter that list and pass
it as `ingest(..., members=...)`.

Workers are started with `spawn` rather than `fork`: polars starts a Rayon
threadpool on import, and forking a parent holding its locks deadlocks the
children. `spawn` re-imports the caller's main module, so an entry point that
calls `ingest()` at import time needs the `__main__` guard above — without it
the tool logs a warning and reads sequentially instead of failing.

## Development

```bash
uv run --extra dev pytest -q   # 60 tests, ~30-100s
```

Tests run against a synthetic extract (`tests/make_fixture.py`) built to contain
the awkward cases: self-references, polymorphic columns, references to absent
objects, ~2% deliberately dangling ids, nullable lookups, ISO text dates and a
custom object with `__c` lookups. The suite asserts the inferred edge set
matches the planted one exactly, in both profiles. `build_split` lays the
same org out as a large data export: two zips inside a zip, with Contact (CSV)
and Task (xlsx) split between them and an identical copy of User in both.
