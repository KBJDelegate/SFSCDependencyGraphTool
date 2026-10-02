using System.IO.Compression;
using System.Text.Json.Nodes;
using static DepGraph.Tests.Helpers;

namespace DepGraph.Tests;

public sealed class EndToEndTests : IDisposable
{
    static readonly Lazy<JsonNode> sf = new(() => Run([Fixture.Extract], TempDir.Shared("sf")));

    readonly TempDir tmp = new();

    public void Dispose() => tmp.Dispose();

    static JsonNode Sf => sf.Value;

    [Fact]
    public void FindsExactlyTheRealRelationships() => Assert.Equal(Expected, Edges(Sf));

    [Fact]
    public void EverySheetBecomesANodeWithAKey()
    {
        Assert.Equal(["Account", "Contact", "Opportunity", "User", "Task", "Custom_Project__c"], NodeIds(Sf));
        Assert.All(Sf.Each("nodes"), n => Assert.Equal("Id", n.Str("key")));
        Assert.All(Sf.Each("nodes"), n => Assert.Null(n["warnings"]));
    }

    [Fact]
    public void KeyPrefixesAreDetected()
    {
        var tokens = Sf.Each("nodes").ToDictionary(n => n.Str("id"), n => n["key_token"]?.GetValue<string>());
        Assert.Equal("001", tokens["Account"]);
        Assert.Equal("003", tokens["Contact"]);
        Assert.Equal("005", tokens["User"]);
        Assert.Equal("a01", tokens["Custom_Project__c"]);
    }

    [Fact]
    public void DanglingReferencesLowerTheResolveRate()
    {
        // The fixture plants ~2% ids that look real but match no Account row.
        var edge = Sf.Each("edges").First(e => e.Str("from") == "Contact.AccountId");
        Assert.InRange(edge.Num("resolve_rate"), 0.9, 0.9999);
        Assert.True(edge.Num("null_pct") > 0);
        var clean = Sf.Each("edges").First(e => e.Str("from") == "Opportunity.AccountId");
        Assert.Equal(1.0, clean.Num("resolve_rate"));
    }

    [Fact]
    public void PolymorphicColumnsAreGrouped()
    {
        var what = Sf.Each("edges").Where(e => e.Str("from") == "Task.WhatId").ToList();
        Assert.Equal(["Account.Id", "Opportunity.Id"], what.Select(e => e.Str("to")).ToHashSet());
        Assert.Single(what.Select(e => e.Str("polymorphic_group")).Distinct());
        // WhoId points at Contact and Lead; Lead is absent but it is still polymorphic.
        var who = Sf.Each("edges").First(e => e.Str("from") == "Task.WhoId");
        Assert.Equal("Task.WhoId", who.Str("polymorphic_group"));
    }

    [Fact]
    public void ReferencesOutsideTheExtractAreReported()
    {
        var unresolved = Sf.Each("unresolved").Select(u => (u.Str("from"), u["token"]?.GetValue<string>())).ToHashSet();
        Assert.Contains(("User.ProfileId", "00e"), unresolved);
        Assert.Contains(("Opportunity.Pricebook2Id", "01s"), unresolved);
        Assert.Contains(("Task.WhoId", "00Q"), unresolved);
        var lead = Sf.Each("unresolved").First(u => u["token"]?.GetValue<string>() == "00Q");
        Assert.Contains("Lead", lead.Str("reason"));
    }

    [Fact]
    public void LoadOrderRespectsDependencies()
    {
        var depth = new Dictionary<string, int>();
        foreach (var (layer, i) in Sf.Each("load_order").Select((l, i) => (l, i)))
        {
            foreach (var n in layer.Strings())
                depth[n] = i;
        }
        Assert.True(depth["User"] < depth["Account"] && depth["Account"] < depth["Contact"]);
        Assert.True(depth["Account"] < depth["Opportunity"]);
        Assert.True(depth["Contact"] < depth["Custom_Project__c"]);
        Assert.Null(Sf["cycles"]);
    }

    [Fact]
    public void SelfReferencesAreMarkedButDoNotCreateCycles()
    {
        var selfies = Sf.Each("edges").Where(e => e.Str("kind") == "self").Select(e => e.Str("from")).ToHashSet();
        Assert.Equal(["Account.ParentId", "Contact.ReportsToId", "User.ManagerId"], selfies);
    }

