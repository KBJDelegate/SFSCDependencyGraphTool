// Exports split across nested zips, and the per-entity Markdown docs.

using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using static DepGraph.Tests.Helpers;

namespace DepGraph.Tests;

public sealed class SplitTests : IDisposable
{
    readonly TempDir tmp = new();

    public void Dispose() => tmp.Dispose();

    [Fact]
    public void ASplitExportReadsExactlyLikeTheUnsplitOne()
    {
        // The strongest check on merging: every statistic of every column comes out
        // identical whether an object was read whole or in two parts.
        var sf = Profiles.Get("salesforce");
        var whole = Infer.Run(Ingest.Run([Fixture.Extract], tmp.Sub("a"), "salesforce", new() { Workers = 1 }), sf);
        var split = Infer.Run(Ingest.Run([Fixture.SplitExtract], tmp.Sub("b"), "salesforce", new() { Workers = 2 }), sf);

        Assert.Equal(whole.Nodes.Select(n => n.Id), split.Nodes.Select(n => n.Id));
        foreach (var (a, b) in whole.Nodes.Zip(split.Nodes))
        {
            Assert.Equal((a.Rows, a.Key, a.KeyToken), (b.Rows, b.Key, b.KeyToken));
            Assert.Equal(a.Columns.Select(c => c.Name), b.Columns.Select(c => c.Name));
            foreach (var (ca, cb) in a.Columns.Zip(b.Columns))
            {
                var where = $"{a.Id}.{ca.Name}";
                Assert.True(ca.Type == cb.Type, where);
                Assert.True(ca.Nulls == cb.Nulls, where);
                Assert.True(ca.Distinct == cb.Distinct, where);
                Assert.True(ca.IdTokens.SequenceEqual(cb.IdTokens), where);
                Assert.True(ca.IsIdLike == cb.IsIdLike, where);
                Assert.True(ca.MaxLength == cb.MaxLength, where);
            }
        }

        static Dictionary<(string, string, string), (double, double, string, string)> Measured(Graph g) =>
            g.Edges.ToDictionary(e => (e.FromNode, e.FromColumn, e.ToNode), e => (e.ResolveRate, e.NullPct, e.Cardinality, e.Kind));
        Assert.Equal(Measured(whole), Measured(split));
        Assert.Equal(whole.LoadOrder, split.LoadOrder);
    }

    [Fact]
    public void SplitObjectsListTheirParts()
    {
        var g = Run([Fixture.SplitExtract], tmp.Dir);
        Assert.Equal(Expected, Edges(g));
        Assert.Equal(["WE_1.zip!Contact.csv", "WE_2.zip!Contact.csv"], Node(g, "Contact")["parts"].Strings());
        Assert.Equal(2000, Node(g, "Contact").Num("rows"));
        Assert.Equal(3000, Node(g, "Task").Num("rows"));
        Assert.Null(Node(g, "Account")["parts"]);
        Assert.Equal(2, g["stats"]!.Num("split_objects"));
        Assert.Equal(7300, g["stats"]!.Num("rows"));
    }

    [Fact]
    public void IdenticalCopiesAreReadOnce()
    {
        // User.xlsx is in both zips byte for byte; reading both would double it.
        var g = Run([Fixture.SplitExtract], tmp.Dir);
        var user = Node(g, "User");
        Assert.Equal(100, user.Num("rows"));
        Assert.Null(user["parts"]);
        Assert.Contains(user["warnings"].Strings(), w => w.Contains("identical copy"));
        Assert.Equal(8, g["stats"]!.Num("files")); // 9 in the zips, less the copy
    }

