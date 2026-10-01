"""Exports split across nested zips, and the per-entity Markdown docs."""

from __future__ import annotations

import json
import re
import zipfile
from pathlib import Path

import pytest
from make_fixture import build_split
from test_end_to_end import EXPECTED

from depgraph import get_profile, infer, ingest
from depgraph.cli import default_stem, main
from depgraph.docs import MARKER
from depgraph.ingest import _date_kind


def run(args: list, tmp_path: Path, name: str = "g") -> dict:
    out = tmp_path / f"{name}.json"
    assert main([*map(str, args), "-o", str(out), "-q"]) == 0
    return json.loads(out.read_text())


def edges(g: dict) -> set[tuple[str, str]]:
    return {(e["from"], e["to"]) for e in g["edges"]}


def node(g: dict, node_id: str) -> dict:
    return next(n for n in g["nodes"] if n["id"] == node_id)


# --- split exports -------------------------------------------------------------


def test_a_split_export_reads_exactly_like_the_unsplit_one(
    extract, split_extract, tmp_path
):
    """The strongest check on merging: every statistic of every column comes out
    identical whether an object was read whole or in two parts."""
    sf = get_profile("salesforce")
    whole = infer(ingest(extract, tmp_path / "a", "salesforce", workers=1), sf)
    split = infer(ingest(split_extract, tmp_path / "b", "salesforce", workers=2), sf)

    assert [n.id for n in split.nodes] == [n.id for n in whole.nodes]
    for a, b in zip(whole.nodes, split.nodes):
        assert (b.rows, b.key, b.key_token) == (a.rows, a.key, a.key_token), a.id
        assert [c.name for c in b.columns] == [c.name for c in a.columns]
        for ca, cb in zip(a.columns, b.columns):
            for attr in (
                "dtype", "nulls", "distinct", "id_tokens", "is_id_like",
                "top_values", "min", "max", "max_length",
            ):
                assert getattr(cb, attr) == getattr(ca, attr), (a.id, ca.name, attr)

    def measured(g):
        return {
            (e.from_node, e.from_column, e.to_node): (
                e.resolve_rate, e.null_pct, e.cardinality, e.kind
            )
            for e in g.edges
        }

    assert measured(split) == measured(whole)
    assert split.load_order == whole.load_order


def test_split_objects_list_their_parts(split_extract, tmp_path):
    g = run([split_extract], tmp_path)
    assert edges(g) == EXPECTED
    assert node(g, "Contact")["parts"] == ["WE_1.zip!Contact.csv", "WE_2.zip!Contact.csv"]
    assert node(g, "Contact")["rows"] == 2000
    assert node(g, "Task")["rows"] == 3000
    assert "parts" not in node(g, "Account")
    assert g["stats"]["split_objects"] == 2
    assert g["stats"]["rows"] == 7300


def test_identical_copies_are_read_once(split_extract, tmp_path):
    """User.xlsx is in both zips byte for byte; reading both would double it."""
    g = run([split_extract], tmp_path)
    user = node(g, "User")
    assert user["rows"] == 100
    assert "parts" not in user
    assert any("identical copy" in w for w in user["warnings"])
    assert g["stats"]["files"] == 8  # 9 in the zips, less the copy


def test_a_directory_of_zips_reads_like_a_zip_of_zips(tmp_path):
    folder = build_split(tmp_path / "export", nested=False)
    assert sorted(p.name for p in folder.iterdir()) == ["WE_1.zip", "WE_2.zip"]
    g = run([folder], tmp_path)
    assert edges(g) == EXPECTED
    assert node(g, "Contact")["rows"] == 2000


def test_several_zips_can_be_passed_at_once(tmp_path, monkeypatch):
    folder = build_split(tmp_path / "export", nested=False)
    monkeypatch.chdir(folder)
    assert main(["WE_1.zip", "WE_2.zip", "-q"]) == 0
    # Named for what the parts share, not after the first part.
    g = json.loads((folder / "WE.json").read_text())
    assert edges(g) == EXPECTED
    assert node(g, "Contact")["parts"] == [
        "WE_1.zip!Contact.csv", "WE_2.zip!Contact.csv"
    ]
    assert (folder / "WE-docs" / "README.md").exists()


