// A synthetic Salesforce-shaped extract.
//
// Deliberately includes the awkward cases: a self-reference (Account.ParentId),
// polymorphic columns (Task.WhatId/WhoId), references to objects absent from the
// extract (User.ProfileId), dangling ids that will not resolve, nullable
// lookups, and a custom object with __c lookups. BuildSplit lays the same org
// out the way a large Salesforce data export does: zips inside a zip, with
// objects split across them.

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace DepGraph.Tests;

using Table = (string[] Headers, List<object?[]> Rows);

public static class Fixture
{
    const string B62 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static readonly Lazy<string> extract = new(() => Build(Path.Combine(TempDir.Shared("fx"), "extract.zip")));
    static readonly Lazy<string> splitExtract = new(() => BuildSplit(Path.Combine(TempDir.Shared("split"), "export.zip")));

    /// <summary>One zip, one xlsx per object; built once per test run.</summary>
    public static string Extract => extract.Value;

    /// <summary>The same org as a zip of two zips, with Contact and Task split between them.</summary>
    public static string SplitExtract => splitExtract.Value;

    static List<string> MakeIds(string prefix, int n, Random rng)
    {
        var seen = new HashSet<string>();
        while (seen.Count < n)
            seen.Add(prefix + new string(Enumerable.Range(0, 12).Select(_ => B62[rng.Next(B62.Length)]).ToArray()));
        return seen.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Object name -> (headers, rows) for the whole synthetic org.</summary>
    public static Dictionary<string, Table> Sheets(int seed = 7)
    {
        var rng = new Random(seed);
        var users = MakeIds("005", 100, rng);
        var accounts = MakeIds("001", 500, rng);
        var contacts = MakeIds("003", 2000, rng);
        var opps = MakeIds("006", 1500, rng);
        var tasks = MakeIds("00T", 3000, rng);
        var projects = MakeIds("a01", 200, rng);
        var leads = MakeIds("00Q", 300, rng);         // referenced, never exported
        var profiles = MakeIds("00e", 8, rng);        // referenced, never exported
        var pricebooks = MakeIds("01s", 3, rng);      // referenced, never exported
        var ghostAccounts = MakeIds("001", 40, rng);  // look real, resolve to nothing

        T Pick<T>(IReadOnlyList<T> pool) => pool[rng.Next(pool.Count)];
        double Uniform(double a, double b) => Math.Round(a + (b - a) * rng.NextDouble(), 2);
        // A real Salesforce CSV export carries dates as ISO text.
        var epoch = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string Stamp() => epoch.AddSeconds(rng.Next(5 * 365 * 86400)).ToString("yyyy-MM-ddTHH:mm:ss.000Z", Inv);

        var output = new Dictionary<string, Table>();
        output["User"] = (
            ["Id", "Name", "Email", "ManagerId", "ProfileId", "IsActive"],
            users.Select((u, i) => new object?[]
            {
                u, $"User {i}", $"user{i}@example.com", i > 5 ? Pick(users) : null, Pick(profiles), rng.NextDouble() > 0.1,
            }).ToList());

        output["Account"] = (
            ["Id", "Name", "Type", "OwnerId", "ParentId", "AnnualRevenue"],
            accounts.Select((a, i) => new object?[]
            {
                a, $"Account {i}", Pick(["Customer", "Partner", "Prospect"]), Pick(users),
                rng.NextDouble() < 0.3 ? Pick(accounts) : null, Uniform(1e4, 1e8),
            }).ToList());

        output["Contact"] = (
            ["Id", "AccountId", "Name", "Email", "ReportsToId", "OwnerId", "CreatedById", "CreatedDate"],
            contacts.Select((c, i) => new object?[]
            {
                c,
                // ~2% dangling, ~3% null: exercises resolve_rate and null_pct
                rng.NextDouble() < 0.03 ? null : rng.NextDouble() < 0.02 ? Pick(ghostAccounts) : Pick(accounts),
                $"Contact {i}", $"contact{i}@example.com", rng.NextDouble() < 0.25 ? Pick(contacts) : null,
                Pick(users), Pick(users), Stamp(),
            }).ToList());

        output["Opportunity"] = (
            ["Id", "Name", "AccountId", "OwnerId", "StageName", "Amount", "Pricebook2Id"],
            opps.Select((o, i) => new object?[]
            {
                o, $"Opp {i}", Pick(accounts), Pick(users), Pick(["Prospecting", "Closed Won", "Closed Lost"]),
                Uniform(500, 500000), Pick(pricebooks),
            }).ToList());

        output["Task"] = (
            ["Id", "Subject", "WhatId", "WhoId", "OwnerId", "Status"],
            tasks.Select(t => new object?[]
            {
                t, Pick(["Call", "Email", "Meeting"]), rng.NextDouble() < 0.6 ? Pick(accounts) : Pick(opps),
                rng.NextDouble() < 0.7 ? Pick(contacts) : Pick(leads), Pick(users), Pick(["Open", "Completed"]),
            }).ToList());

        output["Custom_Project__c"] = (
            ["Id", "Name", "Account__c", "Primary_Contact__c", "OwnerId", "Budget__c"],
            projects.Select((p, i) => new object?[]
            {
                p, $"Project {i}", Pick(accounts), rng.NextDouble() < 0.8 ? Pick(contacts) : null, Pick(users),
                Uniform(1000, 900000),
            }).ToList());

        return output;
    }

    /// <summary>
    /// A minimal single-sheet workbook: inline strings, numbers and booleans, no styles.
    /// The header goes in A1 unless <paramref name="skipRows"/> or <paramref name="skipColumns"/> move it.
    /// </summary>
    public static string WriteXlsx(string path, string sheet, string[] headers, IEnumerable<object?[]> rows,
        int skipRows = 0, int skipColumns = 0)
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

        static string Col(int i) => i < 26 ? ((char)('A' + i)).ToString() : Col(i / 26 - 1) + (char)('A' + i % 26);
        static string Cell(string r, object value) => value switch
        {
            bool b => $"<c r=\"{r}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>",
            double or int or long => $"<c r=\"{r}\"><v>{Convert.ToString(value, Inv)}</v></c>",
            _ => $"<c r=\"{r}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(value.ToString())}</t></is></c>",
        };

        var data = new StringBuilder();
        var all = new[] { headers.Cast<object?>().ToArray() }.Concat(rows);
        var rowNo = skipRows;
        foreach (var row in all)
        {
            rowNo++;
            data.Append($"<row r=\"{rowNo}\">");
            for (var c = 0; c < row.Length; c++)
            {
                if (row[c] is { } v)
                    data.Append(Cell($"{Col(c + skipColumns)}{rowNo}", v));
            }
            data.Append("</row>");
        }

        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" + xml);
        }
        Part("[Content_Types].xml",
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
            + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
            + "</Types>");
        Part("_rels/.rels",
            $"<Relationships xmlns=\"{pkg}\"><Relationship Id=\"rId1\" Type=\"{rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Part("xl/workbook.xml",
            $"<workbook xmlns=\"{ns}\" xmlns:r=\"{rel}\"><sheets><sheet name=\"{SecurityElement.Escape(sheet[..Math.Min(sheet.Length, 31)])}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels",
            $"<Relationships xmlns=\"{pkg}\"><Relationship Id=\"rId1\" Type=\"{rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
        Part("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><sheetData>{data}</sheetData></worksheet>");
        return path;
    }