    [Fact]
    public void FullyResolvedEdgesAreMarkedVerified()
    {
        // Every fixture reference points at a real row, so nothing is a guess.
        Assert.All(Sf.Each("edges"), e => Assert.True(e["verified"]!.GetValue<bool>()));
        Assert.All(Sf.Each("edges"), e => Assert.NotEqual("name-only", e.Str("kind")));
    }

    [Fact]
    public void AMissingNameHintDoesNotLookLikeDoubt()
    {
        // The bug this guards: Primary_Contact__c resolves 100% to Contact.Id but
        // shares no name with it. It must not be scored down into "uncertain".
        var e = Sf.Each("edges").First(x => x.Str("from") == "Custom_Project__c.Primary_Contact__c");
        Assert.Equal(1.0, e.Num("resolve_rate"));
        Assert.True(e["verified"]!.GetValue<bool>());
        Assert.True(e.Num("confidence") >= 0.95, "a proven edge must not read as a guess");
        Assert.DoesNotContain(e["evidence"].Strings(), ev => ev.Contains("column name implies"));
    }

    [Fact]
    public void PartiallyResolvedEdgesAreNotMarkedVerified()
    {
        Assert.True(Infer.Score(1.0, true, false, true) >= 0.95);
        Assert.True(Infer.Score(0.4, true, true, true) < Infer.Score(1.0, true, false, true));
        Assert.Equal(0.9, Infer.VerifiedAt);
    }

    [Fact]
    public void ReadingGuideExplainsVerifiedAndConfidence()
    {
        var guide = Sf.Str("reading_guide");
        Assert.Contains("resolve_rate is the authority", guide);
        Assert.Contains("does NOT mean unverified", guide);
        Assert.Contains("verified=true", guide);
    }

    [Fact]
    public void ColumnsCarryAnInlineRefPointer()
    {
        var contact = Node(Sf, "Contact");
        var accountId = contact.Each("columns").First(c => c.Str("name") == "AccountId");
        Assert.Equal("Account.Id", accountId.Str("ref"));
        Assert.Equal("id", accountId.Str("type"));
        // Non-identifier columns are still described, for schema questions.
        var email = contact.Each("columns").First(c => c.Str("name") == "Email");
        Assert.Equal("string", email.Str("type"));
        Assert.Null(email["ref"]);
    }

    [Fact]
    public void JsonStaysSmallAndSelfDescribing()
    {
        Assert.Contains("resolve_rate", Sf.Str("reading_guide"));
        Assert.Equal(7300, Sf["stats"]!.Num("rows"));
        Assert.Equal("salesforce", Sf.Str("profile"));
    }

    [Fact]
    public void GenericProfileWorksWithoutSalesforceKnowledge()
    {
        var g = Run([Fixture.Extract, "--profile", "generic"], tmp.Dir);
        var found = Edges(g);
        Assert.True(found.IsSubsetOf(Expected), "generic must not invent edges the data disproves");
        Assert.Contains(("Contact.AccountId", "Account.Id"), found);
        Assert.Contains(("Account.ParentId", "Account.Id"), found);
        Assert.True(found.Count >= 12);
        // Weaker signals must score lower than the prefix-backed ones.
        Assert.True(g.Each("edges").Max(e => e.Num("confidence")) < 0.99);
    }

    [Fact]
    public void DirectoryInputMatchesZipInput()
    {
        var unzipped = tmp.Sub("sheets");
        ZipFile.ExtractToDirectory(Fixture.Extract, unzipped);
        Assert.Equal(Expected, Edges(Run([unzipped], tmp.Sub("d"))));
    }

    [Fact]
    public void MaxRowsSamplingIsFlagged()
    {
        var g = Run([Fixture.Extract, "--max-rows", "50"], tmp.Dir);
        Assert.True(g["stats"]!["sampled"]!.GetValue<bool>());
        Assert.True(g["stats"]!.Num("rows") < 7300);
        Assert.All(g.Each("nodes"), n => Assert.True(n.Num("rows") <= 50));
    }

    [Fact]
    public void DiagramsRender()
    {
        var nodes = Ingest.Run([Fixture.Extract], tmp.Sub("stg"), "salesforce", new IngestOptions { Workers = 1 });
        var graph = Infer.Run(nodes, Profiles.Get("salesforce"));
        var mmd = Render.ToMermaid(graph);
        Assert.StartsWith("erDiagram", mmd);
        Assert.Contains("Account", mmd);
        Assert.Contains("||--o{", mmd);
        var dot = Render.ToDot(graph);
        Assert.StartsWith("digraph", dot);
        Assert.EndsWith("}", dot.TrimEnd());
        Assert.Equal(graph.Edges.Count, dot.Split("->").Length - 1);
    }