    [Fact]
    public void ADirectoryOfZipsReadsLikeAZipOfZips()
    {
        var folder = Fixture.BuildSplit(Path.Combine(tmp.Dir, "export"), nested: false);
        Assert.Equal(["WE_1.zip", "WE_2.zip"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        var g = Run([folder], tmp.Dir);
        Assert.Equal(Expected, Edges(g));
        Assert.Equal(2000, Node(g, "Contact").Num("rows"));
    }

    [Fact]
    public void AStrayQuoteFromWindowsQuotingIsForgiven()
    {
        // PowerShell tab-completes "C:\dir\", and Windows hands the program
        // `C:\dir"`: the trailing backslash escapes the closing quote.
        var folder = tmp.Sub("Data prod", "JF Dataudtræk 11-09-2026");
        ZipFile.ExtractToDirectory(Fixture.Extract, folder);
        Assert.Equal(Expected, Edges(Run([folder + "\""], tmp.Dir)));
    }

    [Fact]
    public void OutputNameForSeveralSources()
    {
        Assert.Equal("extract", Cli.DefaultStem(["extract.zip"]));
        Assert.Equal("WE_00D", Cli.DefaultStem(["WE_00D_1.ZIP", "WE_00D_2.ZIP"]));
        Assert.Equal("a", Cli.DefaultStem(["a-1.zip", "a-12.zip"]));
        Assert.Equal("extract", Cli.DefaultStem(["x.zip", "y.zip"]));
    }

    [Fact]
    public void OverlappingPartsKeepTheirKeyAndSaySo()
    {
        // If the parts repeat rows, Id is no longer unique overall but is still the
        // key; losing it would silently drop every relationship into Contact.
        var g = Run([Fixture.BuildSplit(tmp.File("overlap.zip"), overlap: 50)], tmp.Dir);
        var contact = Node(g, "Contact");
        Assert.Equal("Id", contact.Str("key"));
        Assert.Equal(2050, contact.Num("rows"));
        Assert.Contains(contact["warnings"].Strings(), w => w.Contains("overlap") && w.Contains("50"));
        Assert.Equal(Expected, Edges(g));
    }

    [Fact]
    public void AColumnMissingFromOnePartCountsAsEmptyThere()
    {
        var src = Fixture.BuildSplit(tmp.File("ragged.zip"), dropColumn: "Email");
        var contact = Ingest.Run([src], tmp.Sub("stg"), "salesforce", new() { Workers = 1 }).Single(n => n.Id == "Contact");
        var email = contact.Column("Email")!;
        Assert.Equal((2000, 1000, 1000), (email.Rows, email.Nulls, email.Distinct));
        Assert.Contains(contact.Warnings, w => w.Contains("Email") && w.Contains("missing"));
    }

    [Fact]
    public void IncludeListReachesIntoNestedZips()
    {
        var list = tmp.File("w.txt");
        File.WriteAllText(list, "Contact\nAccount\n");
        var g = Run([Fixture.SplitExtract, "--include", list], tmp.Dir);
        Assert.Equal(["Account", "Contact"], NodeIds(g));
        Assert.Equal(2, Node(g, "Contact")["parts"].Strings().Count);
    }

    /// <summary>
    /// An export with files included: three objects (ContentVersion among them)
    /// beside a ContentVersion/ folder of attachments, one of which is a CSV that
    /// would merge into Account if read, and one a zip that would be unpacked.
    /// </summary>
    string WithAttachments(string name)
    {
        var archive = tmp.File($"{name}-attachment.zip");
        using (var inner = ZipFile.Open(archive, ZipArchiveMode.Create))
            EndToEndTests.Write(inner, "Contact.csv", "Id,LastName\n003A00000000001,Ann\n");

        var src = tmp.File($"{name}.zip");
        using var zip = ZipFile.Open(src, ZipArchiveMode.Create);
        EndToEndTests.Write(zip, "User.csv", "Id,Name\n005A00000000001,Ann\n005A00000000002,Bo\n");
        EndToEndTests.Write(zip, "Account.csv", "Id,Name,OwnerId\n001A00000000001,One,005A00000000001\n001A00000000002,Two,005A00000000002\n");
        EndToEndTests.Write(zip, "ContentVersion.csv", "Id,Title,OwnerId\n068A00000000001,Plan,005A00000000001\n");
        EndToEndTests.Write(zip, "ContentVersion/068A00000000001", "%PDF-1.4");
        EndToEndTests.Write(zip, "ContentVersion/Account.csv", "Id,Name\n001A00000000009,Nine\n");
        zip.CreateEntryFromFile(archive, "ContentVersion/068A00000000002.zip");
        return src;
    }

    [Fact]
    public void AttachmentsUnderContentVersionAreNotRead()
    {
        var src = WithAttachments("files");
        var (code, log) = Depgraph(src, "-o", tmp.File("f.json"), "--no-docs");
        Assert.True(code == 0, log);
        var g = Read(tmp.File("f.json"));
        Assert.Equal(["Account", "ContentVersion", "User"], NodeIds(g));
        Assert.Equal(2, Node(g, "Account").Num("rows"));
        Assert.Contains("skipped 3 files under ContentVersion/ (attachments, not data)", log);
        Assert.DoesNotContain("unpacked", log);
    }

    [Fact]
    public void AttachmentsAreSkippedInNestedZipsAndFoldersToo()
    {
        var src = WithAttachments("WE_1");
        var outer = tmp.File("outer.zip");
        using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(src, "WE_1.zip");
        var folder = tmp.Sub("unzipped");
        ZipFile.ExtractToDirectory(src, Path.Combine(folder, "export"));

        foreach (var (input, where) in new[] { (outer, "WE_1.zip!ContentVersion/"), (folder, "export/ContentVersion/") })
        {
            var (code, log) = Depgraph(input, "-o", tmp.File("n.json"), "--no-docs");
            Assert.True(code == 0, log);
            Assert.Equal(["Account", "ContentVersion", "User"], NodeIds(Read(tmp.File("n.json"))));
            Assert.Contains($"skipped 3 files under {where}", log);
        }
    }

    [Fact]
    public void OnlyTheSalesforceProfileSkipsContentVersion()
    {
        Assert.Contains("contentversion", Profiles.Get("salesforce").SkippedFolders);
        Assert.Empty(Profiles.Get("generic").SkippedFolders);
    }

    [Fact]
    public void AnUnreadableFileIsReportedNotSilentlyDropped()
    {
        var src = tmp.File("broken.zip");
        using (var zip = ZipFile.Open(src, ZipArchiveMode.Create))
        {
            EndToEndTests.Write(zip, "User.csv", "Id,Name,Fax\n005A00000000001,Ann,\n005A00000000002,Bo,\n");
            EndToEndTests.Write(zip, "Broken.xlsx", "this is not a workbook");
        }
        var (code, log) = Depgraph(src, "-o", tmp.File("b.json"));
        Assert.Equal(0, code);
        Assert.Contains("could not read Broken.xlsx", log);
        Assert.Equal(["User"], NodeIds(Read(tmp.File("b.json"))));
        var readme = File.ReadAllText(Path.Combine(tmp.Dir, "b-docs", "README.md"));
        Assert.Contains("Could not read `Broken.xlsx`", readme);
        var user = File.ReadAllText(Path.Combine(tmp.Dir, "b-docs", "entities", "User.md"));
        Assert.Contains("## Always empty (1)", user);
        Assert.Contains("`Fax`", user);
    }

    [Fact]
    public void IsoTextDatesAreTypedAsDates()
    {
        Assert.Equal("date", ColumnProfiler.DateKind(["2024-01-02", "2023-12-31"]));
        Assert.Equal("datetime", ColumnProfiler.DateKind(["2024-01-02T10:00:00.000Z", "2024-01-02 10:00"]));
        Assert.Equal("datetime", ColumnProfiler.DateKind(["2024-01-02T10:00:00+0100"]));
        Assert.Null(ColumnProfiler.DateKind(["Call", "2024-01-02"]));
    }

    [Fact]
    public void TheLogSaysWhichFileIsBeingRead()
    {
        // With many zips, a long wait must be attributable to one named file.
        var (code, log) = Depgraph(Fixture.SplitExtract, "-o", tmp.File("log.json"), "-j", "2", "--no-docs");
        Assert.Equal(0, code);
        // Before reading: what is in each zip, and what is split across which.
        Assert.Contains("  WE_1.zip: 5 files,", log);
        Assert.Contains("  WE_2.zip: 4 files,", log);
        Assert.Contains("  Contact.csv in WE_1.zip, WE_2.zip", log);
        Assert.Contains("  WE_2.zip!User.xlsx", log); // the skipped identical copy
        // While reading: one line per finished file, naming the zip it came from.
        foreach (var label in new[] { "WE_1.zip!Contact.csv", "WE_2.zip!Contact.csv", "WE_2.zip!Task.xlsx" })
            Assert.Matches($@"\] {Regex.Escape(label)}: [\d,]+ rows in", log);
        // Nothing announces a file before it is finished.
        Assert.DoesNotContain("reading WE_", log);
        // A part says it is a part, and the merged total follows.
        Assert.Contains("WE_1.zip!Contact.csv: 1,000 rows in", log);
        Assert.Contains("(one of 2 parts of Contact)", log);
        Assert.Contains("  Contact: 2,000 rows from 2 files (1,000 + 1,000)", log);
        Assert.Contains("  Task: 3,000 rows from 2 files (1,500 + 1,500)", log);
    }

    [Fact]
    public void TheLogSaysWhatBecomesOfAnEmptyFile()
    {
        var src = tmp.File("e.zip");
        using (var zip = ZipFile.Open(src, ZipArchiveMode.Create))
        {
            EndToEndTests.Write(zip, "User.csv", "Id,Name\n005A00000000001,Ann\n");
            EndToEndTests.Write(zip, "Case.csv", "Id,CaseNumber\n"); // header only
        }
        Assert.Contains("Case.csv: empty, excluded", Depgraph(src, "-o", tmp.File("a.json"), "--no-docs").Log);
        Assert.Contains("Case.csv: empty, kept", Depgraph(src, "-o", tmp.File("b.json"), "--no-docs", "--include-empty").Log);
    }

    [Fact]
    public void ProgressCountsTheDataReadAndEndsAtAllOfIt()
    {
        // The file count says little when the first few files are most of the data.
        var reports = new List<IngestProgress>();
        Ingest.Run([Fixture.SplitExtract], tmp.Sub("stg"), "salesforce", new() { Workers = 2, Progress = reports.Add });
        var total = Sources.PlanParts(Sources.List([Fixture.SplitExtract], tmp.Sub("list"))).Groups
            .Values.SelectMany(parts => parts).Sum(m => m.Size);

        var finished = reports.Where(r => r.Message is not null).ToList();
        Assert.Equal(Enumerable.Range(1, finished.Count), finished.Select(r => r.Done));
        Assert.All(reports, r => Assert.Equal(total, r.TotalBytes));
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.First.BytesRead <= p.Second.BytesRead));
        Assert.Equal(total, reports[^1].BytesRead);
        Assert.Equal(0, reports[^1].Reading);
    }

