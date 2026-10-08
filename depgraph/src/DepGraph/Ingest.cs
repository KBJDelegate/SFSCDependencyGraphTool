// Pass 1: read every sheet once, profile its columns, stage only what pass 2 needs.
//
// Memory is bounded by the files being read at once, not by the size of the
// extract. That works because .xlsx caps a sheet at 1,048,576 rows and
// Salesforce caps each zip of a data export at ~512 MB, so a multi-GB extract
// is always *many* bounded files rather than one huge one. A file still holds
// about its own size in memory while it is read, so files start only while
// their estimates fit in a memory budget, whatever the worker count. Only the
// identifier columns are staged, so the working set pass 2 joins over is tiny
// even when the extract is enormous.
//
// An object split across several files is still read one part at a time; the
// parts are combined afterwards from mergeable statistics, so splitting never
// costs memory.

using System.Diagnostics;
using System.IO.Compression;

namespace DepGraph;

public sealed record IngestOptions
{
    /// <summary>Parallel readers; default one per CPU.</summary>
    public int? Workers { get; init; }

    /// <summary>
    /// Bytes of memory the readers may hold between them; default
    /// <see cref="Ingest.DefaultMemoryBudget"/>. A file starts only once its
    /// <see cref="Ingest.MemoryEstimate"/> fits beside the files already being
    /// read, so a machine with more cores than memory waits instead of paging.
    /// A file larger than the whole budget is read on its own.
    /// </summary>
    public long? MemoryBudget { get; init; }

    /// <summary>Read at most this many rows per sheet (approximate statistics).</summary>
    public int? MaxRows { get; init; }

    /// <summary>Values sampled to decide whether a column holds identifiers.</summary>
    public int SampleN { get; init; } = 500;

    /// <summary>Called once per file as it finishes, and every <see cref="Heartbeat"/> while none does.</summary>
    public Action<IngestProgress>? Progress { get; init; }

    /// <summary>How long the files being read may go without a report.</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The files to read (from <see cref="Sources.List"/>, perhaps filtered); default all of them.</summary>
    public IReadOnlyList<Member>? Members { get; init; }

    /// <summary>
    /// Whether the caller keeps sheets with no rows. Empty objects are always
    /// returned; this only lets the log say what becomes of an empty file.
    /// </summary>
    public bool KeepEmpty { get; init; }
}

/// <summary>
/// Where pass 1 stands. <paramref name="Message"/> says what became of the file
/// that just finished, or is null for a report while files are still being read.
/// <paramref name="BytesRead"/> counts finished files whole and the others as far
/// as they have been read, so it moves while a large file is being read.
/// </summary>
public sealed record IngestProgress(int Done, int Total, long BytesRead, long TotalBytes, int Reading, TimeSpan Elapsed, string? Message);

public static class Ingest
{
    // Measured on Salesforce CSVs: 1.0 to 1.5 bytes of memory per byte of text,
    // nearly all of it the hash and identifier kept per distinct value. Paging is
    // far slower than reading fewer files at once, so the estimate takes the top
    // of that range. A workbook's size is compressed, so it holds several times
    // its size in text.
    const double CsvFactor = 1.5, WorkbookFactor = 6;

    /// <summary>The memory reading <paramref name="m"/> is expected to take, in bytes.</summary>
    public static long MemoryEstimate(Member m) =>
        (long)(m.Size * (Path.GetExtension(m.BaseName).ToLowerInvariant() is ".csv" or ".tsv" ? CsvFactor : WorkbookFactor));

    /// <summary>Files estimated at this many bytes or more are collected as soon as they finish.</summary>
    const long CollectAfter = 64L << 20;

    /// <summary>80% of the memory free now (at least 1 GB), and that free memory.</summary>
    public static (long Budget, long Free) DefaultMemoryBudget()
    {
        GC.Collect(0); // the machine's memory load is only reported once the GC has run
        var info = GC.GetGCMemoryInfo();
        var free = Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        return (Math.Max(free / 10 * 8, 1L << 30), free);
    }