def test_a_stray_quote_from_windows_quoting_is_forgiven(extract, tmp_path):
    """PowerShell tab-completes "C:\\dir\\", and Windows hands the program
    `C:\\dir"`: the trailing backslash escapes the closing quote."""
    folder = tmp_path / "Data prod" / "JF Dataudtræk 11-09-2026"
    folder.mkdir(parents=True)
    with zipfile.ZipFile(extract) as zf:
        zf.extractall(folder)
    g = run([f'{folder}"'], tmp_path)
    assert edges(g) == EXPECTED


def test_output_name_for_several_sources():
    assert default_stem([Path("extract.zip")]) == "extract"
    assert default_stem([Path("WE_00D_1.ZIP"), Path("WE_00D_2.ZIP")]) == "WE_00D"
    assert default_stem([Path("a-1.zip"), Path("a-12.zip")]) == "a"
    assert default_stem([Path("x.zip"), Path("y.zip")]) == "extract"


def test_overlapping_parts_keep_their_key_and_say_so(tmp_path):
    """If the parts repeat rows, Id is no longer unique overall but is still the
    key; losing it would silently drop every relationship into Contact."""
    src = build_split(tmp_path / "overlap.zip", overlap=50)
    g = run([src], tmp_path)
    contact = node(g, "Contact")
    assert contact["key"] == "Id"
    assert contact["rows"] == 2050
    assert any("overlap" in w and "50" in w for w in contact["warnings"])
    assert edges(g) == EXPECTED


def test_a_column_missing_from_one_part_counts_as_empty_there(tmp_path):
    src = build_split(tmp_path / "ragged.zip", drop_column="Email")
    nodes = ingest(src, tmp_path / "stg", "salesforce", workers=1)
    contact = next(n for n in nodes if n.id == "Contact")
    email = contact.column("Email")
    assert email.rows == 2000 and email.nulls == 1000 and email.distinct == 1000
    assert any("Email" in w and "missing" in w for w in contact.warnings)


def test_include_list_reaches_into_nested_zips(split_extract, tmp_path):
    lst = tmp_path / "w.txt"
    lst.write_text("Contact\nAccount\n")
    out = tmp_path / "inc.json"
    assert main([str(split_extract), "-o", str(out), "--include", str(lst), "-q"]) == 0
    g = json.loads(out.read_text())
    assert {n["id"] for n in g["nodes"]} == {"Account", "Contact"}
    assert len(node(g, "Contact")["parts"]) == 2


def test_an_unreadable_file_is_reported_not_silently_dropped(tmp_path, capsys):
    src = tmp_path / "broken.zip"
    with zipfile.ZipFile(src, "w") as zf:
        zf.writestr("User.csv", "Id,Name,Fax\n005A00000000001,Ann,\n005A00000000002,Bo,\n")
        zf.writestr("Broken.xlsx", b"this is not a workbook")
    out = tmp_path / "b.json"
    assert main([str(src), "-o", str(out)]) == 0
    assert "could not read Broken.xlsx" in capsys.readouterr().err
    g = json.loads(out.read_text())
    assert {n["id"] for n in g["nodes"]} == {"User"}
    readme = (tmp_path / "b-docs" / "README.md").read_text()
    assert "Could not read `Broken.xlsx`" in readme
    user = (tmp_path / "b-docs" / "entities" / "User.md").read_text()
    assert "## Always empty (1)" in user and "`Fax`" in user


def test_iso_text_dates_are_typed_as_dates():
    assert _date_kind(["2024-01-02", "2023-12-31"]) == "date"
    assert _date_kind(["2024-01-02T10:00:00.000Z", "2024-01-02 10:00"]) == "datetime"
    assert _date_kind(["2024-01-02T10:00:00+0100"]) == "datetime"
    assert _date_kind(["Call", "2024-01-02"]) is None


