// Command line entry point.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DepGraph;

public static partial class Cli
{
    /// <summary>Lists in the console log are cut to this many lines.</summary>
    const int Show = 20;

    sealed class Options
    {
        public readonly Argument<string[]> Source = new("source")
        {
            Description = "extract .zip (which may hold more zips), a directory of sheets or zips, or several zips of one export",
            Arity = ArgumentArity.OneOrMore,
        };
        public readonly Option<string?> Out = new("--out", "-o")
        {
            Description = "JSON graph path (default: <source name>.json in the current directory)",
            HelpName = "PATH",
        };
        public readonly Option<string?> Mermaid = new("--mermaid")
        {
            Description = "Mermaid ER diagram path (default: beside the JSON)",
            HelpName = "PATH",
        };
        public readonly Option<string?> Dot = new("--dot")
        {
            Description = "also write a Graphviz DOT diagram here (off unless asked for)",
            HelpName = "PATH",
        };
        public readonly Option<string?> Docs = new("--docs")
        {
            Description = "folder for the Markdown docs, one page per entity (default: <JSON name>-docs beside the JSON)",
            HelpName = "DIR",
        };
        public readonly Option<bool> NoDocs = new("--no-docs") { Description = "do not write the docs folder" };
        public readonly Option<bool> JsonOnly = new("--json-only") { Description = "write only the JSON: no diagrams, no docs" };
        public readonly Option<bool> IncludeEmpty = new("--include-empty")
        {
            Description = "keep sheets with no rows as graph nodes. Off by default: they carry no columns, key or "
                + "references, so nothing is built from them.",
        };
        // Now the default; still accepted so old commands work.
        public readonly Option<bool> SkipEmpty = new("--skip-empty") { Hidden = true };
        public readonly Option<string?> Include = new("--include")
        {
            Description = "a text file listing the sheets to read, one per line ('Account', 'Account.xlsx', blank "
                + "lines and # comments allowed). Everything else is skipped without being read.",
            HelpName = "FILE",
        };
        public readonly Option<string> Profile = new("--profile")
        {
            Description = "export dialect",
            DefaultValueFactory = _ => SalesforceProfile.ProfileName,
        };
        public readonly Option<int?> Workers = new("--workers", "-j") { Description = "parallel readers (default: one per CPU)", HelpName = "N" };
        public readonly Option<double?> Memory = new("--memory")
        {
            Description = "memory the readers may use between them, in GB (default: 80% of the memory free at the "
                + "start). A file starts only once it fits beside the files already being read.",
            HelpName = "GB",
        };
        public readonly Option<int?> MaxRows = new("--max-rows")
        {
            Description = "read at most N rows per file (faster, approximate stats)",
            HelpName = "N",
        };
        public readonly Option<int> Sample = new("--sample")
        {
            Description = "values sampled to decide if a column holds identifiers",
            HelpName = "N",
            DefaultValueFactory = _ => 500,
        };
        public readonly Option<double> MinConfidence = new("--min-confidence")
        {
            Description = "drop inferred edges below this confidence",
            HelpName = "F",
            DefaultValueFactory = _ => 0.5,
        };
        public readonly Option<double> OverlapThreshold = new("--overlap-threshold")
        {
            Description = "when name and prefix give no candidate, accept a target whose key covers at least this "
                + "share of the values; 0 disables",
            HelpName = "F",
            DefaultValueFactory = _ => 0.8,
        };
        public readonly Option<string?> Staging = new("--staging")
        {
            Description = "staging dir, which also receives unpacked inner zips (default: a temp dir)",
            HelpName = "DIR",
        };
        public readonly Option<bool> KeepStaging = new("--keep-staging") { Description = "keep staged columns for debugging" };
        public readonly Option<int> Indent = new("--indent")
        {
            Description = "JSON indent; 0 for compact",
            HelpName = "N",
            DefaultValueFactory = _ => 1,
        };
        public readonly Option<bool> Quiet = new("--quiet", "-q") { Description = "print nothing but errors" };

        public RootCommand Command()
        {
            Profile.AcceptOnlyFromAmong([.. Profiles.Names]);
            Memory.Validators.Add(r =>
            {
                if (r.GetValueOrDefault<double?>() is <= 0)
                    r.AddError("--memory must be more than 0");
            });
            Indent.Validators.Add(r =>
            {
                if (r.GetValueOrDefault<int>() is < 0 or > 64)
                    r.AddError("--indent must be between 0 and 64");
            });
            return new RootCommand(
                "Build an AI-agent-readable dependency graph, and a folder of Markdown docs per entity, from a zip "
                + "(or directory) of tabular extracts. Zips inside zips are opened, and an object split across "
                + "several zips is merged back into one.")
            {
                Source, Out, Mermaid, Dot, Docs, NoDocs, JsonOnly, IncludeEmpty, SkipEmpty, Include,
                Profile, Workers, Memory, MaxRows, Sample, MinConfidence, OverlapThreshold, Staging, KeepStaging, Indent,
                Quiet,
            };
        }
    }