    [Fact]
    public void AMemoryBudgetReadsFilesOneAtATimeWithTheSameResult()
    {
        // A budget of one byte fits no two files together, so each is read alone.
        var reports = new List<IngestProgress>();
        var sf = Profiles.Get("salesforce");
        var tight = Infer.Run(Ingest.Run([Fixture.SplitExtract], tmp.Sub("a"), "salesforce",
            new() { Workers = 4, MemoryBudget = 1, Progress = reports.Add }), sf);
        var free = Infer.Run(Ingest.Run([Fixture.SplitExtract], tmp.Sub("b"), "salesforce", new() { Workers = 4 }), sf);

        Assert.All(reports, r => Assert.Equal(0, r.Reading)); // nothing else was being read when one finished
        Assert.Equal(free.Nodes.Select(n => (n.Id, n.Rows)), tight.Nodes.Select(n => (n.Id, n.Rows)));
        Assert.Equal(free.Edges.Select(e => (e.FromNode, e.FromColumn, e.ToNode, e.ResolveRate)),
            tight.Edges.Select(e => (e.FromNode, e.FromColumn, e.ToNode, e.ResolveRate)));
    }

    [Fact]
    public void TheLogGivesTheMemoryBudgetAndTheShareOfDataRead()
    {
        var (code, log) = Depgraph(Fixture.SplitExtract, "-o", tmp.File("a.json"), "--no-docs");
        Assert.Equal(0, code);
        Assert.Matches(@"memory budget: [\d.,]+ [MG]B \(80% of the [\d.,]+ [MG]B free\)", log);
        Assert.Matches(@"\[1/8, \d+% of data\] ", log);
        Assert.Contains("[8/8, 100% of data] ", log);

        (code, log) = Depgraph(Fixture.SplitExtract, "-o", tmp.File("b.json"), "--no-docs", "--memory", "0.5");
        Assert.Equal(0, code);
        Assert.Contains("memory budget: 500.0 MB;", log);
        Assert.Equal(2, Depgraph(Fixture.SplitExtract, "--memory", "0").Code);
    }