# --- docs ----------------------------------------------------------------------


@pytest.fixture(scope="module")
def docs(extract, tmp_path_factory) -> Path:
    tmp = tmp_path_factory.mktemp("docs")
    run([extract], tmp)
    return tmp / "g-docs"


def test_docs_are_written_by_default_beside_the_json(docs):
    assert (docs / "README.md").exists()
    assert (docs / "relationships.md").exists()
    assert sorted(p.name for p in (docs / "entities").iterdir()) == [
        "Account.md", "Contact.md", "Custom_Project__c.md", "Opportunity.md",
        "Task.md", "User.md",
    ]
    for page in docs.rglob("*.md"):
        assert page.read_text().startswith(MARKER), page


def test_every_link_in_the_docs_resolves(docs):
    for page in docs.rglob("*.md"):
        for target in re.findall(r"\]\(([^)]+)\)", page.read_text()):
            assert (page.parent / target).exists(), f"{page.name} -> {target}"


def test_index_carries_the_statistics(docs):
    readme = (docs / "README.md").read_text()
    assert "| Entities | 6 |" in readme
    assert "| Rows | 7,300 |" in readme
    assert "| Relationships | 16 (16 verified) |" in readme
    assert "| [Contact](entities/Contact.md) | 2,000 | 8 of 8 | `Id` (003) |" in readme
    assert "## Load order" in readme
    # The reliability rule an agent most needs is stated up front.
    assert "Resolves** is the authority" in readme


def test_entity_page_describes_every_column_and_relationship(docs):
    page = (docs / "entities" / "Contact.md").read_text()
    assert "- **Rows:** 2,000" in page
    assert "- **Primary key:** `Id`, whose values start with the key prefix `003`" in page
    for col in ("Id", "AccountId", "Name", "Email", "ReportsToId", "OwnerId",
                "CreatedById", "CreatedDate"):
        assert f"| `{col}` |" in page, col
    # Out: the planted ~2% dangling accounts show as a partial, still verified, resolve.
    assert re.search(r"\| `AccountId` \| \[Account\]\(Account.md\)\.`Id` \| N:1 \| 9\d\.\d% verified", page)
    # In: who points here.
    assert "| [Task](Task.md) | `WhoId` |" in page
    assert "| [Custom_Project__c](Custom_Project__c.md) | `Primary_Contact__c` |" in page
    # ISO text dates are recognised and given a range.
    assert re.search(r"\| `CreatedDate` \| datetime \| 100% \| [\d,]+ (\(unique\) )?\| 20\d\d-", page)


def test_picklist_values_are_counted(docs):
    page = (docs / "entities" / "Opportunity.md").read_text()
    row = next(line for line in page.splitlines() if line.startswith("| `StageName`"))
    for value in ("Prospecting", "Closed Won", "Closed Lost"):
        assert f"`{value}`" in row
    counts = [int(x.replace(",", "")) for x in re.findall(r"` ([\d,]+)", row)]
    assert sum(counts) == 1500
    assert counts == sorted(counts, reverse=True), "most common first"


def test_references_outside_the_extract_are_named(docs):
    page = (docs / "entities" / "Task.md").read_text()
    assert "also points at Lead (outside the extract)" in page
    assert "| `WhoId` | `00Q` |" in page


def test_no_values_keeps_data_out_of_the_docs(extract, tmp_path):
    out = tmp_path / "nv.json"
    assert main([str(extract), "-o", str(out), "--no-values", "-q"]) == 0
    text = "".join(p.read_text() for p in (tmp_path / "nv-docs").rglob("*.md"))
    for leaked in ("Closed Won", "Customer", "2019-", "2020-"):
        assert leaked not in text, leaked
    # Structure and counts are still there.
    assert "| `StageName` | string | 100% | 3 |" in text