    /// <summary>Copy the fixture and append header-only sheets, like a real org export.</summary>
    static string WithEmptySheets(string dest)
    {
        File.Copy(Fixture.Extract, dest, overwrite: true);
        var work = Path.Combine(Path.GetDirectoryName(dest)!, "empties");
        Directory.CreateDirectory(work);
        using var zip = ZipFile.Open(dest, ZipArchiveMode.Update);
        foreach (var name in new[] { "EmptyObject", "AlsoEmpty__c" })
        {
            var path = Fixture.WriteXlsx(Path.Combine(work, $"{name}.xlsx"), name, ["Id", "Name", "AccountId"], []);
            zip.CreateEntryFromFile(path, $"{name}.xlsx");
        }
        return dest;
    }

    [Fact]
    public void EmptySheetsAreDroppedByDefault()
    {
        // They have no columns, key or references, so as nodes they are noise.
        var g = Run([WithEmptySheets(tmp.File("e.zip"))], tmp.Sub("keep"));
        Assert.DoesNotContain("EmptyObject", NodeIds(g));
        Assert.DoesNotContain("AlsoEmpty__c", NodeIds(g));
        // Nothing at all is built from them: no nodes, no list, no stat.
        Assert.Null(g["empty_sheets"]);
        Assert.Null(g["stats"]!["empty_sheets"]);
        // Dropping them changes no real relationship...
        Assert.Equal(Expected, Edges(g));
        // ...and keeps them out of the load order, whose job is processing order.
        Assert.DoesNotContain("EmptyObject", g.Each("load_order").SelectMany(l => l.Strings()));
    }

    [Fact]
    public void NothingIsBuiltFromEmptySheetsInTheDiagram()
    {
        var (code, _) = Depgraph(WithEmptySheets(tmp.File("e5.zip")), "-o", tmp.File("d.json"), "-q");
        Assert.Equal(0, code);
        var mmd = File.ReadAllText(tmp.File("d.mmd"));
        Assert.DoesNotContain("EmptyObject", mmd);
        Assert.DoesNotContain("AlsoEmpty__c", mmd);
    }

    [Fact]
    public void IncludeEmptyPutsThemBackAsNodes()
    {
        var g = Run([WithEmptySheets(tmp.File("e2.zip")), "--include-empty"], tmp.Dir);
        Assert.Contains("EmptyObject", NodeIds(g));
        Assert.Contains("AlsoEmpty__c", NodeIds(g));
        var empty = Node(g, "EmptyObject");
        Assert.Equal(0, empty.Num("rows"));
        Assert.Equal(["empty sheet"], empty["warnings"].Strings());
    }

    [Fact]
    public void SkipEmptyIsStillAcceptedAsANoOp()
    {
        // It was the flag people typed before this became the default.
        var g = Run([WithEmptySheets(tmp.File("e4.zip")), "--skip-empty"], tmp.Dir);
        Assert.DoesNotContain("EmptyObject", NodeIds(g));
    }

    [Fact]
    public void EmptyCsvSheetsAreDroppedToo()
    {
        // A CSV export signals "empty" with a header-only or zero-byte file.
        var src = tmp.File("csv.zip");
        using (var zip = ZipFile.Open(src, ZipArchiveMode.Create))
        {
            Write(zip, "User.csv", "Id,Name\n005A00000000001,Ann\n005A00000000002,Bo\n");
            Write(zip, "Account.csv", "Id,Name,OwnerId\n001A00000000001,One,005A00000000001\n001A00000000002,Two,005A00000000002\n");
            Write(zip, "Case.csv", "Id,CaseNumber,AccountId\n"); // header only
            Write(zip, "Tombstone.csv", ""); // zero bytes
        }
        var g = Run([src], tmp.Dir, "c");
        Assert.Equal(["Account", "User"], NodeIds(g));
        Assert.Contains(("Account.OwnerId", "User.Id"), Edges(g));
        Assert.DoesNotContain("Case", File.ReadAllText(tmp.File("c.mmd")));
    }