    [Fact]
    public void WhileLargeFilesAreReadTheLogSaysHowFarTheyHaveGot()
    {
        var line = Cli.ProgressLine(new IngestProgress(3, 1600, 50_000_000_000, 200_000_000_000, 4, TimeSpan.FromMinutes(30), null));
        Assert.Equal("  ... 25% of data read (50.0 GB of 200.0 GB) in 30m 00s, 4 files being read, about 1h 30m left at this rate", line);
    }
}

public sealed class ProfilerTests
{
    static ColumnStats Column(Action<ColumnProfiler> fill, string profile = "salesforce")
    {
        var p = new ColumnProfiler("c", Profiles.Get(profile), 500);
        fill(p);
        return p.Finish();
    }

    [Fact]
    public void CsvTextIsTypedLikeTheValuesItHolds()
    {
        Assert.Equal("int", Column(p => { p.AddText("1", true); p.AddText("-20", true); }).Type);
        var f = Column(p => { p.AddText("1", true); p.AddText("2.5", true); p.AddText("", true); });
        Assert.Equal(("float", 1L, 2L), (f.Type, f.Nulls, f.Distinct));
        var b = Column(p => { p.AddText("true", true); p.AddText("FALSE", true); p.AddText("True", true); });
        Assert.Equal(("bool", 2L), (b.Type, b.Distinct));
        Assert.Equal("string", Column(p => { p.AddText("1", true); p.AddText("true", true); }).Type);
        Assert.Equal("string", Column(p => { p.AddText("12", true); p.AddText("Call", true); }).Type);
        // Text in a workbook stays text, even when it reads as a number.
        Assert.Equal("string", Column(p => p.AddText("00123", false)).Type);
    }

