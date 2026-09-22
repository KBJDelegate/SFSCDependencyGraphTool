# depgraph

Turn a zip of tabular extracts into a dependency graph an AI agent can read in
one shot, instead of opening every file to work out how the tables relate.

Built for Salesforce extracts (a zip of `.xlsx`, one sheet per object), but the
Salesforce knowledge lives in a swappable **profile**, so the same engine works
on any zip or directory of sheets.

```bash
uv run depgraph extract.zip
```

```
6 sheets / 1,175,000 rows / 38 columns in 3.04s
16 relationships, 3 pointing outside the extract
wrote big.json (11,599 bytes)
wrote big.mmd (1,110 bytes)
```

Every output is named after the source, so there is nothing to pass. Use `-o`
to put the JSON elsewhere (the diagrams follow it) or `--json-only` to skip them.

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
| `load_order` | Tables in dependency order, targets first. The last layer holds anything unorderable because of a cycle. |
| `unresolved` | References pointing outside the extract, named where the prefix is a known standard object. |
| `ref` (on a column) | Denormalised pointer, so a column answers "what does this point at" without cross-referencing `edges`. |

The `.mmd` file is a Mermaid ER diagram for humans reviewing what was inferred;
it renders inline on GitHub and in most markdown viewers. Pass `--dot PATH` if
you also want Graphviz DOT, where dashed edges are `name-only` guesses.

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

```bash
cat > wanted.txt <<'EOF'
# core objects only
Account
Contact
Opportunity.xlsx     # the extension is optional
User
EOF

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
   cycles by Tarjan.

The Salesforce profile's leverage is that the first three characters of a record
Id are the object's **key prefix**, so a reference names its own target. That is
why the graph can be built without joining every table against every other one,
and why references to objects *missing* from the extract can still be named.

### Memory and scale

Memory is bounded by `workers × one sheet`, not by the extract, because `.xlsx`
caps a sheet at 1,048,576 rows — a multi-GB extract is always *many* bounded
sheets, never one huge one. Measured on 1.175M rows / 81 MB of xlsx across 6
sheets: **3.0s wall, 452 MB peak per worker**. Scale roughly linearly in total
bytes; use `-j` to trade memory for speed.

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
    --json-only           write only the JSON, no diagrams
    --profile NAME        export dialect (default: salesforce)
-j, --workers N           parallel readers (default: cpus)
    --max-rows N          read at most N rows per sheet (approximate stats)
    --sample N            rows sampled to classify a column (default: 500)
    --min-confidence F    drop inferred edges below this (default: 0.5)
    --overlap-threshold F accept a value-overlap match at or above this (default: 0.8)
    --staging DIR         keep staged parquet here instead of a temp dir
    --indent N            JSON indent; 0 for compact
```

`source` may be the extract `.zip` or an already-unpacked directory. `.xlsx`,
`.xlsm`, `.xlsb`, `.csv` and `.tsv` are read.

## Library use

```python
from depgraph import ingest, infer, get_profile, to_json

if __name__ == "__main__":  # required: workers use the "spawn" start method
    nodes = ingest(Path("extract.zip"), Path("staging"), "salesforce")
    graph = infer(nodes, get_profile("salesforce"))
    print(to_json(graph))
```

Workers are started with `spawn` rather than `fork`: polars starts a Rayon
threadpool on import, and forking a parent holding its locks deadlocks the
children. `spawn` re-imports the caller's main module, so an entry point that
calls `ingest()` at import time needs the `__main__` guard above — without it
the tool logs a warning and reads sequentially instead of failing.

## Development

```bash
uv venv && uv pip install -e ".[dev]"
uv run pytest -q          # 17 tests, ~3s
```

Tests run against a synthetic extract (`tests/make_fixture.py`) built to contain
the awkward cases: self-references, polymorphic columns, references to absent
objects, ~2% deliberately dangling ids, nullable lookups and a custom object
with `__c` lookups. The suite asserts the inferred edge set matches the planted
one exactly — in both profiles.
