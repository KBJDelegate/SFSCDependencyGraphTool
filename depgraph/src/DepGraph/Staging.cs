// Staged columns: what pass 1 leaves on disk for pass 2.
//
// Two kinds of file, both sorted so that every later step is a single streaming
// pass with constant memory:
//
//   *.ids   distinct value -> row count, ordinal order, for identifier columns.
//           Resolving a reference is a merge-join of two of these.
//   *.hash  distinct 64-bit value hashes, ascending, for the other columns of a
//           split object. A k-way merge counts distinct values across the parts exactly.
//
// A profiler that runs out of memory writes the same two kinds of file as
// sorted runs, and merging its runs gives the staged file.

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

    static BinaryWriter? Writer(string? path) =>
        path is null ? null : new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, Buffer));

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

    public static IEnumerable<ulong> ReadHashes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer);
        using var r = new BinaryReader(stream);
        for (var n = stream.Length / sizeof(ulong); n > 0; n--)
            yield return r.ReadUInt64();
    }

    /// <summary>Merge several sorted count files into one, adding the counts of values they share.</summary>
    public static long MergeCounts(IReadOnlyList<string> inputs, string output) => MergeCounts(inputs.Select(ReadCounts), output);

    /// <summary>
    /// Merge sorted (value, count) sequences, adding the counts of values they
    /// share, into <paramref name="output"/> when given; returns the distinct values.
    /// </summary>
    public static long MergeCounts(IEnumerable<IEnumerable<(string Value, long Count)>> sorted, string? output)
    {
        using var w = Writer(output);
        long distinct = 0;
        string? value = null;
        long count = 0;
        foreach (var item in Merge(sorted, (a, b) => string.CompareOrdinal(a.Value, b.Value)))
        {
            if (item.Value == value)
            {
                count += item.Count;
                continue;
            }
            if (value is not null)
            {
                w?.Write(value);
                w?.Write7BitEncodedInt64(count);
            }
            (value, count) = item;
            distinct++;
        }
        if (value is not null)
        {
            w?.Write(value);
            w?.Write7BitEncodedInt64(count);
        }
        return distinct;
    }

    /// <summary>Merge sorted hash sequences into <paramref name="output"/> when given, each hash once; returns how many.</summary>
    public static long MergeHashes(IEnumerable<IEnumerable<ulong>> sorted, string? output)
    {
        using var w = Writer(output);
        long distinct = 0;
        ulong? last = null;
        foreach (var h in Merge(sorted, (a, b) => a.CompareTo(b)))
        {
            if (h == last)
                continue;
            w?.Write(h);
            distinct++;
            last = h;
        }
        return distinct;
    }

    /// <summary>
    /// Distinct values over several staged files of one column: a value present in
    /// two parts counts once. Identifiers (.ids) compare by value and the rest
    /// (.hash) by hash; when a column is staged both ways, the identifiers are
    /// hashed and sorted into <paramref name="scratch"/> files to compare them.
    /// </summary>
    public static long CountDistinct(IReadOnlyList<string> inputs, string scratch)
    {
        var isIds = inputs.Select(p => p.EndsWith(".ids", StringComparison.Ordinal)).ToList();
        if (isIds.All(x => x))
            return MergeCounts(inputs.Select(ReadCounts), null);
        var hashed = inputs.Select((p, i) => isIds[i] ? HashIds(p, $"{scratch}_{i}") : p).ToList();
        try
        {
            return MergeHashes(hashed.Select(ReadHashes), null);
        }
        finally
        {
            foreach (var (p, i) in hashed.Select((p, i) => (p, i)).Where(t => isIds[t.i]))
                File.Delete(p);
        }
    }

    /// <summary>A staged identifier file as a .hash file, sorted a chunk at a time so memory stays bounded.</summary>
    static string HashIds(string ids, string stem)
    {
        const int chunk = 1 << 22; // 32 MB of hashes
        var runs = new List<string>();
        try
        {
            foreach (var part in ReadCounts(ids).Select(x => ColumnProfiler.Hash(x.Value)).Chunk(chunk))
            {
                Array.Sort(part);
                var run = $"{stem}.run{runs.Count}";
                WriteHashes(run, part);
                runs.Add(run);
            }
            MergeHashes(runs.Select(ReadHashes), stem + ".hash");
            return stem + ".hash";
        }
        finally
        {
            foreach (var run in runs)
                File.Delete(run);
        }
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
}
