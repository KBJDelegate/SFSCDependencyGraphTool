// Pass 2: turn profiled columns into edges.
//
// Candidate targets come from three independent signals — the type token inside
// the value, the column's name, and dialect built-ins — and every candidate is
// then *checked* by joining the staged identifier columns, so each edge carries a
// measured resolve rate rather than a guess.

using System.Globalization;

namespace DepGraph;

public static class Infer
{
    /// <summary>
    /// At or above this resolve rate the relationship is treated as proven. Orphan
    /// rows below it are a data-quality question, not a doubt about the schema.
    /// </summary>
    public const double VerifiedAt = 0.9;

    /// <summary>
    /// Confidence that this is the right target.
    ///
    /// The measured resolve rate dominates deliberately: whether a column *name*
    /// happens to contain the target's name is cosmetic, and weighting it heavily
    /// made fully-resolved edges look uncertain (Lead.ConvertedAccountId resolves
    /// 100% but shares no name with Account).
    /// </summary>
    public static double Score(double resolve, bool tokenMatch, bool nameMatch, bool suffix)
    {
        var conf = 0.70 * resolve
            + 0.20 * (tokenMatch ? 1 : 0)
            + 0.05 * (nameMatch ? 1 : 0)
            + 0.05 * (suffix ? 1 : 0);
        return Fmt.Round(Math.Min(conf, 0.99), 3);
    }

    sealed class Candidate
    {
        public string? Token;
        public bool Name;
        public bool Overlap;
    }

    public static Graph Run(
        List<Node> nodes,
        Profile profile,
        double minConfidence = 0.5,
        double minTokenShare = 0.01,
        double overlapThreshold = 0.8)
    {
        var graph = new Graph { Nodes = nodes, Profile = profile.Name };
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        var tokenMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var nameMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (n.KeyToken is not null && n.Key is not null)
                tokenMap.TryAdd(n.KeyToken, n.Id);
            nameMap.TryAdd(n.Id.ToLowerInvariant(), n.Id);
            nameMap.TryAdd(n.Id.ToLowerInvariant().Replace("_", ""), n.Id);
        }

        var best = new Dictionary<(string, string, string), Edge>();
        var cache = new Dictionary<(string, string, string?), (double Rate, long Matched, long Total)>();

        (double Rate, long Matched, long Total) Resolve(string src, string tgt, string? token)
        {
            if (!cache.TryGetValue((src, tgt, token), out var r))
                cache[(src, tgt, token)] = r = Staging.Resolve(src, tgt, token);
            return r;
        }