    [Fact]
    public void WorkbookCellsKeepTheirTypes()
    {
        var day = new DateTime(2024, 1, 31);
        var d = Column(p => { p.AddValue(day); p.AddValue(day.AddHours(5)); p.AddValue(null); });
        Assert.Equal(("datetime", 2L), (d.Type, d.Distinct));
        Assert.Equal("int", Column(p => { p.AddValue(3.0); p.AddValue(4.0); }).Type);
        Assert.Equal("float", Column(p => { p.AddValue(3.0); p.AddValue(4.5); }).Type);
        Assert.Equal("empty", Column(p => p.AddValue(null)).Type);
    }

    [Fact]
    public void IdentifiersAreCountedPastTheSample()
    {
        // The id decision is made on the first 500 values; the token counts must
        // still cover every row after that.
        var c = Column(p =>
        {
            for (var i = 0; i < 1200; i++)
                p.AddText($"{(i < 1000 ? "001" : "003")}A{i:D11}", true);
        });
        Assert.Equal("id", c.Type);
        Assert.Equal([KeyValuePair.Create("001", 1000L), KeyValuePair.Create("003", 200L)], c.IdTokens);
        Assert.True(c.Unique);
    }
}

public sealed class DocsTests : IDisposable
{
    static readonly Lazy<string> docs = new(() =>
    {
        var dir = TempDir.Shared("docs");
        Run([Fixture.Extract], dir);
        return Path.Combine(dir, "g-docs");
    });

    readonly TempDir tmp = new();

    public void Dispose() => tmp.Dispose();

    static string Dir => docs.Value;

    static string Page(params string[] parts) => File.ReadAllText(Path.Combine([Dir, .. parts]));

