// Staged columns: what pass 1 leaves on disk for pass 2.
//
// Two kinds of file, both sorted so that every later step is a single streaming
// pass with constant memory:
//
//   *.ids   distinct value -> row count, ordinal order, for identifier columns.
//           Resolving a reference is a merge-join of two of these.
//   *.hash  distinct 64-bit value hashes, ascending, for every column of a split
//           object. A k-way merge counts distinct values across the parts exactly.

namespace DepGraph;

internal static class Staging
{
    const int Buffer = 1 << 16;

    public static void WriteCounts(string path, IEnumerable<KeyValuePair<string, long>> sorted)
    {
        using var w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, Buffer));
        foreach (var (value, count) in sorted)
        {
            w.Write(value);
            w.Write7BitEncodedInt64(count);
        }
    }

    public static IEnumerable<(string Value, long Count)> ReadCounts(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer);
        using var r = new BinaryReader(stream);
        var end = stream.Length;
        while (stream.Position < end)
            yield return (r.ReadString(), r.Read7BitEncodedInt64());
    }

    public static void WriteHashes(string path, IEnumerable<ulong> sorted)
    {
        using var w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, Buffer));
        foreach (var h in sorted)
            w.Write(h);
    }

    static IEnumerable<ulong> ReadHashes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer);
        using var r = new BinaryReader(stream);
        for (var n = stream.Length / sizeof(ulong); n > 0; n--)
            yield return r.ReadUInt64();
    }

    /// <summary>Merge several sorted count files into one, adding the counts of values they share.</summary>
    public static void MergeCounts(IReadOnlyList<string> inputs, string output)
    {
        WriteCounts(output, Merge(inputs.Select(ReadCounts), (a, b) => string.CompareOrdinal(a.Value, b.Value))
            .GroupAdjacent((a, b) => a.Value == b.Value)
            .Select(run => KeyValuePair.Create(run[0].Value, run.Sum(x => x.Count))));
    }

    /// <summary>Distinct values over several hash files: a value present in two parts counts once.</summary>
    public static long CountDistinct(IReadOnlyList<string> inputs)
    {
        long distinct = 0;
        ulong? last = null;
        foreach (var h in Merge(inputs.Select(ReadHashes), (a, b) => a.CompareTo(b)))
        {
            if (h != last)
                distinct++;
            last = h;
        }
        return distinct;
    }

    /// <summary>
    /// Share of a staged column's references found in a target's staged key,
    /// weighted by rows: (rate, matched rows, total rows). With a token, only
    /// values starting with it are counted.
    /// </summary>
    public static (double Rate, long Matched, long Total) Resolve(string source, string target, string? token)
    {
        long matched = 0, total = 0;
        using var keys = ReadCounts(target).GetEnumerator();
        var more = keys.MoveNext();
        foreach (var (value, count) in ReadCounts(source))
        {
            if (token is not null && !value.StartsWith(token, StringComparison.Ordinal))
                continue;
            total += count;
            while (more && string.CompareOrdinal(keys.Current.Value, value) < 0)
                more = keys.MoveNext();
            if (more && keys.Current.Value == value)
                matched += count;
        }
        return total == 0 ? (0.0, 0, 0) : (matched / (double)total, matched, total);
    }

    /// <summary>K-way merge of sorted sequences.</summary>
    static IEnumerable<T> Merge<T>(IEnumerable<IEnumerable<T>> sequences, Comparison<T> compare)
    {
        var cursors = sequences.Select(s => s.GetEnumerator()).ToList();
        try
        {
            var heap = new PriorityQueue<int, T>(Comparer<T>.Create(compare));
            for (var i = 0; i < cursors.Count; i++)
            {
                if (cursors[i].MoveNext())
                    heap.Enqueue(i, cursors[i].Current);
            }
            while (heap.TryDequeue(out var i, out var item))
            {
                yield return item;
                if (cursors[i].MoveNext())
                    heap.Enqueue(i, cursors[i].Current);
            }
        }
        finally
        {
            foreach (var c in cursors)
                c.Dispose();
        }
    }

    static IEnumerable<List<T>> GroupAdjacent<T>(this IEnumerable<T> items, Func<T, T, bool> same)
    {
        var run = new List<T>();
        foreach (var item in items)
        {
            if (run.Count > 0 && !same(run[^1], item))
            {
                yield return run;
                run = [];
            }
            run.Add(item);
        }
        if (run.Count > 0)
            yield return run;
    }
}