    /// <summary>Run the tool; returns the process exit code. Progress and errors go to <paramref name="stderr"/>.</summary>
    public static int Run(string[] args, TextWriter stderr, TextWriter? stdout = null)
    {
        var o = new Options();
        var command = o.Command();
        command.SetAction(parsed =>
        {
            try
            {
                return Build(parsed, o, stderr);
            }
            catch (Exception exc) when (exc is UserError or IOException or UnauthorizedAccessException)
            {
                // A missing folder, a file in use, -o naming a directory: say so in one line.
                stderr.WriteLine($"depgraph: {exc.Message}");
                return 1;
            }
        });

        var result = command.Parse(args);
        if (result.Action is ParseErrorAction)
        {
            foreach (var error in result.Errors)
                stderr.WriteLine($"depgraph: {error.Message}");
            stderr.WriteLine("Run 'depgraph --help' for usage.");
            return 2;
        }
        // --help and --version are actions too; a crash should surface, not be summarised.
        return result.Invoke(new InvocationConfiguration
        {
            Output = stdout ?? Console.Out,
            Error = stderr,
            EnableDefaultExceptionHandler = false,
        });
    }

    static int Build(ParseResult p, Options o, TextWriter stderr)
    {
        // "export/" names the folder "export", in the log and in the docs title.
        var sources = p.GetValue(o.Source)!.Select(Unquote).Select(s => Path.TrimEndingDirectorySeparator(s)).ToList();
        var include = p.GetValue(o.Include) is { } inc ? Unquote(inc) : null;
        foreach (var src in sources)
        {
            if (!Exists(src))
            {
                stderr.WriteLine($"depgraph: {src} does not exist");
                if (src.Contains('"'))
                    stderr.WriteLine("  (on Windows, a quoted path must not end in a backslash: write \"C:\\My dir\", not \"C:\\My dir\\\")");
                return 2;
            }
        }

        // By default the outputs are named after the source, so a bare
        // `depgraph extract.zip` writes extract.json, extract.mmd and extract-docs/.
        var output = p.GetValue(o.Out) ?? $"{DefaultStem(sources)}.json";
        var mermaid = p.GetValue(o.Mermaid) ?? Path.ChangeExtension(output, ".mmd");
        var dot = p.GetValue(o.Dot);
        var docsDir = p.GetValue(o.Docs)
            ?? Path.Combine(Path.GetDirectoryName(output) ?? "", Path.GetFileNameWithoutExtension(output) + "-docs");
        if (p.GetValue(o.JsonOnly))
            mermaid = dot = docsDir = null;
        if (p.GetValue(o.NoDocs))
            docsDir = null;

        var includeEmpty = p.GetValue(o.IncludeEmpty);
        var minConfidence = p.GetValue(o.MinConfidence);
        var maxRows = p.GetValue(o.MaxRows);
        var profile = Profiles.Get(p.GetValue(o.Profile)!);
        var quiet = p.GetValue(o.Quiet);
        void Log(string line)
        {
            if (!quiet)
                stderr.WriteLine(line);
        }

        var userStaging = p.GetValue(o.Staging);
        var staging = userStaging ?? Directory.CreateTempSubdirectory("depgraph-staging-").FullName;
        var started = Stopwatch.StartNew();

        Graph graph;
        List<string> written;
        int pages = 0;
        try
        {
            var names = string.Join(", ", sources);
            Log($"reading {names} (profile: {profile.Name})");

            var discovered = Sources.List(sources, Path.Combine(staging, "archives"), Log, profile.SkippedFolders);
            var archives = discovered.Where(m => m.Name is not null).Select(m => m.Path).Distinct().Count();
            var members = discovered;
            List<string> unmatched = [];
            if (include is not null)
            {
                if (!File.Exists(include))
                {
                    stderr.WriteLine($"depgraph: {include} does not exist");
                    return 2;
                }
                var wanted = Sources.ReadNameFilter(include);
                (members, unmatched) = Sources.Filter(discovered, wanted);
                Log($"include list: {members.Count} of {discovered.Count} files selected");
                if (unmatched.Count > 0)
                    Log($"  warning: {unmatched.Count} name(s) matched nothing: "
                        + string.Join(", ", unmatched.Take(10)) + (unmatched.Count > 10 ? " ..." : ""));
                if (members.Count == 0)
                    throw new UserError($"none of the {wanted.Count} names in {include} matched a sheet in {names}");
            }
            if (members.Count == 0)
                throw new UserError($"no tabular files found in {names}");

            var (groups, skipped) = Sources.PlanParts(members);
            var split = groups.Where(g => g.Value.Count > 1).ToList();
            var copies = skipped.Values.Sum(v => v.Count);
            LogPlan(Log, members, names, split.Select(g => g.Value).ToList(), skipped);

            long budget;
            if (p.GetValue(o.Memory) is { } gb)
            {
                budget = (long)(gb * 1e9);
                Log($"memory budget: {Bytes(budget)}; a file starts once it fits beside the files being read");
            }
            else
            {
                (budget, var free) = Ingest.DefaultMemoryBudget();
                Log($"memory budget: {Bytes(budget)} (80% of the {Bytes(free)} free); a file starts once it fits "
                    + "beside the files being read. Use --memory to change it.");
            }

            var nodes = Ingest.Run(sources, staging, profile.Name, new IngestOptions
            {
                Workers = p.GetValue(o.Workers),
                MemoryBudget = budget,
                MaxRows = maxRows,
                SampleN = p.GetValue(o.Sample),
                Progress = r => Log(ProgressLine(r)),
                Members = members,
                KeepEmpty = includeEmpty,
            });
            var readSeconds = started.Elapsed.TotalSeconds;
            var merged = nodes.Where(n => n.Parts.Count > 1).ToList();
            if (merged.Count > 0)
            {
                Log($"merged {merged.Count} split objects:");
                foreach (var n in merged.Take(Show))
                    Log($"  {n.Id}: {n.Rows:N0} rows from {n.Parts.Count} files ({string.Join(" + ", n.PartRows.Select(r => r.ToString("N0")))})");
                if (merged.Count > Show)
                    Log($"  ... and {merged.Count - Show} more");
            }

            var unreadable = nodes.Where(n => n.Rows == 0 && n.Warnings.Any(w => w.Contains("unreadable"))).ToList();
            foreach (var n in unreadable)
                Log($"  warning: could not read {n.Source}: {n.Warnings[^1]}");
            var emptyNames = nodes.Where(n => n.Rows == 0 && !unreadable.Contains(n)).Select(n => n.Id).ToList();
            if (!includeEmpty)
            {
                nodes = nodes.Where(n => n.Rows > 0).ToList();
                if (emptyNames.Count > 0)
                    Log($"excluded {emptyNames.Count} sheets with no rows");
            }
            if (nodes.Count == 0)
                throw new UserError("every sheet was empty; nothing to graph");

            Log("inferring relationships");
            graph = Infer.Run(nodes, profile, minConfidence, overlapThreshold: p.GetValue(o.OverlapThreshold));
            graph.Source = names;
            graph.Stats = new GraphStats
            {
                Sheets = nodes.Count,
                Rows = nodes.Sum(n => n.Rows),
                Columns = nodes.Sum(n => n.Columns.Count),
                Edges = graph.Edges.Count,
                Unresolved = graph.Unresolved.Count,
                Files = members.Count - copies,
                SplitObjects = nodes.Count(n => n.Parts.Count > 1),
                ReadSeconds = Math.Round(readSeconds, 2),
                TotalSeconds = Math.Round(started.Elapsed.TotalSeconds, 2),
                Sampled = maxRows is not null,
                IncludeList = include,
                UnmatchedIncludeNames = unmatched,
            };

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            void WriteText(string path, string text)
            {
                if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, text, utf8);
            }
            WriteText(output, Render.ToJson(graph, p.GetValue(o.Indent)));
            written = [output];
            if (mermaid is not null)
            {
                WriteText(mermaid, Render.ToMermaid(graph, minConfidence));
                written.Add(mermaid);
            }
            if (dot is not null)
            {
                WriteText(dot, Render.ToDot(graph, minConfidence));
                written.Add(dot);
            }
            if (docsDir is not null)
            {
                pages = Docs.Write(graph, docsDir, new DocsContext
                {
                    Files = members.Count - copies,
                    Bytes = groups.Values.SelectMany(parts => parts).Sum(m => m.Size),
                    Archives = archives,
                    Copies = copies,
                    ExcludedEmpty = includeEmpty ? 0 : emptyNames.Count,
                    Unreadable = unreadable.Select(n => n.Source).ToList(),
                }).Count;
            }
        }
        finally
        {
            if (userStaging is null && !p.GetValue(o.KeepStaging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else if (userStaging is null)
            {
                Log($"kept staging in {staging}");
            }
        }

        var s = graph.Stats;
        Log($"\n{s.Sheets} sheets / {s.Rows:N0} rows / {s.Columns} columns in {s.TotalSeconds}s");
        Log($"{s.Edges} relationships, {s.Unresolved} pointing outside the extract");
        if (graph.Cycles.Count > 0)
            Log($"cycles: {string.Join(", ", graph.Cycles.Select(c => string.Join(" <-> ", c)))}");
        foreach (var path in written)
            Log($"wrote {path} ({new FileInfo(path).Length:N0} bytes)");
        if (docsDir is not null)
            Log($"wrote {docsDir}{Path.DirectorySeparatorChar} ({pages} pages; start at {Path.Combine(docsDir, "README.md")})");
        return 0;
    }

    /// <summary>
    /// A finished file, with how far the whole read has got. The share is of the
    /// data, not the files: the largest files are read first, so the first few
    /// files can be most of the work.
    /// </summary>
    internal static string ProgressLine(IngestProgress r)
    {
        var share = r.TotalBytes > 0 ? r.BytesRead / (double)r.TotalBytes : 1;
        if (r.Message is not null)
            return $"  [{r.Done}/{r.Total}, {share * 100:F0}% of data] {r.Message}";
        // Reading is about as fast per byte for small files as for large, so the
        // rate so far gives a fair estimate of what is left.
        var line = $"  ... {share * 100:F0}% of data read ({Bytes(r.BytesRead)} of {Bytes(r.TotalBytes)}) in {Duration(r.Elapsed)}, "
            + $"{r.Reading} file{(r.Reading == 1 ? "" : "s")} being read";
        if (r.BytesRead > 0 && share < 1)
            line += $", about {Duration(r.Elapsed * ((1 - share) / share))} left at this rate";
        return line;
    }

    static string Bytes(long n) => n >= 1e9 ? $"{n / 1e9:N1} GB" : $"{n / 1e6:N1} MB";

    static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:D2}s"
        : $"{t.Seconds}s";