    internal static void Write(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open());
        w.Write(text);
    }

    [Fact]
    public void IncludeListReadsOnlyTheNamedSheets()
    {
        var list = tmp.File("wanted.txt");
        File.WriteAllText(list, "# objects we care about\nAccount\ncontact.xlsx\n\n  User  \n"); // extension and case are ignored
        var g = Run([Fixture.Extract, "--include", list], tmp.Dir);
        Assert.Equal(["Account", "Contact", "User"], NodeIds(g));
        // Opportunity was never read, so nothing references it.
        Assert.DoesNotContain(g.Each("edges"), e => e.Str("to").Contains("Opportunity"));
        Assert.Contains(("Contact.AccountId", "Account.Id"), Edges(g));
        Assert.Equal(3, g["stats"]!.Num("sheets"));
    }

    [Fact]
    public void IncludeListReportsNamesThatMatchedNothing()
    {
        var list = tmp.File("typos.txt");
        File.WriteAllText(list, "Account\nAccuont\nNoSuchObject\n");
        var g = Run([Fixture.Extract, "--include", list], tmp.Dir);
        Assert.Equal(["accuont", "nosuchobject"], g["stats"]!["unmatched_include_names"].Strings());
        Assert.Equal(["Account"], NodeIds(g));
    }

    [Fact]
    public void IncludeListMatchingNothingIsACleanError()
    {
        var list = tmp.File("none.txt");
        File.WriteAllText(list, "Nonexistent\n");
        var (code, log) = Depgraph(Fixture.Extract, "-o", tmp.File("x.json"), "--include", list, "-q");
        Assert.Equal(1, code);
        Assert.Contains("matched a sheet", log);
    }

    [Fact]
    public void MissingIncludeFileIsACleanError()
    {
        var (code, _) = Depgraph(Fixture.Extract, "-o", tmp.File("x.json"), "--include", tmp.File("nope.txt"), "-q");
        Assert.Equal(2, code);
    }

    [Fact]
    public void IncludeListAndEmptyDroppingCombine()
    {
        var list = tmp.File("w.txt");
        File.WriteAllText(list, "Account\nContact\nEmptyObject\n");
        var g = Run([WithEmptySheets(tmp.File("e3.zip")), "--include", list], tmp.Dir);
        Assert.Equal(["Account", "Contact"], NodeIds(g));
    }

    static Edge Ref(string from, string to) => new(from, "c", to, "Id", EdgeKind.Lookup, "N:1", 0.9, 1.0, 0.0);

    static Graph Hand(string[] names, (string, string)[] pairs) => new()
    {
        Nodes = names.Select(n => new Node { Id = n, Source = "x", Rows = 1 }).ToList(),
        Edges = pairs.Select(p => Ref(p.Item1, p.Item2)).ToList(),
    };

    [Fact]
    public void CyclesAreDetectedAndReported()
    {
        var (layers, cycles) = Infer.Topology(Hand(["A", "B", "C"], [("A", "B"), ("B", "A"), ("C", "A")]));
        Assert.Equal([["A", "B"]], cycles);
        // The cycle is condensed into one unit, so C still orders after it rather
        // than being dragged into an unorderable lump.
        Assert.Equal([["A", "B"], ["C"]], layers);
    }

    [Fact]
    public void ACycleDoesNotPoisonTheOrderOfEverythingElse()
    {
        // Regression: User sits in a real Salesforce cycle and almost every object
        // has an OwnerId, so an uncondensed graph collapsed to one useless layer.
        string[] names = ["User", "Account", "Contact", "Opportunity", "Task"];
        var (layers, cycles) = Infer.Topology(Hand(names,
            [("Account", "User"), ("User", "Contact"), ("Contact", "Account"), ("Opportunity", "Account"), ("Task", "Opportunity")]));
        Assert.Equal([["Account", "Contact", "User"]], cycles);
        Assert.Equal([["Account", "Contact", "User"], ["Opportunity"], ["Task"]], layers);
        // Every node is placed exactly once.
        Assert.Equal(names.Order(), layers.SelectMany(l => l).Order());
    }

    [Fact]
    public void MissingSourceIsACleanError()
    {
        Assert.Equal(2, Depgraph(tmp.File("nope.zip"), "-o", tmp.File("o.json")).Code);
    }

    [Fact]
    public void EmptyZipIsACleanError()
    {
        var empty = tmp.File("empty.zip");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create))
            Write(zip, "readme.txt", "nothing tabular here");
        var (code, log) = Depgraph(empty, "-o", tmp.File("o.json"), "-q");
        Assert.Equal(1, code);
        Assert.Contains("no tabular files", log);
    }

    [Fact]
    public void BadOptionsAreAUsageError()
    {
        Assert.Equal(2, Depgraph(Fixture.Extract, "--profile", "nope").Code);
        Assert.Equal(2, Depgraph(Fixture.Extract, "--no-such-flag").Code);
        Assert.Equal(2, Depgraph().Code);
    }
}

