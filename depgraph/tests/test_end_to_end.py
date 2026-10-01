from __future__ import annotations

import json
import zipfile
from pathlib import Path

import pytest

from depgraph import get_profile, infer, ingest, to_dot, to_mermaid
from depgraph.cli import main
from depgraph.infer import _topology
from depgraph.model import Edge, Graph, Node


def run(source: Path, tmp_path: Path, profile: str = "salesforce") -> dict:
    out = tmp_path / "graph.json"
    assert main([str(source), "-o", str(out), "--profile", profile, "-q"]) == 0
    return json.loads(out.read_text())


@pytest.fixture(scope="session")
def sf(extract, tmp_path_factory) -> dict:
    return run(extract, tmp_path_factory.mktemp("sf"))


EXPECTED = {
    ("Account.OwnerId", "User.Id"),
    ("Account.ParentId", "Account.Id"),
    ("Contact.AccountId", "Account.Id"),
    ("Contact.OwnerId", "User.Id"),
    ("Contact.CreatedById", "User.Id"),
    ("Contact.ReportsToId", "Contact.Id"),
    ("Opportunity.AccountId", "Account.Id"),
    ("Opportunity.OwnerId", "User.Id"),
    ("Task.OwnerId", "User.Id"),
    ("Task.WhatId", "Account.Id"),
    ("Task.WhatId", "Opportunity.Id"),
    ("Task.WhoId", "Contact.Id"),
    ("User.ManagerId", "User.Id"),
    ("Custom_Project__c.Account__c", "Account.Id"),
    ("Custom_Project__c.OwnerId", "User.Id"),
    ("Custom_Project__c.Primary_Contact__c", "Contact.Id"),
}


def test_finds_exactly_the_real_relationships(sf):
    found = {(e["from"], e["to"]) for e in sf["edges"]}
    assert found == EXPECTED


def test_every_sheet_becomes_a_node_with_a_key(sf):
    assert {n["id"] for n in sf["nodes"]} == {
        "Account", "Contact", "Opportunity", "User", "Task", "Custom_Project__c"
    }
    assert all(n["key"] == "Id" for n in sf["nodes"])
    assert not any(n.get("warnings") for n in sf["nodes"])


def test_key_prefixes_are_detected(sf):
    tokens = {n["id"]: n.get("key_token") for n in sf["nodes"]}
    assert tokens["Account"] == "001"
    assert tokens["Contact"] == "003"
    assert tokens["User"] == "005"
    assert tokens["Custom_Project__c"] == "a01"


def test_dangling_references_lower_the_resolve_rate(sf):
    """The fixture plants ~2% ids that look real but match no Account row."""
    edge = next(e for e in sf["edges"] if e["from"] == "Contact.AccountId")
    assert 0.9 < edge["resolve_rate"] < 1.0
    assert edge["null_pct"] > 0
    clean = next(e for e in sf["edges"] if e["from"] == "Opportunity.AccountId")
    assert clean["resolve_rate"] == 1.0


def test_polymorphic_columns_are_grouped(sf):
    what = [e for e in sf["edges"] if e["from"] == "Task.WhatId"]
    assert {e["to"] for e in what} == {"Account.Id", "Opportunity.Id"}
    assert len({e["polymorphic_group"] for e in what}) == 1
    # WhoId points at Contact and Lead; Lead is absent but it is still polymorphic.
    who = next(e for e in sf["edges"] if e["from"] == "Task.WhoId")
    assert who["polymorphic_group"] == "Task.WhoId"


def test_references_outside_the_extract_are_reported(sf):
    unresolved = {(u["from"], u["token"]) for u in sf["unresolved"]}
    assert ("User.ProfileId", "00e") in unresolved
    assert ("Opportunity.Pricebook2Id", "01s") in unresolved
    assert ("Task.WhoId", "00Q") in unresolved
    lead = next(u for u in sf["unresolved"] if u["token"] == "00Q")
    assert "Lead" in lead["reason"]