        foreach (var node in nodes)
        {
            foreach (var col in node.Columns)
            {
                if (!col.IsIdLike || col.Name == node.Key)
                    continue;
                if (!node.Staged.TryGetValue(col.Name, out var stagedSrc))
                    continue;

                var @base = profile.ReferenceBase(col.Name);
                var suffix = @base is not null;
                var aliases = @base is not null ? profile.NameAliases(@base) : [];
                bool Shared(long rows) => rows / (double)Math.Max(col.NonNull, 1) >= minTokenShare;

                // --- candidate targets, remembering which signal produced each ---
                var cands = new OrderedDictionary<string, Candidate>(StringComparer.Ordinal);
                Candidate Cand(string target)
                {
                    if (!cands.TryGetValue(target, out var c))
                        cands[target] = c = new Candidate();
                    return c;
                }

                foreach (var (token, rows) in col.IdTokens)
                {
                    if (!Shared(rows))
                        continue;
                    if (tokenMap.TryGetValue(token, out var target))
                    {
                        Cand(target).Token = token;
                    }
                    else
                    {
                        var guess = profile.NameForToken(token);
                        graph.Unresolved.Add(new Unresolved(node.Id, col.Name, token, rows,
                            guess is not null
                                ? $"key prefix {token} looks like {guess}, which is not in this extract"
                                : $"key prefix {token} matches no sheet in this extract"));
                    }
                }

                foreach (var alias in aliases.Order(StringComparer.Ordinal))
                {
                    if (nameMap.TryGetValue(alias, out var target))
                        Cand(target).Name = true;
                }

                foreach (var builtin in profile.BuiltinTargets(col.Name))
                {
                    if (nameMap.TryGetValue(builtin.ToLowerInvariant(), out var target))
                        Cand(target).Name = true;
                }

                // No signal from token or name: fall back to measuring value overlap
                // against every known key. The staged columns are small, so probing
                // them all is cheap, and this is what carries the generic profile.
                if (cands.Count == 0 && overlapThreshold > 0)
                {
                    foreach (var tnode in nodes)
                    {
                        // The same node is a valid target: self-references like
                        // Account.ParentId -> Account.Id are real. The key column
                        // itself was already skipped above.
                        if (tnode.Key is null || !tnode.Staged.TryGetValue(tnode.Key, out var stagedTgt))
                            continue;
                        if (Resolve(stagedSrc, stagedTgt, null).Rate >= overlapThreshold)
                            Cand(tnode.Id).Overlap = true;
                    }
                }

                if (cands.Count == 0 && col.IdTokens.Count == 0)
                    graph.Unresolved.Add(new Unresolved(node.Id, col.Name, null, col.NonNull,
                        "identifier-shaped column with no matching sheet"));

                var tokenCands = cands.Count(kv => kv.Value.Token is not null);
                // A column whose values carry several type tokens is polymorphic even
                // if only one of those targets happens to be present in the extract.
                var observedTokens = col.IdTokens.Count(t => Shared(t.Value));
                var poly = tokenCands > 1
                    || observedTokens > 1
                    || (profile.IsPolymorphic(col.Name) && cands.Count > 1);
                var group = poly ? $"{node.Id}.{col.Name}" : null;

                foreach (var (target, meta) in cands)
                {
                    if (!byId.TryGetValue(target, out var tnode) || tnode.Key is null)
                        continue;
                    if (!tnode.Staged.TryGetValue(tnode.Key, out var stagedTgt))
                        continue;

                    var (rate, matched, total) = Resolve(stagedSrc, stagedTgt, meta.Token);
                    var conf = Score(rate, meta.Token is not null, meta.Name, suffix);
                    var kind = target == node.Id ? EdgeKind.Self : EdgeKind.Lookup;
                    if (rate == 0.0)
                    {
                        kind = EdgeKind.NameOnly;
                        conf = Math.Min(conf, 0.25);
                    }
                    if (conf < minConfidence)
                        continue;

                    var ev = new List<string>();
                    if (meta.Token is not null)
                        ev.Add($"key prefix {meta.Token} matches {target}.{tnode.Key}");
                    if (meta.Name)
                        ev.Add($"column name implies {target}");
                    if (meta.Overlap)
                        ev.Add("matched by value overlap only, not by name or prefix");
                    ev.Add($"{matched:N0}/{total:N0} references resolve ({Fmt.Percent(rate, 1)})");
                    if (poly)
                        ev.Add("column references more than one object");

                    var edge = new Edge(node.Id, col.Name, target, tnode.Key, kind,
                        col.Unique ? "1:1" : "N:1",
                        conf,
                        Fmt.Round(rate, 4),
                        Fmt.Round(col.Nulls / (double)Math.Max(col.Rows, 1), 4))
                    {
                        Verified = rate >= VerifiedAt,
                        Evidence = ev,
                        PolymorphicGroup = group,
                    };
                    var key = (node.Id, col.Name, target);
                    if (!best.TryGetValue(key, out var old) || old.Confidence < conf)
                        best[key] = edge;
                }
            }
        }

