// Profiles one column as its cells stream past, without holding the column.
//
// What is kept per column is only what the statistics need: a 64-bit hash per
// distinct value (for the distinct count, and for merging split objects), the
// first sampleN values (to decide whether the column holds identifiers or ISO
// dates), and, once that sample says identifiers, each distinct value with its
// row count, which is what gets staged for pass 2. From then on that dictionary
// also gives the distinct count, so the column stops hashing.
//
// The hashes and identifiers grow with the number of distinct values, so they
// do not stay in memory: once the profilers of a file hold more than their
// Spill allows, the largest writes what it has to disk as a sorted run and
// starts again. Finish merges the runs with what is left in memory, so every
// count is exact, as if nothing had left memory.
//
// No data value leaves this class except the staged identifiers, which pass 2
// needs to check references and which never reach the output: the statistics
// are types and counts only, never values, ranges or picklists.

using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DepGraph;

internal sealed partial class ColumnProfiler
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const NumberStyles FloatStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    // Rough bytes per entry, counting the collection's own arrays and their room to grow.
    const long HashBytes = 24;
    static long IdBytes(int length) => 64 + 2L * length;

    readonly string name;
    readonly Profile profile;
    readonly int sampleN;
    readonly Spill? spill;

    long rows, nulls;

    // How many values parsed as each kind; the column's type is decided at the end.
    long ints, floats, bools, dates, texts;
    int maxLength;

    HashSet<ulong> hashes = [];
    readonly List<string> sample = [];
    bool decided;
    Dictionary<string, long>? ids;
    readonly Dictionary<string, long> tokens = new(StringComparer.Ordinal);

    // Sorted runs on disk: of hashes until the column is found to hold identifiers, then of identifiers.
    readonly List<string> runs = [];

    /// <param name="spill">Shared by the profilers of one file; without it everything stays in memory.</param>
    public ColumnProfiler(string name, Profile profile, int sampleN, Spill? spill = null)
    {
        this.name = name;
        this.profile = profile;
        this.sampleN = sampleN;
        this.spill = spill;
        spill?.Add(this);
    }

    public string Name => name;
    public long NonNull => rows - nulls;

    /// <summary>Estimated bytes this column's hashes or identifiers hold in memory.</summary>
    public long Held { get; private set; }

    /// <summary>The file <see cref="Finish"/> staged: sorted identifiers (.ids) or sorted hashes (.hash); null if none.</summary>
    public string? StagedPath { get; private set; }

    KeyValuePair<string, long>[] SortedIds()
    {
        var sorted = (ids ?? []).ToArray();
        Array.Sort(sorted, (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return sorted;
    }

    ulong[] SortedHashes()
    {
        var sorted = hashes.ToArray();
        Array.Sort(sorted);
        return sorted;
    }

    void Grow(long bytes)
    {
        Held += bytes;
        spill?.Grew(bytes);
    }

    /// <summary>Write what is in memory to disk as a sorted run and start again empty.</summary>
    internal void WriteRun()
    {
        if (spill is null || Held == 0)
            return;
        var path = spill.NextRun();
        if (ids is not null)
        {
            CountTokens();
            Staging.WriteCounts(path, SortedIds());
            ids = new Dictionary<string, long>(StringComparer.Ordinal);
        }
        else
        {
            Staging.WriteHashes(path, SortedHashes());
            hashes = [];
        }
        runs.Add(path);
        var held = Held;
        Held = 0;
        spill.Grew(-held);
    }

    public void AddNull()
    {
        rows++;
        nulls++;
    }

    /// <summary>A cell read as text. <paramref name="parse"/> is true for CSV, where numbers and flags arrive as text too.</summary>
    public void AddText(ReadOnlySpan<char> text, bool parse)
    {
        if (text.IsEmpty)
        {
            AddNull();
            return;
        }
        rows++;
        // Once one value is plain text the column is text, so stop parsing.
        if (!parse || texts > 0)
        {
            texts++;
            Observe(text);
            return;
        }
        // A number or flag counts by its value, so "7" and "07", or "true" and
        // "TRUE", are one value, and a CSV part agrees with a workbook part.
        Span<char> canonical = stackalloc char[32];
        int n;
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, Inv, out var l))
        {
            ints++;
            l.TryFormat(canonical, out n, default, Inv);
        }
        else if (double.TryParse(text, FloatStyle, Inv, out var d) && d.TryFormat(canonical, out n, "R", Inv))
        {
            floats++;
        }
        else if (text.Equals("true", StringComparison.OrdinalIgnoreCase)
                 || text.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            var b = text.Length == 4;
            bools++;
            (b ? "True" : "False").AsSpan().CopyTo(canonical);
            n = b ? 4 : 5;
        }
        else
        {
            texts++;
            Observe(text);
            return;
        }
        Observe(canonical[..n]);
    }

    /// <summary>A typed cell from a workbook.</summary>
    public void AddValue(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                AddNull();
                return;
            case string s:
                AddText(s, parse: false);
                return;
            case double d when double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 9.2e18:
                rows++;
                ints++;
                Observe(((long)d).ToString(Inv));
                return;
            case double d:
                rows++;
                floats++;
                Observe(d.ToString("R", Inv));
                return;
            case bool b:
                rows++;
                bools++;
                Observe(b ? "True" : "False");
                return;
            case DateTime t:
                rows++;
                dates++;
                Observe(t.ToString(t.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff", Inv));
                return;
            default:
                AddText(Convert.ToString(value, Inv) ?? "", parse: false);
                return;
        }
    }

    internal static ulong Hash(ReadOnlySpan<char> text) => XxHash3.HashToUInt64(MemoryMarshal.AsBytes(text));

    void Observe(ReadOnlySpan<char> text)
    {
        if (text.Length > maxLength)
            maxLength = Math.Max(maxLength, CodePoints(text));

        // An identifier column counts its distinct values in ids instead.
        if (ids is null && hashes.Add(Hash(text)))
            Grow(HashBytes);

        if (!decided)
        {
            sample.Add(text.ToString());
            if (sample.Count >= sampleN)
                Decide();
        }
        else if (ids is not null)
        {
            CollectionsMarshal.GetValueRefOrAddDefault(ids.GetAlternateLookup<ReadOnlySpan<char>>(), text, out var seen)++;
            if (!seen)
                Grow(IdBytes(text.Length)); // last: it may write ids to disk and start a new dictionary
        }
    }

    /// <summary>
    /// For a dialect that encodes the target type in the value, add the rows of
    /// each token among the identifiers in memory, before they go to disk.
    /// </summary>
    void CountTokens()
    {
        if (!profile.EncodesTypeInValue)
            return;
        var lookup = tokens.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var (value, count) in ids!)
        {
            if (value.Length is 15 or 18)
                CollectionsMarshal.GetValueRefOrAddDefault(lookup, value.AsSpan(0, 3), out _) += count;
        }
    }

    /// <summary>The sample is complete: from here on, count every value if they are identifiers.</summary>
    void Decide()
    {
        decided = true;
        if (!profile.LooksLikeId(sample))
            return;
        ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var s in sample)
            ids[s] = ids.GetValueOrDefault(s) + 1;
        // Hashes already on disk are left for the Spill to delete.
        runs.Clear();
        hashes = [];
        var held = ids.Sum(kv => IdBytes(kv.Key.Length)) - Held;
        Held += held;
        spill?.Grew(held);
    }

    static int CodePoints(ReadOnlySpan<char> text)
    {
        var n = text.Length;
        foreach (var c in text)
        {
            if (char.IsLowSurrogate(c))
                n--;
        }
        return n;
    }

    string TypeName()
    {
        if (NonNull == 0)
            return "empty";
        if (texts > 0)
            return "string";
        var kinds = (ints > 0 ? 1 : 0) + (floats > 0 ? 1 : 0) + (bools > 0 ? 1 : 0) + (dates > 0 ? 1 : 0);
        if (kinds == 1 && bools > 0) return "bool";
        if (kinds == 1 && dates > 0) return "datetime";
        if (kinds == 1 && ints > 0) return "int";
        if (bools == 0 && dates == 0) return "float";
        return "string"; // mixed kinds, e.g. numbers and flags in one column
    }

    /// <summary>
    /// The column's statistics. With <paramref name="stage"/>, identifiers are
    /// staged at <c>stage.ids</c> for pass 2, and with <paramref name="keepHashes"/>
    /// (for an object split across files) every other column's hashes at
    /// <c>stage.hash</c>, so the parts' distinct counts can be combined;
    /// <see cref="StagedPath"/> says which was written.
    /// </summary>
    public ColumnStats Finish(string? stage = null, bool keepHashes = false)
    {
        var type = TypeName();
        if (type == "string" && !decided)
            Decide();
        var isId = type == "string" && ids is not null;
        var stats = new ColumnStats
        {
            Name = name,
            Type = type,
            Rows = rows,
            Nulls = nulls,
            Distinct = CountDistinct(stage is not null && (isId || (keepHashes && NonNull > 0)) ? stage : null),
        };
        if (stats.Type != "string")
            return stats;

        stats.MaxLength = maxLength;
        stats.IsIdLike = isId;
        if (!stats.IsIdLike)
        {
            stats.Type = DateKind(sample) ?? stats.Type;
            return stats;
        }

        stats.Type = "id";
        if (profile.EncodesTypeInValue)
        {
            CountTokens();
            stats.IdTokens = MostCommon(tokens).Take(12).ToList();
        }
        return stats;
    }

    /// <summary>Distinct values over the runs and memory, writing them all to <paramref name="stage"/> when given.</summary>
    long CountDistinct(string? stage)
    {
        if (ids is not null)
        {
            if (stage is null && runs.Count == 0)
                return ids.Count;
            StagedPath = stage is null ? null : stage + ".ids";
            IEnumerable<(string, long)> memory = SortedIds().Select(kv => (kv.Key, kv.Value));
            return Staging.MergeCounts(runs.Select(Staging.ReadCounts).Append(memory), StagedPath);
        }
        if (stage is null && runs.Count == 0)
            return hashes.Count;
        StagedPath = stage is null ? null : stage + ".hash";
        return Staging.MergeHashes(runs.Select(Staging.ReadHashes).Append(SortedHashes()), StagedPath);
    }

    /// <summary>Most common first; ties in ordinal order, so output never depends on hashing.</summary>
    public static IEnumerable<KeyValuePair<string, long>> MostCommon(IEnumerable<KeyValuePair<string, long>> counts) =>
        counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal);

    /// <summary>
    /// "date" or "datetime" when the sampled text is ISO dates, else null. CSV
    /// exports carry dates as text; recognising them keeps the docs from calling
    /// CreatedDate a string.
    /// </summary>
    public static string? DateKind(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return null;
        if (values.All(v => IsoDate().IsMatch(v)))
            return "date";
        var hits = values.Count(v => IsoDateTime().IsMatch(v) || IsoDate().IsMatch(v));
        return hits / (double)values.Count >= 0.95 ? "datetime" : null;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}\z")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?\z")]
    private static partial Regex IsoDateTime();
}