    /// <summary>Read every object in <paramref name="sources"/> into a profiled Node.</summary>
    public static List<Node> Run(IReadOnlyList<string> sources, string staging, string profileName, IngestOptions? options = null)
    {
        options ??= new IngestOptions();
        var progress = options.Progress ?? (_ => { });
        Directory.CreateDirectory(staging);
        var profile = Profiles.Get(profileName);
        var members = options.Members ?? Sources.List(sources, Path.Combine(staging, "archives"), skipFolders: profile.SkippedFolders);
        if (members.Count == 0)
            throw new UserError($"no {string.Join(", ", Sources.Tabular)} files found in {string.Join(", ", sources)}");

        var (groups, skipped) = Sources.PlanParts(members);
        var partCount = groups.ToDictionary(g => g.Key, g => g.Value.Count, StringComparer.Ordinal);
        // Largest first, so the long jobs start early.
        var tasks = groups.Values
            .SelectMany(parts => parts.Select(m => (Member: m, Split: parts.Count > 1)))
            .OrderByDescending(t => t.Member.Size)
            .ToList();
        var workers = tasks.Count == 1 ? 1 : options.Workers is > 0 and var w ? w : Math.Min(tasks.Count, Environment.ProcessorCount);
        var budget = options.MemoryBudget ?? DefaultMemoryBudget().Budget;
        var cost = tasks.Select(t => MemoryEstimate(t.Member)).ToArray();
        var totalBytes = tasks.Sum(t => t.Member.Size);

        var results = new List<Node>[tasks.Count];
        var read = new long[tasks.Count]; // bytes read so far, per file
        var clock = Stopwatch.StartNew();
        var gate = new object();
        // Guarded by gate: the next task to start, how many are running and the memory they hold.
        int next = 0, reading = 0, done = 0;
        long held = 0;
        var failed = false;
        var lastReport = TimeSpan.Zero;

        IngestProgress Report(string? message)
        {
            lastReport = clock.Elapsed;
            long bytes = 0;
            for (var i = 0; i < tasks.Count; i++)
                bytes += Math.Min(Volatile.Read(ref read[i]), tasks[i].Member.Size);
            return new IngestProgress(done, tasks.Count, bytes, totalBytes, reading, clock.Elapsed, message);
        }

        // One line per file, written once it is finished, saying what it held and,
        // when that is nothing, what becomes of it.
        void Finished(Member m, List<Node> result, double secs)
        {
            var rows = result.Sum(n => n.Rows);
            var parts = partCount[Sources.MemberKey(m)];
            string outcome;
            if (result.Any(n => n.Warnings.Any(w => w.Contains("unreadable"))))
                outcome = "UNREADABLE, listed in the warnings at the end";
            else if (rows > 0 && parts > 1)
                outcome = $"{rows:N0} rows in {secs:F1}s (one of {parts} parts of {Path.GetFileNameWithoutExtension(m.BaseName)})";
            else if (rows > 0)
                outcome = $"{rows:N0} rows in {secs:F1}s";
            else if (parts > 1)
                outcome = $"empty part (the object is split across {parts} files)"; // its other parts decide
            else
                outcome = options.KeepEmpty ? "empty, kept" : "empty, excluded";
            done++;
            progress(Report($"{m.Label}: {outcome}"));
        }

        // Tasks start in order, largest first, each once its memory fits beside
        // the files already being read; with nothing being read, any file fits.
        bool Take(out int i)
        {
            lock (gate)
            {
                while (!failed && next < tasks.Count && held > 0 && held + cost[next] > budget)
                    Monitor.Wait(gate);
                i = next;
                if (failed || next == tasks.Count)
                    return false;
                next++;
                reading++;
                held += cost[i];
                return true;
            }
        }

        void Work()
        {
            while (Take(out var i))
            {
                var (m, split) = tasks[i];
                var started = Stopwatch.StartNew();
                List<Node> nodes;
                try
                {
                    nodes = IngestMember(m, i + 1, split, staging, profile, options.MaxRows, options.SampleN,
                        n => Interlocked.Add(ref read[i], n));
                }
                catch
                {
                    lock (gate)
                    {
                        failed = true; // the others finish what they hold and start nothing new
                        reading--;
                        held -= cost[i];
                        Monitor.PulseAll(gate);
                    }
                    throw;
                }
                results[i] = nodes;
                // The GC would otherwise keep a large file's memory until well after the
                // next file has filled up its own, holding twice what the budget allows.
                if (cost[i] >= CollectAfter)
                    GC.Collect();
                lock (gate)
                {
                    reading--;
                    held -= cost[i];
                    Volatile.Write(ref read[i], m.Size);
                    Monitor.PulseAll(gate);
                    Finished(m, nodes, started.Elapsed.TotalSeconds);
                }
            }
        }

        // While large files are read nothing finishes for a long time; say how far they have got.
        void Heartbeat()
        {
            lock (gate)
            {
                if (reading > 0 && clock.Elapsed - lastReport >= options.Heartbeat)
                    progress(Report(null));
            }
        }

        var period = TimeSpan.FromTicks(Math.Clamp(options.Heartbeat.Ticks / 4, TimeSpan.TicksPerMillisecond * 10, TimeSpan.TicksPerSecond * 5));
        using (new Timer(_ => Heartbeat(), null, period, period))
        {
            // Dedicated threads: a worker waiting for memory must not hold up the thread pool.
            Task.WaitAll(Enumerable.Range(0, workers)
                .Select(_ => Task.Factory.StartNew(Work, TaskCreationOptions.LongRunning))
                .ToArray());
        }

        return Combine.Parts(results.SelectMany(r => r).ToList(), staging, profile, skipped);
    }