        graph.Edges = best.Values
            .OrderByDescending(e => e.Confidence)
            .ThenBy(e => e.FromNode, StringComparer.Ordinal)
            .ThenBy(e => e.FromColumn, StringComparer.Ordinal)
            .ThenBy(e => e.ToNode, StringComparer.Ordinal)
            .ToList();
        (graph.LoadOrder, graph.Cycles) = Topology(graph);
        return graph;
    }

    /// <summary>
    /// Layer the nodes so every dependency comes before its dependents.
    ///
    /// Cycles are condensed into a single unit first. Salesforce has genuine
    /// circular references (Account -> User -> Contact -> Account), and without
    /// condensing them nothing downstream could ever be placed: because almost
    /// every object has an OwnerId, one tangle involving User made the whole graph
    /// unorderable and collapsed load_order into a single meaningless layer.
    /// </summary>
    public static (List<List<string>> Layers, List<List<string>> Cycles) Topology(Graph graph)
    {
        var deps = new OrderedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
            deps.TryAdd(n.Id, new SortedSet<string>(StringComparer.Ordinal));
        foreach (var e in graph.Edges)
        {
            if (e.ToNode != e.FromNode && deps.ContainsKey(e.ToNode) && deps.TryGetValue(e.FromNode, out var targets))
                targets.Add(e.ToNode);
        }

        // Condense: each strongly connected component becomes one unit, so what is
        // left is a DAG and Kahn's algorithm always terminates having placed all.
        var comps = StronglyConnected(deps);
        var compOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < comps.Count; i++)
        {
            foreach (var n in comps[i])
                compOf[n] = i;
        }
        var cdeps = Enumerable.Range(0, comps.Count).Select(_ => new HashSet<int>()).ToArray();
        foreach (var (node, targets) in deps)
        {
            foreach (var target in targets)
            {
                if (compOf[target] != compOf[node])
                    cdeps[compOf[node]].Add(compOf[target]);
            }
        }

        var placed = new HashSet<int>();
        var remaining = Enumerable.Range(0, comps.Count).ToHashSet();
        var layers = new List<List<string>>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(c => cdeps[c].IsSubsetOf(placed)).ToList();
            if (ready.Count == 0) // impossible for a DAG; keep the output honest if it happens
            {
                layers.Add(remaining.SelectMany(c => comps[c]).Order(StringComparer.Ordinal).ToList());
                break;
            }
            layers.Add(ready.SelectMany(c => comps[c]).Order(StringComparer.Ordinal).ToList());
            placed.UnionWith(ready);
            remaining.ExceptWith(ready);
        }

        var cycles = comps.Where(c => c.Count > 1).ToList();
        cycles.Sort(CompareLists);
        return (layers, cycles);
    }

    static int CompareLists(List<string> a, List<string> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0)
                return c;
        }
        return a.Count.CompareTo(b.Count);
    }

    /// <summary>
    /// Every strongly connected component, singletons included, each sorted.
    /// Tarjan's algorithm, iterative, so a deep graph cannot overflow the stack.
    /// </summary>
    static List<List<string>> StronglyConnected(OrderedDictionary<string, SortedSet<string>> adj)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var output = new List<List<string>>();
        var counter = 0;

        foreach (var root in adj.Keys)
        {
            if (index.ContainsKey(root))
                continue;
            var work = new Stack<(string Node, Stack<string> Children)>();
            void Visit(string n)
            {
                index[n] = low[n] = counter++;
                stack.Push(n);
                onStack.Add(n);
                work.Push((n, new Stack<string>(adj.TryGetValue(n, out var c) ? c : [])));
            }
            Visit(root);

            while (work.Count > 0)
            {
                var (node, children) = work.Peek();
                if (children.TryPop(out var child))
                {
                    if (!index.ContainsKey(child))
                        Visit(child);
                    else if (onStack.Contains(child))
                        low[node] = Math.Min(low[node], index[child]);
                    continue;
                }
                work.Pop();
                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }
                if (low[node] == index[node])
                {
                    var comp = new List<string>();
                    string w;
                    do
                    {
                        w = stack.Pop();
                        onStack.Remove(w);
                        comp.Add(w);
                    } while (w != node);
                    comp.Sort(StringComparer.Ordinal);
                    output.Add(comp);
                }
            }
        }
        return output;
    }
}

/// <summary>Number formatting shared by the log, the evidence strings and the docs; always invariant.</summary>
internal static class Fmt
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A share as a percentage: Percent(0.9795, 1) is "98.0%".</summary>
    public static string Percent(double share, int decimals) =>
        (share * 100).ToString("F" + decimals, Inv) + "%";

    public static string N(long x) => x.ToString("N0", Inv);

    /// <summary>
    /// Round to <paramref name="decimals"/> places the way the written number reads.
    /// Math.Round scales by a power of ten first, which can tip a value over a
    /// boundary (2469/20000 became 0.1234 rather than 0.1235).
    /// </summary>
    public static double Round(double x, int decimals) => double.Parse(x.ToString("F" + decimals, Inv), Inv);
}