    public static string WriteCsv(string path, string[] headers, IEnumerable<object?[]> rows)
    {
        static string Field(object? v) => v switch
        {
            null => "",
            bool b => b ? "True" : "False",
            _ => Convert.ToString(v, Inv) is var s && s!.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s,
        };
        var lines = new[] { headers.Cast<object?>().ToArray() }.Concat(rows).Select(r => string.Join(",", r.Select(Field)));
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        return path;
    }

    static void Zip(string dest, IEnumerable<(string Name, string Source)> members)
    {
        File.Delete(dest);
        using var zip = ZipFile.Open(dest, ZipArchiveMode.Create);
        foreach (var (name, source) in members)
            zip.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
    }

    public static string Build(string dest, int seed = 7)
    {
        var work = Path.Combine(Path.GetDirectoryName(dest)!, Path.GetFileNameWithoutExtension(dest) + "-sheets");
        Directory.CreateDirectory(work);
        var paths = Sheets(seed)
            .Select(kv => WriteXlsx(Path.Combine(work, $"{kv.Key}.xlsx"), kv.Key, kv.Value.Headers, kv.Value.Rows))
            .ToList();
        Zip(dest, paths.Select(p => (Path.GetFileName(p), p)));
        return dest;
    }

    /// <summary>
    /// The same org as <see cref="Build"/>, shaped like a large Salesforce data export:
    /// two zips, WE_1.zip and WE_2.zip, with Contact (as CSV) and Task (as xlsx) each
    /// split between them, and a byte-identical copy of User in both. <paramref name="nested"/>
    /// wraps the two in one outer zip at <paramref name="dest"/>; otherwise the result is a
    /// directory holding them. <paramref name="overlap"/> repeats that many Contact rows in
    /// both parts; <paramref name="dropColumn"/> leaves a Contact column out of the second part.
    /// </summary>
    public static string BuildSplit(string dest, int seed = 7, int overlap = 0, string? dropColumn = null, bool nested = true)
    {
        var parent = Path.GetDirectoryName(dest)!;
        var stem = Path.GetFileNameWithoutExtension(dest);
        var work = Path.Combine(parent, $"{stem}-split");
        Directory.CreateDirectory(Path.Combine(work, "1"));
        Directory.CreateDirectory(Path.Combine(work, "2"));
        var data = Sheets(seed);

        (string[], List<object?[]>, List<object?[]>) Halves(string name)
        {
            var (headers, rows) = data[name];
            var mid = rows.Count / 2;
            return (headers, rows[..(mid + (name == "Contact" ? overlap : 0))], rows[mid..]);
        }

        var user = WriteXlsx(Path.Combine(work, "User.xlsx"), "User", data["User"].Headers, data["User"].Rows);
        var zip1 = new List<(string, string)> { ("User.xlsx", user) };
        var zip2 = new List<(string, string)> { ("User.xlsx", user) };
        foreach (var name in new[] { "Account", "Custom_Project__c" })
            zip1.Add(($"{name}.xlsx", WriteXlsx(Path.Combine(work, $"{name}.xlsx"), name, data[name].Headers, data[name].Rows)));
        zip2.Add(("Opportunity.xlsx",
            WriteXlsx(Path.Combine(work, "Opportunity.xlsx"), "Opportunity", data["Opportunity"].Headers, data["Opportunity"].Rows)));

        var (headers, first, second) = Halves("Contact");
        zip1.Add(("Contact.csv", WriteCsv(Path.Combine(work, "1", "Contact.csv"), headers, first)));
        if (dropColumn is not null)
        {
            var i = Array.IndexOf(headers, dropColumn);
            headers = [.. headers[..i], .. headers[(i + 1)..]];
            second = second.Select(r => (object?[])[.. r[..i], .. r[(i + 1)..]]).ToList();
        }
        zip2.Add(("Contact.csv", WriteCsv(Path.Combine(work, "2", "Contact.csv"), headers, second)));

        (headers, first, second) = Halves("Task");
        zip1.Add(("Task.xlsx", WriteXlsx(Path.Combine(work, "1", "Task.xlsx"), "Task", headers, first)));
        zip2.Add(("Task.xlsx", WriteXlsx(Path.Combine(work, "2", "Task.xlsx"), "Task", headers, second)));

        var inner = nested ? work : Path.Combine(parent, stem);
        Directory.CreateDirectory(inner);
        var zips = new List<string>();
        foreach (var (members, i) in new[] { zip1, zip2 }.Select((m, i) => (m, i + 1)))
        {
            var path = Path.Combine(inner, $"WE_{i}.zip");
            Zip(path, members);
            zips.Add(path);
        }
        if (!nested)
            return inner;
        Zip(dest, zips.Select(p => (Path.GetFileName(p), p)));
        return dest;
    }
}

/// <summary>A fresh directory under the system temp folder, removed when disposed.</summary>
public sealed class TempDir : IDisposable
{
    static readonly List<TempDir> shared = [];

    static TempDir() => AppDomain.CurrentDomain.ProcessExit += (_, _) => shared.ForEach(d => d.Dispose());

    public string Dir { get; } = Directory.CreateTempSubdirectory("depgraph-test-").FullName;

    /// <summary>A directory kept for the whole run, for fixtures built once.</summary>
    public static string Shared(string name)
    {
        var dir = new TempDir();
        lock (shared)
            shared.Add(dir);
        return dir.Sub(name);
    }

    public string Sub(params string[] parts)
    {
        var dir = Path.Combine([Dir, .. parts]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string File(string name) => Path.Combine(Dir, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
