// Profiles one column as its cells stream past, without holding the column.
//
// What is kept per column is only what the statistics need: a 64-bit hash per
// distinct value (for the distinct count, and for merging split objects), value
// counts while there are at most Ingest.TopValues distinct values (picklists),
// the first sampleN values (to decide whether the column holds identifiers or
// ISO dates), and, once that sample says identifiers, each distinct value with
// its row count, which is what gets staged for pass 2. From then on that
// dictionary also gives the distinct count, so the column stops hashing.

using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DepGraph;

internal sealed partial class ColumnProfiler(string name, Profile profile, int sampleN)
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const NumberStyles FloatStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    long rows, nulls;

    // How many values parsed as each kind; the column's type is decided at the end.
    long ints, floats, bools, dates, texts;
    long minLong = long.MaxValue, maxLong = long.MinValue;
    double minDouble = double.PositiveInfinity, maxDouble = double.NegativeInfinity;
    DateTime minDate = DateTime.MaxValue, maxDate = DateTime.MinValue;
    string? minText, maxText;
    bool rangeOfText = true; // false once the sample rules out ISO dates, the only text with a range
    int maxLength;

    readonly HashSet<ulong> hashes = [];
    Dictionary<string, long>? top = new(StringComparer.Ordinal);
    readonly List<string> sample = [];
    bool decided;
    Dictionary<string, long>? ids;

    public string Name => name;
    public long NonNull => rows - nulls;

    /// <summary>The distinct values with their row counts, once <see cref="Finish"/> found identifiers.</summary>
    public KeyValuePair<string, long>[] SortedIds()
    {
        var sorted = (ids ?? []).ToArray();
        Array.Sort(sorted, (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return sorted;
    }

    public ulong[] SortedHashes()
    {
        var sorted = ids is null ? hashes.ToArray() : ids.Keys.Select(Hash).ToArray();
        Array.Sort(sorted);
        return sorted;
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
            SeeLong(l);
            l.TryFormat(canonical, out n, default, Inv);
        }
        else if (double.TryParse(text, FloatStyle, Inv, out var d) && d.TryFormat(canonical, out n, "R", Inv))
        {
            SeeDouble(d);
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
                SeeLong((long)d);
                Observe(((long)d).ToString(Inv));
                return;
            case double d:
                rows++;
                SeeDouble(d);
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
                if (t < minDate) minDate = t;
                if (t > maxDate) maxDate = t;
                Observe(t.ToString(t.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff", Inv));
                return;
            default:
                AddText(Convert.ToString(value, Inv) ?? "", parse: false);
                return;
        }
    }

    void SeeLong(long l)
    {
        ints++;
        if (l < minLong) minLong = l;
        if (l > maxLong) maxLong = l;
        SeeNumber(l);
    }

    void SeeDouble(double d)
    {
        floats++;
        SeeNumber(d);
    }

    void SeeNumber(double d)
    {
        if (d < minDouble) minDouble = d;
        if (d > maxDouble) maxDouble = d;
    }

    static ulong Hash(string text) => Hash(text.AsSpan());

    static ulong Hash(ReadOnlySpan<char> text) => XxHash3.HashToUInt64(MemoryMarshal.AsBytes(text));

    void Observe(ReadOnlySpan<char> text)
    {
        // An identifier column counts its distinct values in ids instead.
        if (ids is null)
            hashes.Add(Hash(text));

        if (top is not null)
        {
            var lookup = top.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(text, out var n))
                lookup[text] = n + 1;
            else if (top.Count == Ingest.TopValues)
                top = null; // too many to list
            else
                lookup[text] = 1;
        }

        if (!decided)
        {
            sample.Add(text.ToString());
            if (sample.Count >= sampleN)
                Decide();
        }
        else if (ids is not null)
        {
            CollectionsMarshal.GetValueRefOrAddDefault(ids.GetAlternateLookup<ReadOnlySpan<char>>(), text, out _)++;
        }

        if (rangeOfText)
        {
            if (minText is null || text.CompareTo(minText, StringComparison.Ordinal) < 0)
                minText = text.ToString();
            if (maxText is null || text.CompareTo(maxText, StringComparison.Ordinal) > 0)
                maxText = text.ToString();
        }
        if (text.Length > maxLength)
            maxLength = Math.Max(maxLength, CodePoints(text));
    }

    /// <summary>The sample is complete: from here on, count every value if they are identifiers.</summary>
    void Decide()
    {
        decided = true;
        rangeOfText = DateKind(sample) is not null;
        if (!profile.LooksLikeId(sample))
            return;
        rangeOfText = false;
        ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var s in sample)
            ids[s] = ids.GetValueOrDefault(s) + 1;
        hashes.Clear();
        hashes.TrimExcess();
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

    public ColumnStats Finish()
    {
        var stats = new ColumnStats
        {
            Name = name,
            Type = TypeName(),
            Rows = rows,
            Nulls = nulls,
            Distinct = ids?.Count ?? hashes.Count,
        };
        if (NonNull == 0)
            return stats;

        if (top is not null && stats.Distinct is > 0 and <= Ingest.TopValues)
        {
            stats.TopValues = MostCommon(top).ToList();
        }
        switch (stats.Type)
        {
            case "int":
                (stats.Min, stats.Max) = (minLong, maxLong);
                break;
            case "float":
                (stats.Min, stats.Max) = (minDouble, maxDouble);
                break;
            case "datetime":
                (stats.Min, stats.Max) = (minDate, maxDate);
                break;
        }
        if (stats.Type != "string")
            return stats;

        stats.MaxLength = maxLength;
        if (!decided)
            Decide();
        stats.IsIdLike = ids is not null;
        if (!stats.IsIdLike)
        {
            var kind = DateKind(sample);
            if (kind is not null)
            {
                stats.Type = kind;
                (stats.Min, stats.Max) = (minText, maxText);
            }
            return stats;
        }

        stats.Type = "id";
        if (profile.EncodesTypeInValue)
        {
            // Dialect encodes the target type in the value: count tokens over all rows.
            var tokens = ids!
                .Where(kv => kv.Key.Length is 15 or 18)
                .GroupBy(kv => kv.Key[..3], StringComparer.Ordinal)
                .Select(g => KeyValuePair.Create(g.Key, g.Sum(kv => kv.Value)));
            stats.IdTokens = MostCommon(tokens).Take(12).ToList();
        }
        return stats;
    }

    /// <summary>Most common first; ties in ordinal order, so output never depends on hashing.</summary>
    public static IEnumerable<KeyValuePair<string, long>> MostCommon(IEnumerable<KeyValuePair<string, long>> counts) =>
        counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal);

    /// <summary>
    /// "date" or "datetime" when the sampled text is ISO dates, else null. CSV
    /// exports carry dates as text; recognising them lets the docs give a date
    /// range instead of calling CreatedDate a string.
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