    static IEnumerable<string> Pages(string dir) => Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories);

    [Fact]
    public void DocsAreWrittenByDefaultBesideTheJson()
    {
        Assert.True(File.Exists(Path.Combine(Dir, "README.md")));
        Assert.True(File.Exists(Path.Combine(Dir, "relationships.md")));
        Assert.Equal(
            ["Account.md", "Contact.md", "Custom_Project__c.md", "Opportunity.md", "Task.md", "User.md"],
            Directory.GetFiles(Path.Combine(Dir, "entities")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(Pages(Dir), page => Assert.StartsWith(Docs.Marker, File.ReadAllText(page)));
    }

    [Fact]
    public void EveryLinkInTheDocsResolves()
    {
        foreach (var page in Pages(Dir))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(page), @"\]\(([^)]+)\)"))
            {
                var target = Path.Combine(Path.GetDirectoryName(page)!, m.Groups[1].Value);
                Assert.True(File.Exists(target), $"{Path.GetFileName(page)} -> {m.Groups[1].Value}");
            }
        }
    }

    [Fact]
    public void IndexCarriesTheStatistics()
    {
        var readme = Page("README.md");
        Assert.Contains("| Entities | 6 |", readme);
        Assert.Contains("| Rows | 7,300 |", readme);
        Assert.Contains("| Relationships | 16 (16 verified) |", readme);
        Assert.Contains("| [Contact](entities/Contact.md) | 2,000 | 8 of 8 | `Id` (003) |", readme);
        Assert.Contains("## Load order", readme);
        // The reliability rule an agent most needs is stated up front.
        Assert.Contains("Resolves** is the authority", readme);
    }

    [Fact]
    public void EntityPageDescribesEveryColumnAndRelationship()
    {
        var page = Page("entities", "Contact.md");
        Assert.Contains("- **Rows:** 2,000", page);
        Assert.Contains("- **Primary key:** `Id`, whose values start with the key prefix `003`", page);
        foreach (var col in new[] { "Id", "AccountId", "Name", "Email", "ReportsToId", "OwnerId", "CreatedById", "CreatedDate" })
            Assert.Contains($"| `{col}` |", page);
        // Out: the planted ~2% dangling accounts show as a partial, still verified, resolve.
        Assert.Matches(@"\| `AccountId` \| \[Account\]\(Account.md\)\.`Id` \| N:1 \| 9\d\.\d% verified", page);
        // In: who points here.
        Assert.Contains("| [Task](Task.md) | `WhoId` |", page);
        Assert.Contains("| [Custom_Project__c](Custom_Project__c.md) | `Primary_Contact__c` |", page);
        // ISO text dates are recognised as dates.
        Assert.Matches(@"\| `CreatedDate` \| datetime \| 100% \| [\d,]+ (\(unique\) )?\|  \|", page);
    }

    [Fact]
    public void PicklistsAreCountedNotListed()
    {
        var row = Page("entities", "Opportunity.md").Split('\n').First(l => l.StartsWith("| `StageName`"));
        Assert.StartsWith("| `StageName` | string | 100% | 3 |", row);
        foreach (var value in new[] { "Prospecting", "Closed Won", "Closed Lost" })
            Assert.DoesNotContain(value, row);
    }

    [Fact]
    public void ReferencesOutsideTheExtractAreNamed()
    {
        var page = Page("entities", "Task.md");
        Assert.Contains("also points at Lead (outside the extract)", page);
        Assert.Contains("| `WhoId` | `00Q` |", page);
    }

    [Fact]
    public void NoDataValueReachesAnyOutput()
    {
        var json = tmp.File("nd.json");
        var (code, log) = Depgraph(Fixture.Extract, "-o", json, "--dot", tmp.File("nd.dot"));
        Assert.Equal(0, code);
        var output = string.Concat(
            new[] { log }.Concat(Directory.EnumerateFiles(tmp.Dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));

        // Every value in the extract, except ones that are also a name in the
        // schema (Task.Subject holds "Email", which is also a column).
        var sheets = Fixture.Sheets();
        var names = sheets.Keys.Concat(sheets.Values.SelectMany(t => t.Headers)).ToHashSet(StringComparer.Ordinal);
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in sheets.Values.SelectMany(t => t.Rows).SelectMany(r => r))
        {
            switch (cell)
            {
                case string s when !names.Contains(s):
                    values.Add(s);
                    break;
                case double d when d != Math.Floor(d):
                    values.Add(d.ToString("R", CultureInfo.InvariantCulture));
                    values.Add(d.ToString("N2", CultureInfo.InvariantCulture));
                    break;
            }
        }
        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.DoesNotContain(v, output, StringComparison.Ordinal));
        // Nor any date from the data, in whatever format it might be written.
        Assert.DoesNotMatch(@"\b20(19|2[0-4])-\d\d-\d\d", output);
    }

    [Fact]
    public void ARerunRemovesStalePagesAndNothingElse()
    {
        var dir = tmp.Sub("g-docs", "entities");
        File.WriteAllText(Path.Combine(dir, "Gone.md"), $"{Docs.Marker} 0.1 -->\n# Gone\n");
        File.WriteAllText(Path.Combine(tmp.Dir, "g-docs", "notes.md"), "# my own notes\n");
        Run([Fixture.Extract], tmp.Dir);
        Assert.False(File.Exists(Path.Combine(dir, "Gone.md")));
        Assert.Equal("# my own notes\n", File.ReadAllText(Path.Combine(tmp.Dir, "g-docs", "notes.md")));
    }

    [Fact]
    public void SplitDocsReportTheMerge()
    {
        Run([Fixture.SplitExtract], tmp.Dir);
        var readme = File.ReadAllText(Path.Combine(tmp.Dir, "g-docs", "README.md"));
        Assert.Contains("| Split entities | 2, merged from 4 files |", readme);
        Assert.Contains("| Identical copies skipped | 1 |", readme);
        Assert.Contains("in 2 zip archives", readme);
        Assert.Contains("| [Contact](entities/Contact.md) | 2,000 |", readme);
        var page = File.ReadAllText(Path.Combine(tmp.Dir, "g-docs", "entities", "Contact.md"));
        Assert.Contains("split across 2 files", page);
        // Each part's own count, and the total every figure on the page is based on.
        Assert.Contains("| `WE_1.zip!Contact.csv` | 1,000 |", page);
        Assert.Contains("| `WE_2.zip!Contact.csv` | 1,000 |", page);
        Assert.Contains("| **Total** | **2,000** |", page);
        Assert.Contains("- **Rows:** 2,000", page);
    }
}

