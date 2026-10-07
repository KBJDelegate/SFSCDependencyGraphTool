// Finding the files.
//
// An extract may be a zip of zips, a directory of zips, or several zips, and the
// same object can be split across them (Account.csv in both WE_1.zip and
// WE_2.zip). This lists every tabular file wherever it is, and groups the files
// by the object they hold, so each part can still be read on its own.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace DepGraph;

public static partial class Sources
{
    public static readonly string[] Tabular = [".xlsx", ".xlsm", ".xlsb", ".csv", ".tsv"];
    public const string Archive = ".zip";

    /// <summary>Zips inside zips are unpacked this many levels deep and no further.</summary>
    public const int MaxNesting = 4;

    public static bool IsTabular(string name) =>
        Tabular.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>The zip a member was found in, as named in its label ("" if none).</summary>
    public static string Container(Member m)
    {
        var bang = m.Label.LastIndexOf('!');
        return bang >= 0 ? m.Label[..bang] : "";
    }

    /// <summary>A name made safe to use in a file name.</summary>
    public static string Safe(string name) => Unsafe().Replace(name, "_");

    static bool Hidden(string name)
    {
        name = name.Replace('\\', '/');
        return name.StartsWith("__MACOSX", StringComparison.Ordinal)
            || name.StartsWith('.')
            || name.Contains("/.", StringComparison.Ordinal)
            || name[(name.LastIndexOf('/') + 1)..].StartsWith("~$", StringComparison.Ordinal);
    }

    // --- finding the files ---------------------------------------------------

