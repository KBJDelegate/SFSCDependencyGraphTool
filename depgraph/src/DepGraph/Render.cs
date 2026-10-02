// Pass 3: emit the graph.
//
// The JSON is the canonical artifact and is written for an agent to read in one
// shot: it carries a short reading guide, every column is annotated inline with
// the reference it participates in, and empty fields are dropped so the file
// stays small enough to paste into context.

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DepGraph;

public static partial class Render
{
    public const string SchemaVersion = "1.1";

    public const string ReadingGuide =
        "nodes[] are tables (one per sheet); edges[] are references between them. "
        + "READ THIS BEFORE JUDGING RELIABILITY: resolve_rate is the authority. It is "
        + "the measured share of non-null source values actually found in the target "
        + "key, so 1.0 means every single reference was checked against real rows. "
        + "verified=true means resolve_rate >= 0.9, i.e. the relationship is proven by "
        + "data; report it as established, not as likely. A confidence below 1.0 does "
        + "NOT mean unverified: confidence only ranks how the target was identified, "
        + "and an edge loses a little of it merely because the column name does not "
        + "contain the target's name (Lead.ConvertedAccountId resolves 100% to "
        + "Account.Id but shares no name with it). Only kind='name-only' is a guess - "
        + "there, nothing resolved. Where resolve_rate is between 0 and 0.9 the "
        + "relationship is real but some rows point at records missing from the "
        + "extract. polymorphic_group marks one column pointing at several tables. "
        + "load_order lists tables in dependency order, targets first, and every table "
        + "appears exactly once. A layer holding several tables that are also listed "
        + "together in cycles[] means they reference each other circularly and have "
        + "to be handled as one unit. unresolved[] are references "
        + "pointing outside this extract, which are not errors. Sheets the export "
        + "shipped with no rows are excluded entirely. A node with parts[] was split "
        + "across several files of the export and has been merged into one table.";

    /// <summary>Fields kept even when zero or false, because their absence would read as unknown.</summary>
    static readonly HashSet<string> AlwaysKept = ["rows", "confidence", "verified"];