def test_a_rerun_removes_stale_pages_and_nothing_else(extract, tmp_path):
    docs = tmp_path / "g-docs"
    (docs / "entities").mkdir(parents=True)
    (docs / "entities" / "Gone.md").write_text(f"{MARKER} 0.1 -->\n# Gone\n")
    (docs / "notes.md").write_text("# my own notes\n")
    run([extract], tmp_path)
    assert not (docs / "entities" / "Gone.md").exists()
    assert (docs / "notes.md").read_text() == "# my own notes\n"


def test_docs_location_and_opt_outs(extract, tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    assert main([str(extract), "-o", "a.json", "--docs", "docs", "-q"]) == 0
    assert (tmp_path / "docs" / "README.md").exists()
    assert not (tmp_path / "a-docs").exists()

    assert main([str(extract), "-o", "b.json", "--no-docs", "-q"]) == 0
    assert not (tmp_path / "b-docs").exists()

    assert main([str(extract), "-o", "c.json", "--json-only", "-q"]) == 0
    assert not (tmp_path / "c-docs").exists()


def test_split_docs_report_the_merge(split_extract, tmp_path):
    run([split_extract], tmp_path)
    readme = (tmp_path / "g-docs" / "README.md").read_text()
    assert "| Split entities | 2, merged from 4 files |" in readme
    assert "| Identical copies skipped | 1 |" in readme
    assert "in 2 zip archives" in readme
    page = (tmp_path / "g-docs" / "entities" / "Contact.md").read_text()
    assert "split across 2 files" in page
    # Each part's own count, and the total every figure on the page is based on.
    assert "| `WE_1.zip!Contact.csv` | 1,000 |" in page
    assert "| `WE_2.zip!Contact.csv` | 1,000 |" in page
    assert "| **Total** | **2,000** |" in page
    assert "- **Rows:** 2,000" in page
    readme = (tmp_path / "g-docs" / "README.md").read_text()
    assert "| [Contact](entities/Contact.md) | 2,000 |" in readme


def test_the_log_says_which_file_is_being_read(split_extract, tmp_path, capsys):
    """With many zips, a long wait must be attributable to one named file."""
    out = tmp_path / "log.json"
    assert main([str(split_extract), "-o", str(out), "-j", "2", "--no-docs"]) == 0
    err = capsys.readouterr().err
    # Before reading: what is in each zip, and what is split across which.
    assert "  WE_1.zip: 5 files," in err
    assert "  WE_2.zip: 4 files," in err
    assert "  Contact.csv in WE_1.zip, WE_2.zip" in err
    assert "  WE_2.zip!User.xlsx" in err  # the skipped identical copy
    # While reading: one line per finished file, naming the zip it came from.
    for label in ("WE_1.zip!Contact.csv", "WE_2.zip!Contact.csv", "WE_2.zip!Task.xlsx"):
        assert re.search(rf"\] {re.escape(label)}: [\d,]+ rows in", err)
    # Nothing announces a file before it is finished.
    assert "reading WE_" not in err
    # A part says it is a part, and the merged total follows.
    assert "WE_1.zip!Contact.csv: 1,000 rows in" in err
    assert "(one of 2 parts of Contact)" in err
    assert "  Contact: 2,000 rows from 2 files (1,000 + 1,000)" in err
    assert "  Task: 3,000 rows from 2 files (1,500 + 1,500)" in err


def test_the_log_says_what_becomes_of_an_empty_file(tmp_path, capsys):
    src = tmp_path / "e.zip"
    with zipfile.ZipFile(src, "w") as zf:
        zf.writestr("User.csv", "Id,Name\n005A00000000001,Ann\n")
        zf.writestr("Case.csv", "Id,CaseNumber\n")  # header only
    assert main([str(src), "-o", str(tmp_path / "a.json"), "--no-docs"]) == 0
    assert "Case.csv: empty, excluded" in capsys.readouterr().err
    assert main(
        [str(src), "-o", str(tmp_path / "b.json"), "--no-docs", "--include-empty"]
    ) == 0
    assert "Case.csv: empty, kept" in capsys.readouterr().err
