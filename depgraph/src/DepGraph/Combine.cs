// Combining the parts of a split object, and choosing every object's key.
//
// Each part was profiled on its own. Rows, empty counts and key-prefix counts
// add up; distinct counts come from the staged hashes, so a value present in two
// parts counts once; identifier columns are merged into one staged column so
// pass 2 resolves references against every part.


namespace DepGraph;

internal static class Combine
{
    public static List<Node> Parts(List<Node> partNodes, string staging, Profile profile, Dictionary<string, List<string>> skipped)
    {
        var groups = partNodes
            .GroupBy(n => n.Id.ToLowerInvariant(), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var output = new List<Node>();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var parts = groups[gi].ToList();
            var node = parts.Count == 1 ? parts[0] : Merge(parts, staging, gi);
            if (node.PartRows.Count == 0)
                node.PartRows = [node.Rows];
            Finish(node, profile, parts);
            var copies = parts
                .SelectMany(p => skipped.GetValueOrDefault(Sources.MemberKey(p.Source)) ?? [])
                .Distinct()
                .Order(StringComparer.Ordinal);
            foreach (var label in copies)
                node.Warnings.Add($"skipped {label}: an identical copy of another part");
            output.Add(node);
        }
        return output.OrderBy(n => n.Id.ToLowerInvariant(), StringComparer.Ordinal).ToList();
    }

    static Node Merge(List<Node> parts, string staging, int groupNo)
    {
        parts = parts.OrderBy(n => n.Source, StringComparer.Ordinal).ToList();
        var first = parts[0];
        var node = new Node
        {
            Id = first.Id,
            Source = first.Source,
            Sheet = first.Sheet,
            Rows = parts.Sum(p => p.Rows),
            Parts = parts.Select(p => p.Source).ToList(),
            PartRows = parts.Select(p => p.Rows).ToList(),
            SizeBytes = parts.Sum(p => p.SizeBytes),
        };
        foreach (var p in parts)
            node.Warnings.AddRange(p.Warnings.Where(w => w != "empty sheet").Select(w => $"{p.Source}: {w}"));

        var byName = parts.Select(p => p.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal)).ToList();
        var names = parts.SelectMany(p => p.Columns).Select(c => c.Name).Distinct().ToList();
        var outStem = Path.Combine(staging, $"m{groupNo:D5}_{Sources.Safe(node.Id)}");
        foreach (var (name, index) in names.Select((n, i) => (n, i)))
        {
            var present = parts.Zip(byName)
                .Where(pc => pc.Second.ContainsKey(name))
                .Select(pc => (Part: pc.First, Column: pc.Second[name]))
                .ToList();
            // The index keeps "Ejer_æ" and "Ejer_ø", both Ejer__ once made safe, apart.
            var (stats, staged) = MergeColumn(name, node.Rows, present, $"{outStem}__{index}");
            node.Columns.Add(stats);
            if (staged is not null)
                node.Staged[name] = staged;
        }