    public static JsonObject ToJsonObject(Graph graph)
    {
        var refs = new Dictionary<(string, string), List<string>>();
        foreach (var e in graph.Edges)
        {
            if (!refs.TryGetValue((e.FromNode, e.FromColumn), out var list))
                refs[(e.FromNode, e.FromColumn)] = list = [];
            list.Add($"{e.ToNode}.{e.ToColumn}");
        }

        var nodes = new JsonArray();
        foreach (var n in graph.Nodes)
        {
            var cols = new JsonArray();
            foreach (var c in n.Columns)
            {
                var col = new JsonObject
                {
                    ["name"] = c.Name,
                    ["type"] = c.Type,
                    ["distinct"] = c.Distinct,
                    ["nulls_pct"] = c.Rows > 0 ? Fmt.Round(c.Nulls / (double)c.Rows, 4) : 0.0,
                };
                if (c.Name == n.Key)
                    col["key"] = true;
                if (c.IdTokens.Count == 1)
                    col["token"] = c.IdTokens[0].Key;
                else if (c.IdTokens.Count > 1)
                    col["tokens"] = Counts(c.IdTokens);
                if (refs.TryGetValue((n.Id, c.Name), out var target))
                    col["ref"] = target.Count > 1 ? Strings(target) : target[0];
                cols.Add(col);
            }

            nodes.Add(new JsonObject
            {
                ["id"] = n.Id,
                ["source"] = n.Source,
                // Only when the export split the object across several files.
                ["parts"] = n.Parts.Count > 1 ? Strings(n.Parts) : null,
                ["sheet"] = n.Sheet != n.Id ? n.Sheet : null,
                ["rows"] = n.Rows,
                ["key"] = n.Key,
                ["key_token"] = n.KeyToken,
                ["columns"] = cols,
                ["warnings"] = Strings(n.Warnings),
            });
        }

        var edges = new JsonArray();
        foreach (var e in graph.Edges)
        {
            edges.Add(new JsonObject
            {
                ["from"] = $"{e.FromNode}.{e.FromColumn}",
                ["to"] = $"{e.ToNode}.{e.ToColumn}",
                ["kind"] = e.Kind,
                ["cardinality"] = e.Cardinality,
                ["confidence"] = e.Confidence,
                ["resolve_rate"] = e.ResolveRate,
                ["verified"] = e.Verified,
                ["null_pct"] = e.NullPct,
                ["polymorphic_group"] = e.PolymorphicGroup,
                ["evidence"] = Strings(e.Evidence),
            });
        }

        var unresolved = new JsonArray();
        foreach (var u in graph.Unresolved)
        {
            unresolved.Add(new JsonObject
            {
                ["from"] = $"{u.Node}.{u.Column}",
                ["token"] = u.Token,
                ["rows"] = u.Rows,
                ["reason"] = u.Reason,
            });
        }

        var root = new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["generator"] = $"depgraph {ToolVersion.Current}",
            ["profile"] = graph.Profile,
            ["source"] = graph.Source,
            ["stats"] = graph.Stats is { } s
                ? new JsonObject
                {
                    ["sheets"] = s.Sheets,
                    ["rows"] = s.Rows,
                    ["columns"] = s.Columns,
                    ["edges"] = s.Edges,
                    ["unresolved"] = s.Unresolved,
                    ["files"] = s.Files,
                    ["split_objects"] = s.SplitObjects,
                    ["read_seconds"] = s.ReadSeconds,
                    ["total_seconds"] = s.TotalSeconds,
                    ["sampled"] = s.Sampled,
                    ["include_list"] = s.IncludeList,
                    ["unmatched_include_names"] = Strings(s.UnmatchedIncludeNames),
                }
                : null,
            ["reading_guide"] = ReadingGuide,
            ["load_order"] = Lists(graph.LoadOrder),
            ["cycles"] = Lists(graph.Cycles),
            ["nodes"] = nodes,
            ["edges"] = edges,
            ["unresolved"] = unresolved,
        };
        Clean(root);
        return root;
    }

    public static string ToJson(Graph graph, int indent = 1)
    {
        var options = new JsonSerializerOptions
        {
            // Names in the extract may be Danish, German, ...; keep them readable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = indent > 0,
            IndentSize = Math.Max(indent, 1),
        };
        return ToJsonObject(graph).ToJsonString(options);
    }

    /// <summary>
    /// Drop empty, zero and false fields so the JSON stays token-lean. Whether a
    /// field is empty is judged before its own contents are cleaned, and list items
    /// are never dropped, only cleaned.
    /// </summary>
    static void Clean(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (IsBlank(obj[key]) && !AlwaysKept.Contains(key))
                        obj.Remove(key);
                    else
                        Clean(obj[key]);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    Clean(item);
                break;
        }
    }

    static bool IsBlank(JsonNode? node) => node switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        JsonValue v when v.TryGetValue(out string? s) => s.Length == 0,
        JsonValue v when v.TryGetValue(out bool b) => !b,
        JsonValue v when v.TryGetValue(out double d) => d == 0,
        JsonValue v when v.TryGetValue(out long l) => l == 0,
        JsonValue v when v.TryGetValue(out int i) => i == 0,
        _ => false,
    };

    static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)s).ToArray());

    static JsonArray Lists(IEnumerable<IEnumerable<string>> lists) => new(lists.Select(l => (JsonNode?)Strings(l)).ToArray());

    static JsonObject Counts(IEnumerable<KeyValuePair<string, long>> counts)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in counts)
            obj[k] = v;
        return obj;
    }

    /// <summary>Mermaid/DOT entity names allow a narrower character set than sheet names.</summary>
    static string Ident(string name)
    {
        var output = NotIdent().Replace(name, "_");
        return output.Length > 0 && !char.IsAsciiDigit(output[0]) ? output : $"n_{output}";
    }

    public static string ToMermaid(Graph graph, double minConfidence = 0.5)
    {
        var lines = new List<string> { "erDiagram" };
        var interesting = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> Of(string id) =>
            interesting.TryGetValue(id, out var set) ? set : interesting[id] = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (n.Key is not null)
                Of(n.Id).Add(n.Key);
        }
        foreach (var e in graph.Edges)
            Of(e.FromNode).Add(e.FromColumn);

        foreach (var n in graph.Nodes)
        {
            if (n.Columns.Count == 0)
                continue;
            lines.Add($"  {Ident(n.Id)} {{");
            foreach (var c in n.Columns)
            {
                if (!Of(n.Id).Contains(c.Name))
                    continue;
                var marker = c.Name == n.Key ? "PK" : "FK";
                lines.Add($"    {c.Type} {Ident(c.Name)} {marker}");
            }
            lines.Add("  }");
        }

        foreach (var e in graph.Edges)
        {
            if (e.Confidence < minConfidence)
                continue;
            // optional on the source side when the reference is nullable
            var left = e.NullPct < 0.001 ? "||" : "|o";
            var right = e.Cardinality == "1:1" ? "||" : "o{";
            var label = e.FromColumn + (e.Kind == EdgeKind.NameOnly ? "?" : "");
            lines.Add($"  {Ident(e.ToNode)} {left}--{right} {Ident(e.FromNode)} : \"{label}\"");
        }
        return string.Join("\n", lines);
    }

    public static string ToDot(Graph graph, double minConfidence = 0.5)
    {
        var lines = new List<string>
        {
            "digraph dependencies {",
            "  rankdir=LR;",
            "  node [shape=box, style=rounded, fontname=Helvetica, fontsize=10];",
            "  edge [fontname=Helvetica, fontsize=8];",
        };
        foreach (var n in graph.Nodes)
        {
            var label = $"{n.Id}\\n{Fmt.N(n.Rows)} rows";
            if (n.Key is not null)
                label += $"\\nkey: {n.Key}";
            lines.Add($"  {Ident(n.Id)} [label=\"{label}\"];");
        }
        foreach (var e in graph.Edges)
        {
            if (e.Confidence < minConfidence)
                continue;
            var style = e.Kind == EdgeKind.NameOnly ? "dashed" : "solid";
            var conf = e.Confidence.ToString("F2", CultureInfo.InvariantCulture);
            lines.Add($"  {Ident(e.FromNode)} -> {Ident(e.ToNode)} [label=\"{e.FromColumn} ({conf})\", style={style}];");
        }
        lines.Add("}");
        return string.Join("\n", lines);
    }

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotIdent();
}

internal static class ToolVersion
{
    /// <summary>The tool's version, from the project file.</summary>
    public static readonly string Current =
        typeof(ToolVersion).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}
