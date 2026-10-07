// Pass 1: read every sheet once, profile its columns, stage only what pass 2 needs.
//
// Memory is bounded by (workers x one file), not by the size of the extract.
// That works because .xlsx caps a sheet at 1,048,576 rows and Salesforce caps
// each zip of a data export at ~512 MB, so a multi-GB extract is always *many*
// bounded files rather than one huge one. Only the identifier columns are
// staged, so the working set pass 2 joins over is tiny even when the extract is
// enormous.
//
// An object split across several files is still read one part at a time; the
// parts are combined afterwards from mergeable statistics, so splitting never
// costs memory.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;

namespace DepGraph;

public sealed record IngestOptions
{
    /// <summary>Parallel readers; default one per CPU.</summary>
    public int? Workers { get; init; }

    /// <summary>Read at most this many rows per sheet (approximate statistics).</summary>
    public int? MaxRows { get; init; }

    /// <summary>Values sampled to decide whether a column holds identifiers.</summary>
    public int SampleN { get; init; } = 500;

    /// <summary>Called once per file as it finishes: (done, total, message).</summary>
    public Action<int, int, string>? Progress { get; init; }

    /// <summary>The files to read (from <see cref="Sources.List"/>, perhaps filtered); default all of them.</summary>
    public IReadOnlyList<Member>? Members { get; init; }

    /// <summary>
    /// Whether the caller keeps sheets with no rows. Empty objects are always
    /// returned; this only lets the log say what becomes of an empty file.
    /// </summary>
    public bool KeepEmpty { get; init; }
}

public static class Ingest
{
    /// <summary>Read every object in <paramref name="sources"/> into a profiled Node.</summary>
    public static List<Node> Run(IReadOnlyList<string> sources, string staging, string profileName, IngestOptions? options = null)
    {
        options ??= new IngestOptions();
        var progress = options.Progress ?? ((_, _, _) => { });
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

        var results = new List<Node>[tasks.Count];
        var done = 0;
        var gate = new object();

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
            progress(++done, tasks.Count, $"{m.Label}: {outcome}");
        }

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers };
        // NoBuffering hands the tasks out in order, so the largest really do start first.
        var order = Partitioner.Create(Enumerable.Range(0, tasks.Count), EnumerablePartitionerOptions.NoBuffering);
        Parallel.ForEach(order, parallel, i =>
        {
            var (m, split) = tasks[i];
            var started = Stopwatch.StartNew();
            var nodes = IngestMember(m, i + 1, split, staging, profile, options.MaxRows, options.SampleN);
            results[i] = nodes;
            lock (gate)
                Finished(m, nodes, started.Elapsed.TotalSeconds);
        });

        return Combine.Parts(results.SelectMany(r => r).ToList(), staging, profile, skipped);
    }

    static List<Node> IngestMember(Member member, int partNo, bool split, string staging, Profile profile, int? maxRows, int sampleN)
    {
        var extension = Path.GetExtension(member.BaseName).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(member.BaseName);
        List<Sheet> sheets;
        try
        {
            using var stream = Open(member, extension, staging);
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
}