[Collection(WorkingDirectory.Name)]
public sealed class OutputNamingTests : IDisposable
{
    readonly TempDir tmp = new();

    public void Dispose() => tmp.Dispose();

    [Fact]
    public void DefaultsNameEveryOutputAfterTheSource()
    {
        // A bare `depgraph extract.zip` writes all three outputs, no flags needed.
        var work = tmp.Sub("work");
        File.Copy(Fixture.Extract, Path.Combine(work, "my-export.zip"));
        WorkingDirectory.In(work, () => Assert.Equal(0, Depgraph("my-export.zip", "-q").Code));
        Assert.True(File.Exists(Path.Combine(work, "my-export.json")));
        Assert.StartsWith("erDiagram", File.ReadAllText(Path.Combine(work, "my-export.mmd")));
        Assert.True(File.Exists(Path.Combine(work, "my-export-docs", "README.md")));
        // DOT is opt-in, not a default output.
        Assert.False(File.Exists(Path.Combine(work, "my-export.dot")));
    }

    [Fact]
    public void DotIsWrittenOnlyWhenAskedFor()
    {
        WorkingDirectory.In(tmp.Dir, () => Assert.Equal(0, Depgraph(Fixture.Extract, "-o", "g.json", "--dot", "g.dot", "-q").Code));
        Assert.StartsWith("digraph", File.ReadAllText(tmp.File("g.dot")));
    }

    [Fact]
    public void JsonOnlySkipsTheDiagrams()
    {
        var work = tmp.Sub("jo");
        File.Copy(Fixture.Extract, Path.Combine(work, "e.zip"));
        WorkingDirectory.In(work, () => Assert.Equal(0, Depgraph("e.zip", "--json-only", "-q").Code));
        Assert.True(File.Exists(Path.Combine(work, "e.json")));
        Assert.False(File.Exists(Path.Combine(work, "e.mmd")));
        Assert.False(File.Exists(Path.Combine(work, "e.dot")));
        Assert.False(Directory.Exists(Path.Combine(work, "e-docs")));
    }

    [Fact]
    public void ExplicitOutPlacesDiagramsBesideIt()
    {
        WorkingDirectory.In(tmp.Dir, () => Assert.Equal(0, Depgraph(Fixture.Extract, "-o", "nested/g.json", "-q").Code));
        Assert.True(File.Exists(Path.Combine(tmp.Dir, "nested", "g.json")));
        Assert.True(File.Exists(Path.Combine(tmp.Dir, "nested", "g.mmd")));
        Assert.False(File.Exists(Path.Combine(tmp.Dir, "nested", "g.dot")));
    }

    [Fact]
    public void DocsLocationAndOptOuts()
    {
        WorkingDirectory.In(tmp.Dir, () =>
        {
            Assert.Equal(0, Depgraph(Fixture.Extract, "-o", "a.json", "--docs", "docs", "-q").Code);
            Assert.Equal(0, Depgraph(Fixture.Extract, "-o", "b.json", "--no-docs", "-q").Code);
            Assert.Equal(0, Depgraph(Fixture.Extract, "-o", "c.json", "--json-only", "-q").Code);
        });
        Assert.True(File.Exists(Path.Combine(tmp.Dir, "docs", "README.md")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Dir, "a-docs")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Dir, "b-docs")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Dir, "c-docs")));
    }

    [Fact]
    public void SeveralZipsCanBePassedAtOnce()
    {
        var folder = Fixture.BuildSplit(Path.Combine(tmp.Dir, "export"), nested: false);
        WorkingDirectory.In(folder, () => Assert.Equal(0, Depgraph("WE_1.zip", "WE_2.zip", "-q").Code));
        // Named for what the parts share, not after the first part.
        var g = Read(Path.Combine(folder, "WE.json"));
        Assert.Equal(Expected, Edges(g));
        Assert.Equal(["WE_1.zip!Contact.csv", "WE_2.zip!Contact.csv"], Node(g, "Contact")["parts"].Strings());
        Assert.True(File.Exists(Path.Combine(folder, "WE-docs", "README.md")));
    }
}