def test_load_order_respects_dependencies(sf):
    order = sf["load_order"]
    depth = {n: i for i, layer in enumerate(order) for n in layer}
    assert depth["User"] < depth["Account"] < depth["Contact"]
    assert depth["Account"] < depth["Opportunity"]
    assert depth["Contact"] < depth["Custom_Project__c"]
    assert not sf.get("cycles")


def test_self_references_are_marked_but_do_not_create_cycles(sf):
    selfies = {e["from"] for e in sf["edges"] if e["kind"] == "self"}
    assert selfies == {"Account.ParentId", "Contact.ReportsToId", "User.ManagerId"}


def test_fully_resolved_edges_are_marked_verified(sf):
    """Every fixture reference points at a real row, so nothing is a guess."""
    assert all(e["verified"] for e in sf["edges"])
    assert all(e["kind"] != "name-only" for e in sf["edges"])


def test_a_missing_name_hint_does_not_look_like_doubt(sf):
    """The bug this guards: Primary_Contact__c resolves 100% to Contact.Id but
    shares no name with it. It must not be scored down into 'uncertain'."""
    e = next(
        x for x in sf["edges"] if x["from"] == "Custom_Project__c.Primary_Contact__c"
    )
    assert e["resolve_rate"] == 1.0
    assert e["verified"] is True
    assert e["confidence"] >= 0.95, "a proven edge must not read as a guess"
    assert not any("column name implies" in ev for ev in e["evidence"])


def test_partially_resolved_edges_are_not_marked_verified():
    from depgraph.infer import VERIFIED_AT, _score

    assert _score(1.0, True, False, True) >= 0.95
    assert _score(0.4, True, True, True) < _score(1.0, True, False, True)
    assert VERIFIED_AT == 0.9


def test_reading_guide_explains_verified_and_confidence(sf):
    guide = sf["reading_guide"]
    assert "resolve_rate is the authority" in guide
    assert "does NOT mean unverified" in guide
    assert "verified=true" in guide


def test_columns_carry_an_inline_ref_pointer(sf):
    contact = next(n for n in sf["nodes"] if n["id"] == "Contact")
    account_id = next(c for c in contact["columns"] if c["name"] == "AccountId")
    assert account_id["ref"] == "Account.Id"
    assert account_id["type"] == "id"
    # Non-identifier columns are still described, for schema questions.
    email = next(c for c in contact["columns"] if c["name"] == "Email")
    assert email["type"] == "string" and "ref" not in email


def test_json_stays_small_and_self_describing(sf, extract):
    assert "reading_guide" in sf and "resolve_rate" in sf["reading_guide"]
    assert sf["stats"]["rows"] == 7300
    assert sf["profile"] == "salesforce"


def test_generic_profile_works_without_salesforce_knowledge(extract, tmp_path):
    g = run(extract, tmp_path, profile="generic")
    found = {(e["from"], e["to"]) for e in g["edges"]}
    assert found <= EXPECTED, "generic must not invent edges the data disproves"
    assert ("Contact.AccountId", "Account.Id") in found
    assert ("Account.ParentId", "Account.Id") in found
    assert len(found) >= 12
    # Weaker signals must score lower than the prefix-backed ones.
    assert max(e["confidence"] for e in g["edges"]) < 0.99


def test_directory_input_matches_zip_input(extract, tmp_path):
    unzipped = tmp_path / "sheets"
    unzipped.mkdir()
    with zipfile.ZipFile(extract) as zf:
        zf.extractall(unzipped)
    from_dir = run(unzipped, tmp_path / "d")
    assert {(e["from"], e["to"]) for e in from_dir["edges"]} == EXPECTED


def test_max_rows_sampling_is_faster_and_flagged(extract, tmp_path):
    out = tmp_path / "s.json"
    assert main([str(extract), "-o", str(out), "--max-rows", "50", "-q"]) == 0
    g = json.loads(out.read_text())
    assert g["stats"]["sampled"] is True
    assert g["stats"]["rows"] < 7300