    /// <summary>Say what was found where, before the slow part starts.</summary>
    static void LogPlan(Action<string> log, List<Member> members, string names, List<List<Member>> split, Dictionary<string, List<string>> skipped)
    {
        var where = new OrderedDictionary<string, List<Member>>(StringComparer.Ordinal);
        foreach (var m in members)
        {
            var c = Sources.Container(m) is { Length: > 0 } zip ? zip : names;
            if (!where.TryGetValue(c, out var found))
                where[c] = found = [];
            found.Add(m);
        }
        log($"found {members.Count} files" + (where.Count > 1 ? ":" : ""));
        if (where.Count > 1)
        {
            foreach (var (name, found) in where)
                log($"  {name}: {found.Count} files, {found.Sum(m => m.Size) / 1e6:N1} MB");
        }
        if (split.Count > 0)
        {
            log($"{split.Count} objects are split across several files; their {split.Sum(v => v.Count)} parts are merged:");
            foreach (var parts in split.Take(Show))
            {
                var places = parts.Select(p => Sources.Container(p) is { Length: > 0 } zip ? zip : p.Label);
                log($"  {parts[0].BaseName} in {string.Join(", ", places)}");
            }
            if (split.Count > Show)
                log($"  ... and {split.Count - Show} more");
        }
        var copies = skipped.Values.SelectMany(v => v).ToList();
        if (copies.Count > 0)
        {
            log($"skipping {copies.Count} file(s) that are identical copies of another part:");
            foreach (var label in copies.Take(Show))
                log($"  {label}");
            if (copies.Count > Show)
                log($"  ... and {copies.Count - Show} more");
        }
    }