        if (node.Rows == 0 && parts.Any(p => p.Warnings.Contains("empty sheet")))
            node.Warnings.Insert(0, "empty sheet");
        var readable = parts.Zip(byName).Where(pc => pc.First.Columns.Count > 0).Select(pc => pc.Second).ToList();
        var missing = names.Where(n => readable.Any(cols => !cols.ContainsKey(n))).ToList();
        if (missing.Count > 0)
            node.Warnings.Add(
                $"{missing.Count} column(s) missing from some parts, counted as empty there: "
                + string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? " ..." : ""));
        return node;
    }

    /// <summary>One column's statistics over every part, plus its merged staging file.</summary>
    static (ColumnStats, string?) MergeColumn(string name, long rows, List<(Node Part, ColumnStats Column)> present, string outStem)
    {
        var cols = present.Select(p => p.Column).ToList();
        var filled = cols.Where(c => c.NonNull > 0).ToList();
        var nonNull = filled.Sum(c => c.NonNull);

        var idRows = filled.Where(c => c.IsIdLike).Sum(c => c.NonNull);
        var idLike = filled.Count > 0 && idRows * 2 >= nonNull;
        var stats = new ColumnStats
        {
            Name = name,
            Type = MergeType((filled.Count > 0 ? filled : [cols[0]]).Select(c => c.Type).ToList(), idLike),
            Rows = rows,
            // A part without this column contributes its rows as empty.
            Nulls = rows - nonNull,
            IsIdLike = idLike,
            MaxLength = filled.Where(c => c.MaxLength > 0).Select(c => c.MaxLength).Max(),
            IdTokens = ColumnProfiler.MostCommon(Sum(filled.Select(c => c.IdTokens))).Take(12).ToList(),
        };

        var hashes = present.Where(p => p.Part.Hashed.ContainsKey(name)).Select(p => p.Part.Hashed[name]).ToList();
        if (hashes.Count > 0)
            stats.Distinct = Staging.CountDistinct(hashes);
        else if (filled.Count > 0) // not staged (should not happen); a lower bound beats nothing
            stats.Distinct = filled.Max(c => c.Distinct);

        var staged = present.Where(p => p.Part.Staged.ContainsKey(name)).Select(p => p.Part.Staged[name]).ToList();
        if (!idLike || staged.Count == 0)
            return (stats, null);
        if (staged.Count == 1)
            return (stats, staged[0]);
        var path = $"{outStem}_{Sources.Safe(name)}.ids";
        Staging.MergeCounts(staged, path);
        return (stats, path);
    }

    static Dictionary<string, long> Sum(IEnumerable<IEnumerable<KeyValuePair<string, long>>> counts)
    {
        var total = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, n) in counts.SelectMany(c => c))
            total[key] = total.GetValueOrDefault(key) + n;
        return total;
    }

    static string MergeType(List<string> types, bool idLike)
    {
        if (idLike)
            return "id";
        var kinds = types.Where(t => t is not "empty" and not "id").ToHashSet();
        if (kinds.Count == 0)
            return types.Count > 0 ? types[0] : "empty";
        if (kinds.Count == 1)
            return kinds.First();
        if (kinds.IsSubsetOf(["int", "float"]))
            return "float";
        if (kinds.IsSubsetOf(["date", "datetime"]))
            return "datetime";
        return "string";
    }

    static string? PickKey(string nodeId, List<ColumnStats> columns, Profile profile)
    {
        var ranked = columns
            .Select((c, i) => (Rank: profile.KeyRank(c.Name, nodeId), Index: i, Column: c))
            .OrderBy(t => t.Rank).ThenBy(t => t.Index);
        foreach (var (rank, _, col) in ranked)
        {
            if (rank < 99 && col.Unique)
                return col.Name;
        }
        // Fall back to the first unique identifier-shaped column.
        return columns.FirstOrDefault(c => c.IsIdLike && c.Unique)?.Name;
    }

    /// <summary>Pick the key once the statistics cover the whole object.</summary>
    static void Finish(Node node, Profile profile, List<Node> byPart)
    {
        node.Key = PickKey(node.Id, node.Columns, profile);
        if (node.Key is null && byPart.Count > 1)
        {
            // Unique within every part but not across them: the parts overlap. Still
            // the key, and worth saying so, because it means rows are duplicated.
            foreach (var col in node.Columns)
            {
                if (profile.KeyRank(col.Name, node.Id) >= 99 || col.NonNull == 0)
                    continue;
                if (byPart.All(p => p.Column(col.Name) is not { } pc || pc.Unique || pc.NonNull == 0))
                {
                    node.Key = col.Name;
                    node.Warnings.Add(
                        $"key {col.Name} repeats {col.NonNull - col.Distinct:N0} value(s) across parts, so the parts "
                        + "overlap and some rows are counted twice");
                    break;
                }
            }
        }
        if (node.Key is not null && node.Column(node.Key) is { IdTokens.Count: > 0 } kc)
            node.KeyToken = kc.IdTokens[0].Key;
    }
}