def test_defaults_name_every_output_after_the_source(extract, tmp_path, monkeypatch):
    """A bare `depgraph extract.zip` writes all three files, no flags needed."""
    work = tmp_path / "work"
    work.mkdir()
    src = work / "my-export.zip"
    src.write_bytes(extract.read_bytes())
    monkeypatch.chdir(work)

    assert main(["my-export.zip", "-q"]) == 0
    assert (work / "my-export.json").exists()
    assert (work / "my-export.mmd").read_text().startswith("erDiagram")
    # DOT is opt-in, not a default output.
    assert not (work / "my-export.dot").exists()


def test_dot_is_written_only_when_asked_for(extract, tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    assert main([str(extract), "-o", "g.json", "--dot", "g.dot", "-q"]) == 0
    assert (tmp_path / "g.dot").read_text().startswith("digraph")


def test_json_only_skips_the_diagrams(extract, tmp_path, monkeypatch):
    work = tmp_path / "jo"
    work.mkdir()
    (work / "e.zip").write_bytes(extract.read_bytes())
    monkeypatch.chdir(work)

    assert main(["e.zip", "--json-only", "-q"]) == 0
    assert (work / "e.json").exists()
    assert not (work / "e.mmd").exists()
    assert not (work / "e.dot").exists()


def test_explicit_out_places_diagrams_beside_it(extract, tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    assert main([str(extract), "-o", "nested/g.json", "-q"]) == 0
    assert (tmp_path / "nested" / "g.json").exists()
    assert (tmp_path / "nested" / "g.mmd").exists()
    assert not (tmp_path / "nested" / "g.dot").exists()


def test_diagrams_render(extract, tmp_path):
    nodes = ingest(extract, tmp_path / "stg", "salesforce", workers=1)
    graph = infer(nodes, get_profile("salesforce"))
    mmd = to_mermaid(graph)
    assert mmd.startswith("erDiagram")
    assert "Account" in mmd and "||--o{" in mmd
    # Mermaid entity names must not contain characters it cannot parse.
    assert "__c" not in mmd.split("\n")[1] or "Custom_Project__c" in mmd
    dot = to_dot(graph)
    assert dot.startswith("digraph") and dot.rstrip().endswith("}")
    assert dot.count("->") == len(graph.edges)


def _with_empty_sheets(extract: Path, dest: Path) -> Path:
    """Copy the fixture and append header-only sheets, like a real org export."""
    import xlsxwriter

    work = dest.parent / "empties"
    work.mkdir(parents=True, exist_ok=True)
    extras = []
    for name in ("EmptyObject", "AlsoEmpty__c"):
        path = work / f"{name}.xlsx"
        wb = xlsxwriter.Workbook(str(path))
        ws = wb.add_worksheet(name[:31])
        for c, h in enumerate(["Id", "Name", "AccountId"]):
            ws.write(0, c, h)  # headers only, no data rows
        wb.close()
        extras.append(path)

    dest.write_bytes(extract.read_bytes())
    with zipfile.ZipFile(dest, "a", zipfile.ZIP_DEFLATED) as zf:
        for path in extras:
            zf.write(path, arcname=path.name)
    return dest


def test_empty_sheets_are_dropped_by_default(extract, tmp_path):
    """They have no columns, key or references, so as nodes they are noise."""
    src = _with_empty_sheets(extract, tmp_path / "e.zip")
    g = run(src, tmp_path / "keep")
    ids = {n["id"] for n in g["nodes"]}
    assert not ({"EmptyObject", "AlsoEmpty__c"} & ids)
    # Nothing at all is built from them: no nodes, no list, no stat.
    assert "empty_sheets" not in g
    assert "empty_sheets" not in g["stats"]
    assert not any("Empty" in e["from"] or "Empty" in e["to"] for e in g["edges"])
    # Dropping them changes no real relationship...
    assert {(e["from"], e["to"]) for e in g["edges"]} == EXPECTED
    # ...and keeps them out of the load order, whose job is processing order.
    assert "EmptyObject" not in {n for layer in g["load_order"] for n in layer}


def test_nothing_is_built_from_empty_sheets_in_the_diagram(extract, tmp_path):
    src = _with_empty_sheets(extract, tmp_path / "e5.zip")
    out = tmp_path / "d.json"
    assert main([str(src), "-o", str(out), "-q"]) == 0
    mmd = (tmp_path / "d.mmd").read_text()
    assert "EmptyObject" not in mmd
    assert "AlsoEmpty__c" not in mmd


def test_include_empty_puts_them_back_as_nodes(extract, tmp_path):
    src = _with_empty_sheets(extract, tmp_path / "e2.zip")
    out = tmp_path / "se.json"
    assert main([str(src), "-o", str(out), "--include-empty", "-q"]) == 0
    g = json.loads(out.read_text())
    ids = {n["id"] for n in g["nodes"]}
    assert {"EmptyObject", "AlsoEmpty__c"} <= ids
    empty = next(n for n in g["nodes"] if n["id"] == "EmptyObject")
    assert "rows" not in empty or empty["rows"] == 0
    assert empty["warnings"] == ["empty sheet"]


def test_skip_empty_is_still_accepted_as_a_no_op(extract, tmp_path):
    """It was the flag people typed before this became the default."""
    src = _with_empty_sheets(extract, tmp_path / "e4.zip")
    out = tmp_path / "compat.json"
    assert main([str(src), "-o", str(out), "--skip-empty", "-q"]) == 0
    g = json.loads(out.read_text())
    assert not ({"EmptyObject", "AlsoEmpty__c"} & {n["id"] for n in g["nodes"]})


def test_empty_csv_sheets_are_dropped_too(tmp_path):
    """A CSV export signals 'empty' with a header-only or zero-byte file."""
    src = tmp_path / "csv.zip"
    with zipfile.ZipFile(src, "w") as zf:
        zf.writestr("User.csv", "Id,Name\n005A00000000001,Ann\n005A00000000002,Bo\n")
        zf.writestr(
            "Account.csv",
            "Id,Name,OwnerId\n"
            "001A00000000001,One,005A00000000001\n"
            "001A00000000002,Two,005A00000000002\n",
        )
        zf.writestr("Case.csv", "Id,CaseNumber,AccountId\n")  # header only
        zf.writestr("Tombstone.csv", "")  # zero bytes

    out = tmp_path / "c.json"
    assert main([str(src), "-o", str(out), "-q"]) == 0
    g = json.loads(out.read_text())
    assert {n["id"] for n in g["nodes"]} == {"Account", "User"}
    assert ("Account.OwnerId", "User.Id") in {(e["from"], e["to"]) for e in g["edges"]}
    assert "Case" not in (tmp_path / "c.mmd").read_text()


def test_include_list_reads_only_the_named_sheets(extract, tmp_path):
    lst = tmp_path / "wanted.txt"
    lst.write_text(
        "# objects we care about\n"
        "Account\n"
        "contact.xlsx\n"          # extension and case are both ignored
        "\n"
        "  User  \n"
    )
    out = tmp_path / "inc.json"
    assert main([str(extract), "-o", str(out), "--include", str(lst), "-q"]) == 0
    g = json.loads(out.read_text())
    assert {n["id"] for n in g["nodes"]} == {"Account", "Contact", "User"}
    # Opportunity was never read, so nothing references it.
    assert not any("Opportunity" in e["to"] for e in g["edges"])
    assert ("Contact.AccountId", "Account.Id") in {
        (e["from"], e["to"]) for e in g["edges"]
    }
    assert g["stats"]["sheets"] == 3


def test_include_list_reports_names_that_matched_nothing(extract, tmp_path):
    lst = tmp_path / "typos.txt"
    lst.write_text("Account\nAccuont\nNoSuchObject\n")
    out = tmp_path / "u.json"
    assert main([str(extract), "-o", str(out), "--include", str(lst), "-q"]) == 0
    g = json.loads(out.read_text())
    assert g["stats"]["unmatched_include_names"] == ["accuont", "nosuchobject"]
    assert {n["id"] for n in g["nodes"]} == {"Account"}


def test_include_list_matching_nothing_is_a_clean_error(extract, tmp_path):
    lst = tmp_path / "none.txt"
    lst.write_text("Nonexistent\n")
    with pytest.raises(SystemExit):
        main([str(extract), "-o", str(tmp_path / "x.json"), "--include", str(lst), "-q"])


def test_missing_include_file_is_a_clean_error(extract, tmp_path):
    rc = main(
        [str(extract), "-o", str(tmp_path / "x.json"), "--include",
         str(tmp_path / "nope.txt"), "-q"]
    )
    assert rc == 2


def test_include_list_and_empty_dropping_combine(extract, tmp_path):
    src = _with_empty_sheets(extract, tmp_path / "e3.zip")
    lst = tmp_path / "w.txt"
    lst.write_text("Account\nContact\nEmptyObject\n")
    out = tmp_path / "both.json"
    assert main([str(src), "-o", str(out), "--include", str(lst), "-q"]) == 0
    g = json.loads(out.read_text())
    assert {n["id"] for n in g["nodes"]} == {"Account", "Contact"}


def test_cycles_are_detected_and_reported():
    nodes = [Node(id=n, source="x", sheet="x", rows=1) for n in ("A", "B", "C")]
    graph = Graph(nodes=nodes)
    graph.edges = [
        Edge("A", "b_id", "B", "Id", "lookup", "N:1", 0.9, 1.0, 0.0),
        Edge("B", "a_id", "A", "Id", "lookup", "N:1", 0.9, 1.0, 0.0),
        Edge("C", "a_id", "A", "Id", "lookup", "N:1", 0.9, 1.0, 0.0),
    ]
    layers, cycles = _topology(graph)
    assert cycles == [["A", "B"]]
    # The cycle is condensed into one unit, so C still orders after it rather
    # than being dragged into an unorderable lump.
    assert layers == [["A", "B"], ["C"]]


def test_a_cycle_does_not_poison_the_order_of_everything_else():
    """Regression: User sits in a real Salesforce cycle and almost every object
    has an OwnerId, so an uncondensed graph collapsed to one useless layer."""
    names = ["User", "Account", "Contact", "Opportunity", "Task"]
    graph = Graph(nodes=[Node(id=n, source="x", sheet="x", rows=1) for n in names])
    pairs = [
        ("Account", "User"), ("User", "Contact"), ("Contact", "Account"),
        ("Opportunity", "Account"), ("Task", "Opportunity"),
    ]
    graph.edges = [
        Edge(a, "c", b, "Id", "lookup", "N:1", 0.9, 1.0, 0.0) for a, b in pairs
    ]
    layers, cycles = _topology(graph)
    assert cycles == [["Account", "Contact", "User"]]
    assert layers == [["Account", "Contact", "User"], ["Opportunity"], ["Task"]]
    # Every node is placed exactly once.
    assert sorted(n for layer in layers for n in layer) == sorted(names)


def test_missing_source_is_a_clean_error(tmp_path):
    assert main([str(tmp_path / "nope.zip"), "-o", str(tmp_path / "o.json")]) == 2


def test_empty_zip_is_a_clean_error(tmp_path):
    empty = tmp_path / "empty.zip"
    with zipfile.ZipFile(empty, "w") as zf:
        zf.writestr("readme.txt", "nothing tabular here")
    with pytest.raises(SystemExit):
        main([str(empty), "-o", str(tmp_path / "o.json"), "-q"])
