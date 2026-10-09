// Data model for the dependency graph.
//
// Plain classes with no behaviour beyond small lookups, so every pass can be
// tested on hand-built graphs and the JSON renderer decides the wire format.

namespace DepGraph;

/// <summary>One tabular file somewhere in the extract, possibly inside nested zips.</summary>
/// <param name="Path">Local file to read: the zip that holds the member, or the file itself.</param>
/// <param name="Name">Name inside the zip at <paramref name="Path"/>; null when it is a plain file.</param>
/// <param name="Label">Where it came from, for humans: "outer.zip!WE_2.zip!Account.csv".</param>
/// <param name="Crc">CRC-32 from the zip directory, so identical copies are spotted unread.</param>
public sealed record Member(string Path, string? Name, string Label, long Size = 0, uint? Crc = null)
{
    public string BaseName
    {
        get
        {
            var name = (Name ?? Path).Replace('\\', '/');
            return name[(name.LastIndexOf('/') + 1)..];
        }
    }
}

public sealed class ColumnStats
{
    public required string Name { get; init; }
    public required string Type { get; set; }
    public long Rows { get; set; }
    public long Nulls { get; set; }
    public long Distinct { get; set; }

    /// <summary>
    /// For profiles that encode the target type inside the value (Salesforce key
    /// prefixes), the observed tokens and how many rows carried each, most common first.
    /// </summary>
    public List<KeyValuePair<string, long>> IdTokens { get; set; } = [];

    public bool IsIdLike { get; set; }

    /// <summary>Longest value in characters, for text.</summary>
    public int? MaxLength { get; set; }

    public long NonNull => Rows - Nulls;
    public bool Unique => NonNull > 0 && Distinct == NonNull;
}

/// <summary>One object in the extract: a table, possibly read from several files.</summary>
public sealed class Node
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public string Sheet { get; init; } = "";
    public long Rows { get; set; }
    public List<ColumnStats> Columns { get; set; } = [];
    public string? Key { get; set; }
    public string? KeyToken { get; set; }

    /// <summary>Column name -> staged file of (value, row count) for identifier columns.</summary>
    public Dictionary<string, string> Staged { get; } = [];

    /// <summary>
    /// Column name -> staged file of its distinct values, only for split objects,
    /// where it lets distinct counts be combined exactly across the parts: the
    /// identifier file for an identifier column, else a file of value hashes.
    /// </summary>
    public Dictionary<string, string> Hashed { get; } = [];

    public List<string> Warnings { get; set; } = [];

    /// <summary>Labels of every file this object was read from; more than one when an export split it.</summary>
    public List<string> Parts { get; set; } = [];

    /// <summary>Rows read from each of <see cref="Parts"/>, in the same order; they sum to <see cref="Rows"/>.</summary>
    public List<long> PartRows { get; set; } = [];

    public long SizeBytes { get; set; }

    public ColumnStats? Column(string name) => Columns.Find(c => c.Name == name);
}

public static class EdgeKind
{
    public const string Lookup = "lookup";
    public const string Self = "self";
    public const string NameOnly = "name-only";
}

public sealed record Edge(
    string FromNode,
    string FromColumn,
    string ToNode,
    string ToColumn,
    string Kind,
    string Cardinality,
    double Confidence,
    double ResolveRate,
    double NullPct)
{
    /// <summary>
    /// True when the references were actually found in the target key, i.e. the
    /// relationship is proven by data rather than inferred from naming.
    /// </summary>
    public bool Verified { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string? PolymorphicGroup { get; init; }
}

public sealed record Unresolved(string Node, string Column, string? Token, long Rows, string Reason);

public sealed class GraphStats
{
    public int Sheets { get; init; }
    public long Rows { get; init; }
    public int Columns { get; init; }
    public int Edges { get; init; }
    public int Unresolved { get; init; }
    public int Files { get; init; }
    public int SplitObjects { get; init; }
    public double ReadSeconds { get; init; }
    public double TotalSeconds { get; init; }
    public bool Sampled { get; init; }
    public string? IncludeList { get; init; }
    public IReadOnlyList<string> UnmatchedIncludeNames { get; init; } = [];
}

public sealed class Graph
{
    public List<Node> Nodes { get; init; } = [];
    public List<Edge> Edges { get; set; } = [];
    public List<Unresolved> Unresolved { get; } = [];
    public string Profile { get; init; } = "generic";
    public string Source { get; set; } = "";
    public GraphStats? Stats { get; set; }
    public List<List<string>> LoadOrder { get; set; } = [];
    public List<List<string>> Cycles { get; set; } = [];

    public Node? Node(string id) => Nodes.Find(n => n.Id == id);
}