    static List<Node> IngestMember(Member member, int partNo, bool split, string staging, Profile profile, int? maxRows, int sampleN,
        Action<int> onRead)
    {
        var extension = Path.GetExtension(member.BaseName).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(member.BaseName);
        List<Sheet> sheets;
        try
        {
            using var stream = new CountingStream(Open(member, extension, staging), onRead);
            sheets = Readers.Read(stream, extension, stem, profile, maxRows, sampleN);
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            return
            [
                new Node
                {
                    Id = stem,
                    Source = member.Label,
                    Parts = [member.Label],
                    SizeBytes = member.Size,
                    Warnings = [$"unreadable: {exc.GetType().Name}: {exc.Message}"],
                },
            ];
        }

        var nodes = new List<Node>();
        for (var sheetNo = 0; sheetNo < sheets.Count; sheetNo++)
        {
            var sheet = sheets[sheetNo];
            var node = new Node
            {
                Id = profile.NodeName(member.BaseName, sheet.Name, sheets.Count),
                Source = member.Label,
                Sheet = sheet.Name,
                Rows = sheet.Rows,
                Parts = [member.Label],
                SizeBytes = member.Size,
            };
            nodes.Add(node);
            if (sheet.Rows == 0 || sheet.Columns.Count == 0)
            {
                node.Warnings.Add("empty sheet");
                continue;
            }

            node.Columns = sheet.Columns.Select(c => c.Finish()).ToList();
            // Column index, not just name, keeps "A b" and "A_b" from colliding.
            var fileStem = Path.Combine(staging, $"p{partNo:D5}s{sheetNo}_{Sources.Safe(node.Id)}");
            for (var i = 0; i < node.Columns.Count; i++)
            {
                var col = node.Columns[i];
                var path = $"{fileStem}__{i}_{Sources.Safe(col.Name)}";
                if (col.IsIdLike)
                {
                    Staging.WriteCounts(path + ".ids", sheet.Columns[i].SortedIds());
                    node.Staged[col.Name] = path + ".ids";
                }
                if (split && col.NonNull > 0)
                {
                    Staging.WriteHashes(path + ".hash", sheet.Columns[i].SortedHashes());
                    node.Hashed[col.Name] = path + ".hash";
                }
            }
        }
        return nodes;
    }

    /// <summary>
    /// A readable stream for one member. A CSV inside a zip is read straight out of
    /// it; a workbook is a zip itself and needs seeking, so it is copied out first.
    /// </summary>
    static Stream Open(Member member, string extension, string staging)
    {
        if (member.Name is null)
            return File.OpenRead(member.Path);
        var zip = ZipFile.OpenRead(member.Path);
        try
        {
            var entry = zip.GetEntry(member.Name) ?? throw new FileNotFoundException($"{member.Name} is not in {member.Path}");
            if (extension is ".csv" or ".tsv")
                return new OwnedStream(entry.Open(), zip);

            var tmp = Path.Combine(staging, "tmp");
            Directory.CreateDirectory(tmp);
            var local = new FileStream(Path.Combine(tmp, $"{Guid.NewGuid():N}{extension}"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
            try
            {
                using (var src = entry.Open())
                    src.CopyTo(local, 1 << 20);
                local.Position = 0;
            }
            catch
            {
                local.Dispose(); // an open handle would keep the staging folder from being removed
                throw;
            }
            zip.Dispose();
            return local;
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>A zip entry's stream that also closes the zip it came from.</summary>
    sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>A stream that reports how many bytes are read from it, for progress.</summary>
    sealed class CountingStream(Stream inner, Action<int> onRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        int Count(int n)
        {
            onRead(n);
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
