// The memory the column profilers of one file may hold between them.
//
// A profiler remembers what it has seen of its column: a hash per distinct
// value, or each distinct identifier with its row count. That grows with the
// number of distinct values, which nothing about the file bounds. So the
// profilers of a file share one allowance, and once their entries come to more
// than it, the profiler holding the most writes them to disk as a sorted run and
// starts again empty. Finishing a column merges its runs in one streaming pass,
// so a file takes the same memory however large it is, and the statistics come
// out exactly as if everything had stayed in memory.

namespace DepGraph;

internal sealed class Spill(string directory, string prefix, long limit) : IDisposable
{
    readonly List<ColumnProfiler> profilers = [];
    readonly List<string> runs = [];

    /// <summary>Estimated bytes the profilers hold between them.</summary>
    public long Held { get; private set; }

    /// <summary>Runs written so far.</summary>
    public int Runs => runs.Count;

    public void Add(ColumnProfiler profiler) => profilers.Add(profiler);

    /// <summary>A path for the next run; the file is deleted with this object.</summary>
    public string NextRun()
    {
        var path = Path.Combine(directory, $"{prefix}run{runs.Count}");
        runs.Add(path);
        return path;
    }

    /// <summary>A profiler took or released <paramref name="bytes"/>; past the limit, the largest write runs.</summary>
    public void Grew(long bytes)
    {
        Held += bytes;
        if (bytes <= 0)
            return;
        while (Held > limit && profilers.MaxBy(p => p.Held) is { Held: > 0 } largest)
            largest.WriteRun();
    }

    public void Dispose()
    {
        foreach (var run in runs)
            File.Delete(run);
        runs.Clear();
    }
}