/// <summary>Inputs the synthetic org does not contain, each of which once went wrong.</summary>
public sealed class EdgeCaseTests : IDisposable
{
    readonly TempDir tmp = new();

    public void Dispose() => tmp.Dispose();

    string Zip(string name, params (string Entry, string Text)[] entries)
    {
        var path = tmp.File(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
            EndToEndTests.Write(zip, entry, text);
        return path;
    }

    [Fact]
    public void AWorkbookWhoseHeaderIsNotInA1()
    {
        var dir = tmp.Sub("sheets");
        Fixture.WriteXlsx(Path.Combine(dir, "User.xlsx"), "User", ["Id", "Name"],
            [["005A00000000001", "Ann"], ["005A00000000002", "Bo"]], skipRows: 1, skipColumns: 1);
        Fixture.WriteXlsx(Path.Combine(dir, "Account.xlsx"), "Account", ["Id", "OwnerId"],
            [["001A00000000001", "005A00000000001"]]);
        var g = Run([dir], tmp.Dir);
        var user = Node(g, "User");
        Assert.Equal(["Id", "Name"], user.Each("columns").Select(c => c.Str("name")));
        Assert.Equal(2, user.Num("rows"));
        Assert.Equal(("Account.OwnerId", "User.Id"), Edges(g).Single());
    }

    [Fact]
    public void RatesAreRoundedTheWayTheyRead()
    {
        // 2469/20000 is 0.12345; scaling by 10^4 first made it 0.1234.
        Assert.Equal(0.1235, Fmt.Round(2469 / 20000.0, 4));
        Assert.Equal(0.0001, Fmt.Round(1 / 20000.0, 4));
    }

    [Fact]
    public void CsvNumbersAndFlagsCountByValue()
    {
        var src = Zip("v.zip", ("T.csv", "Id,Flag,N\n1,true,7\n2,TRUE,07\n3,false,8\n"));
        var t = Ingest.Run([src], tmp.Sub("stg"), "generic", new() { Workers = 1 }).Single();
        var flag = t.Column("Flag")!;
        Assert.Equal(("bool", 2L), (flag.Type, flag.Distinct));
        Assert.Equal(("int", 2L), (t.Column("N")!.Type, t.Column("N")!.Distinct));
    }

    [Fact]
    public void SplitColumnsWhoseSafeNamesCollideStayApart()
    {
        // Ejer_æ and Ejer_ø both become Ejer__ in a file name.
        const string users = "Id,Name\n005A00000000001,Ann\n005A00000000002,Bo\n";
        var part1 = Zip("WE_1.zip", ("User.csv", users),
            ("Account.csv", "Id,Ejer_æ,Ejer_ø\n001A00000000001,005A00000000001,\n"));
        var part2 = Zip("WE_2.zip", ("Account.csv", "Id,Ejer_æ,Ejer_ø\n001A00000000002,005A00000000002,005A00000000001\n"));
        var g = Run([part1, part2], tmp.Dir);
        Assert.Contains(("Account.Ejer_æ", "User.Id"), Edges(g));
        Assert.Contains(("Account.Ejer_ø", "User.Id"), Edges(g));
    }

    [Fact]
    public void ADamagedInnerZipIsSkippedWithAWarning()
    {
        var good = Zip("good.zip", ("User.csv", "Id,Name\n005A00000000001,Ann\n"));
        var outer = tmp.File("outer.zip");
        using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(good, "good.zip");
            var bytes = File.ReadAllBytes(good);
            Array.Fill(bytes, (byte)0xFF, 40, 20); // inside the compressed data
            using var w = zip.CreateEntry("bad.zip", CompressionLevel.Optimal).Open();
            w.Write(bytes);
        }
        // Damage the stored copy of bad.zip itself, so inflating it fails.
        var raw = File.ReadAllBytes(outer);
        var at = IndexOf(raw, "bad.zip"u8.ToArray()) + "bad.zip".Length + 8;
        Array.Fill(raw, (byte)0xFF, at, 16);
        File.WriteAllBytes(outer, raw);

        var (code, log) = Depgraph(outer, "-o", tmp.File("o.json"), "--no-docs");
        Assert.Equal(0, code);
        Assert.Contains("could not unpack bad.zip, skipped", log);
        Assert.Equal(["User"], NodeIds(Read(tmp.File("o.json"))));
    }

    static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    [Fact]
    public void ATrailingSlashDoesNotEmptyTheName()
    {
        var dir = tmp.Sub("export");
        File.WriteAllText(Path.Combine(dir, "User.csv"), "Id,Name\n005A00000000001,Ann\n");
        var g = Run([dir + Path.DirectorySeparatorChar], tmp.Dir);
        Assert.EndsWith("export", g.Str("source"));
        Assert.Contains("# Extract: export", File.ReadAllText(Path.Combine(tmp.Dir, "g-docs", "README.md")));
    }

    [Fact]
    public void OddOptionValuesAreHandled()
    {
        // -j 0 means "the default", as it always did.
        Assert.Equal(0, Depgraph(Fixture.Extract, "-o", tmp.File("j.json"), "-j", "0", "-q").Code);
        Assert.Equal(2, Depgraph(Fixture.Extract, "-o", tmp.File("i.json"), "--indent", "500", "-q").Code);
        // -o naming a folder is a one-line error, not a crash.
        var (code, log) = Depgraph(Fixture.Extract, "-o", tmp.Sub("taken"), "--no-docs", "-q");
        Assert.Equal(1, code);
        Assert.StartsWith("depgraph: ", log);
    }
}