    /// <summary>
    /// Every tabular file in the given zips and directories, nested zips included.
    /// <paramref name="scratch"/> receives the unpacked copies of zips found inside
    /// zips; it is only touched when there are any. Nothing under a folder named in
    /// <paramref name="skipFolders"/> is listed (see <see cref="Profile.SkippedFolders"/>).
    /// </summary>
    public static List<Member> List(IReadOnlyList<string> sources, string? scratch, Action<string>? log = null,
        IReadOnlySet<string>? skipFolders = null)
    {
        log ??= _ => { };
        skipFolders ??= new HashSet<string>();
        // With several sources, prefix labels so Account.csv from each stays apart.
        var several = sources.Count > 1;

        var output = new List<Member>();
        foreach (var src in sources)
        {
            var name = FileName(src);
            var prefix = several ? $"{name}!" : "";
            if (Directory.Exists(src))
            {
                var dprefix = several ? $"{name}/" : "";
                var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                var files = Directory.EnumerateFiles(src, "*", walk)
                    .Select(f => (Full: f, Rel: Path.GetRelativePath(src, f).Replace('\\', '/')))
                    .OrderBy(f => f.Rel, StringComparer.Ordinal);
                var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var (full, rel) in files)
                {
                    if (Hidden(rel))
                        continue;
                    if (SkippedFolder(rel, skipFolders) is { } folder)
                    {
                        skipped[folder] = skipped.GetValueOrDefault(folder) + 1;
                        continue;
                    }
                    if (IsTabular(rel))
                    {
                        output.Add(new Member(full, null, dprefix + rel, new FileInfo(full).Length));
                    }
                    else if (rel.EndsWith(Archive, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            output.AddRange(WalkZip(full, $"{dprefix}{rel}!", scratch, 1, skipFolders, log));
                        }
                        catch (InvalidDataException)
                        {
                            log($"  warning: {dprefix}{rel} is not a readable zip, skipped");
                        }
                    }
                }
                LogSkipped(skipped, dprefix, log);
            }
            else if (IsTabular(name)) // before the zip check: an xlsx is a zip too
            {
                output.Add(new Member(src, null, name, new FileInfo(src).Length));
            }
            else if (name.EndsWith(Archive, StringComparison.OrdinalIgnoreCase) || IsZip(src))
            {
                try
                {
                    output.AddRange(WalkZip(src, prefix, scratch, 0, skipFolders, log));
                }
                catch (InvalidDataException exc)
                {
                    throw new UserError($"{src} is not a readable zip: {exc.Message}");
                }
            }
        }
        return output;
    }

    static string FileName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    static bool IsZip(string path)
    {
        Span<byte> magic = stackalloc byte[4];
        using var f = File.OpenRead(path);
        return f.Read(magic) == 4 && magic.SequenceEqual("PK\x03\x04"u8);
    }

    /// <summary>
    /// The folder <paramref name="name"/> lies in, up to and including the first
    /// part named in <paramref name="skip"/>; null when it is not under one.
    /// </summary>
    static string? SkippedFolder(string name, IReadOnlySet<string> skip)
    {
        if (skip.Count == 0)
            return null;
        var parts = name.Replace('\\', '/').Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (skip.Contains(parts[i]))
                return string.Join('/', parts[..(i + 1)]);
        }
        return null;
    }

    static void LogSkipped(SortedDictionary<string, int> skipped, string prefix, Action<string> log)
    {
        foreach (var (folder, count) in skipped)
            log($"  skipped {count:N0} file{(count == 1 ? "" : "s")} under {prefix}{folder}/ (attachments, not data)");
    }

    /// <summary>Tabular members of a zip, unpacking any zips inside it to <paramref name="scratch"/>.</summary>
    static List<Member> WalkZip(string path, string prefix, string? scratch, int depth, IReadOnlySet<string> skipFolders,
        Action<string> log)
    {
        var output = new List<Member>();
        var nested = new List<ZipArchiveEntry>();
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || Hidden(entry.FullName))
                continue;
            if (SkippedFolder(entry.FullName, skipFolders) is { } folder)
            {
                skipped[folder] = skipped.GetValueOrDefault(folder) + 1;
                continue;
            }
            if (IsTabular(entry.FullName))
            {
                output.Add(new Member(path, entry.FullName, prefix + entry.FullName, entry.Length, entry.Crc32));
            }
            else if (entry.FullName.EndsWith(Archive, StringComparison.OrdinalIgnoreCase))
            {
                if (depth >= MaxNesting)
                {
                    log($"  warning: not opening {prefix}{entry.FullName}: zips nested too deep");
                    continue;
                }
                nested.Add(entry);
            }
        }
        LogSkipped(skipped, prefix, log);
        if (nested.Count == 0)
            return output;
        if (scratch is null)
            throw new InvalidOperationException(
                $"{prefix}{nested[0].FullName} is a zip inside a zip; pass a scratch directory to unpack it into");

        // Opened in place, a compressed inner zip would be decompressed again for
        // every backwards seek, so each is copied to disk once. The copies run in
        // parallel, which matters when an export is a dozen zips of 512 MB.
        Directory.CreateDirectory(scratch);
        var names = nested.Select(e => e.FullName).ToArray();
        var unpacked = new (string? Local, double Seconds, string? Error)[names.Length];
        Parallel.For(0, names.Length, i => unpacked[i] = Unpack(path, names[i], scratch));
        for (var i = 0; i < names.Length; i++)
        {
            var mb = nested[i].Length / 1e6;
            log(unpacked[i].Error is { } error
                ? $"  warning: could not unpack {prefix}{names[i]}, skipped: {error}"
                : $"  unpacked {prefix}{names[i]} ({mb:N1} MB) in {unpacked[i].Seconds:F1}s");
        }

        for (var i = 0; i < names.Length; i++)
        {
            var label = prefix + names[i];
            if (unpacked[i].Local is not { } local)
                continue;
            try
            {
                output.AddRange(WalkZip(local, label + "!", scratch, depth + 1, skipFolders, log));
            }
            catch (InvalidDataException)
            {
                log($"  warning: {label} is not a readable zip, skipped");
            }
        }
        return output;
    }

    /// <summary>Copy one inner zip out to <paramref name="scratch"/>; each call opens the outer zip itself.</summary>
    static (string?, double, string?) Unpack(string path, string entryName, string scratch)
    {
        var started = Stopwatch.StartNew();
        var local = Path.Combine(scratch, $"{Guid.NewGuid():N}.zip");
        try
        {
            using var zip = ZipFile.OpenRead(path);
            using (var src = zip.GetEntry(entryName)!.Open())
            using (var dst = File.Create(local))
                src.CopyTo(dst, 1 << 20);
            return (local, started.Elapsed.TotalSeconds, null);
        }
        catch (InvalidDataException exc) // a damaged entry: skip it, like an unreadable inner zip
        {
            File.Delete(local);
            return (null, 0, exc.Message);
        }
    }

    // --- choosing the files ----------------------------------------------------

    /// <summary>
    /// Normalise a sheet name for matching: basename, no extension, lowercased.
    /// So "Account", "account.xlsx" and "exports/Account.XLSX" all match the zip
    /// member "Account.xlsx".
    /// </summary>
    public static string NormName(string raw)
    {
        var name = raw.Trim().Trim('"').Trim('\'').Replace('\\', '/');
        name = name[(name.LastIndexOf('!') + 1)..];
        name = name[(name.LastIndexOf('/') + 1)..].ToLowerInvariant();
        foreach (var ext in Tabular)
        {
            if (name.EndsWith(ext, StringComparison.Ordinal))
                return name[..^ext.Length];
        }
        return name;
    }

    public static string MemberKey(Member m) => NormName(m.BaseName);
    public static string MemberKey(string label) => NormName(label);

    /// <summary>Read a plain-text list of wanted sheets, one per line. Blank lines are skipped and '#' starts a comment.</summary>
    public static HashSet<string> ReadNameFilter(string path)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var hash = line.IndexOf('#');
            var name = NormName(hash >= 0 ? line[..hash] : line);
            if (name.Length > 0)
                wanted.Add(name);
        }
        return wanted;
    }

    /// <summary>(members to read, names in the list that matched nothing).</summary>
    public static (List<Member> Kept, List<string> Unmatched) Filter(IEnumerable<Member> members, IReadOnlySet<string> wanted)
    {
        var kept = members.Where(m => wanted.Contains(MemberKey(m))).ToList();
        var matched = kept.Select(MemberKey).ToHashSet(StringComparer.Ordinal);
        return (kept, wanted.Where(w => !matched.Contains(w)).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Group files by the object they hold, dropping byte-identical copies.
    /// Returns (object key -> parts to read, object key -> labels of skipped
    /// copies). Two files are the same copy when the zip directory gives them the
    /// same CRC-32 and size; reading both would double every count.
    /// </summary>
    public static (OrderedDictionary<string, List<Member>> Groups, Dictionary<string, List<string>> Skipped) PlanParts(
        IEnumerable<Member> members)
    {
        var groups = new OrderedDictionary<string, List<Member>>(StringComparer.Ordinal);
        var skipped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var m in members.OrderBy(m => m.Label, StringComparer.Ordinal))
        {
            var key = MemberKey(m);
            if (!groups.TryGetValue(key, out var parts))
                groups[key] = parts = [];
            if (m.Crc is not null && parts.Any(p => p.Crc == m.Crc && p.Size == m.Size))
            {
                if (!skipped.TryGetValue(key, out var copies))
                    skipped[key] = copies = [];
                copies.Add(m.Label);
                continue;
            }
            parts.Add(m);
        }
        return (groups, skipped);
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex Unsafe();
}