    /// <summary>
    /// Output name for the sources: the source's own name, or for several parts of
    /// one export ("WE_00D_1.ZIP", "WE_00D_2.ZIP") the name they share ("WE_00D").
    /// </summary>
    public static string DefaultStem(IReadOnlyList<string> sources)
    {
        var stems = sources.Select(s =>
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(s));
            var stem = Path.GetFileNameWithoutExtension(name);
            return stem.Length > 0 ? stem : name;
        }).ToList();
        if (stems.Count == 1)
            return stems[0];
        var common = stems.Aggregate((a, b) => a[..a.Zip(b).TakeWhile(x => x.First == x.Second).Count()]);
        common = TrailingNumber().Replace(common, "");
        return common.Length > 0 ? common : "extract";
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Undo a Windows quoting trap: "C:\My dir\" reaches the program as
    /// <c>C:\My dir"</c>, because the runtime reads the final \" as an escaped
    /// quote. Tab-completion in PowerShell adds exactly that trailing backslash.
    /// </summary>
    static string Unquote(string path)
    {
        if (path.EndsWith('"') && !Exists(path))
        {
            var fixedPath = path.TrimEnd('"').TrimEnd();
            if (Exists(fixedPath))
                return fixedPath;
        }
        return path;
    }

    [GeneratedRegex(@"[\s._-]*\d*[\s._-]*$")]
    private static partial Regex TrailingNumber();
}
