// Pluggable profiles.
//
// A profile teaches the engine how a particular export dialect encodes identity
// and references. The engine itself knows nothing about Salesforce: swap the
// profile and the same machinery works on any zip of tabular files.
//
// Add a profile by subclassing Profile and registering it in Profiles.Registry;
// it then becomes available as --profile <name>.

using System.Text.RegularExpressions;

namespace DepGraph;

public static partial class Profiles
{
    public static readonly IReadOnlyDictionary<string, Func<Profile>> Registry =
        new Dictionary<string, Func<Profile>>
        {
            [GenericProfile.ProfileName] = () => new GenericProfile(),
            [SalesforceProfile.ProfileName] = () => new SalesforceProfile(),
        };

    public static IEnumerable<string> Names => Registry.Keys.Order(StringComparer.Ordinal);

    public static Profile Get(string name) =>
        Registry.TryGetValue(name, out var make)
            ? make()
            : throw new UserError($"unknown profile '{name}'; available: {string.Join(", ", Names)}");
}

/// <summary>Base profile: name-based matching plus value overlap, nothing more.</summary>
public abstract partial class Profile
{
    public abstract string Name { get; }

    // --- naming -----------------------------------------------------------

    /// <summary>Human-stable node id for one sheet of one file in the zip.</summary>
    public virtual string NodeName(string member, string sheet, int sheetCount)
    {
        var stem = member[(member.LastIndexOf('/') + 1)..];
        var dot = stem.LastIndexOf('.');
        if (dot >= 0)
            stem = stem[..dot];
        var s = sheet.ToLowerInvariant();
        return sheetCount == 1 || s == "sheet1" || s == "sheet" || s == stem.ToLowerInvariant()
            ? stem
            : $"{stem}.{sheet}";
    }

    // --- identity ---------------------------------------------------------

    /// <summary>Type token embedded in a value, or null if the dialect has none.</summary>
    public virtual string? IdToken(string value) => null;

    /// <summary>
    /// Whether <see cref="IdToken"/> reads a 3-character type token off 15- and
    /// 18-character ids, so token counts are worth taking over every row.
    /// </summary>
    public virtual bool EncodesTypeInValue => false;

    /// <summary>Do these sampled values look like opaque identifiers?</summary>
    public virtual bool LooksLikeId(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return false;
        var hits = values.Count(v => OpaqueId().IsMatch(v));
        return hits / (double)values.Count >= 0.95;
    }

    /// <summary>Lower is a better primary-key candidate; 99 means not a candidate.</summary>
    public virtual int KeyRank(string column, string node)
    {
        var c = column.ToLowerInvariant();
        var n = node.ToLowerInvariant().Replace(' ', '_');
        if (c is "id" or "pk" or "key" or "uuid" or "guid")
            return 0;
        if (c == $"{n}_id" || c == $"{n}id" || c == $"{n}_key")
            return 1;
        return 99;
    }

    // --- references -------------------------------------------------------

    /// <summary>Strip a foreign-key suffix to the implied target name, else null.</summary>
    public virtual string? ReferenceBase(string column)
    {
        var m = IdSuffix().Match(column);
        return m.Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : null;
    }

    /// <summary>Candidate node names a reference base could mean.</summary>
    public virtual HashSet<string> NameAliases(string @base)
    {
        var b = @base.ToLowerInvariant();
        var forms = new[] { b, b.Replace("_", ""), b.Replace('_', ' ') };
        var output = new HashSet<string>(forms, StringComparer.Ordinal);
        foreach (var v in forms.Distinct())
        {
            output.Add(v + "s");
            if (v.EndsWith('s'))
                output.Add(v[..^1]);
            if (v.EndsWith('y'))
                output.Add(v[..^1] + "ies");
        }
        return output;
    }

    /// <summary>Well-known references the dialect defines regardless of naming.</summary>
    public virtual IReadOnlyList<string> BuiltinTargets(string column) => [];

    public virtual bool IsPolymorphic(string column) => false;

    /// <summary>The standard object a type token belongs to, when the dialect knows it.</summary>
    public virtual string? NameForToken(string token) => null;

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{6,64}\z")]
    private static partial Regex OpaqueId();

    [GeneratedRegex(@"^(.+?)[_\- ]?id\z", RegexOptions.IgnoreCase)]
    private static partial Regex IdSuffix();
}

public sealed class GenericProfile : Profile
{
    public const string ProfileName = "generic";
    public override string Name => ProfileName;
}

/// <summary>
/// Salesforce report/data exports.
///
/// The key insight this profile exploits: the first three characters of every
/// Salesforce record Id are the object's key prefix. That makes reference
/// targets readable straight off the value, so the graph can be built without
/// joining every table against every other table.
/// </summary>
public sealed partial class SalesforceProfile : Profile
{
    public const string ProfileName = "salesforce";
    public override string Name => ProfileName;

    /// <summary>
    /// Standard objects whose key prefix is fixed, so a reference can be named
    /// even when the object itself was not included in the extract.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownPrefixes = new Dictionary<string, string>
    {
        ["001"] = "Account", ["003"] = "Contact", ["005"] = "User", ["006"] = "Opportunity",
        ["00Q"] = "Lead", ["500"] = "Case", ["00T"] = "Task", ["00U"] = "Event",
        ["0Q0"] = "Quote", ["801"] = "Order", ["701"] = "Campaign", ["00G"] = "Group",
        ["012"] = "RecordType", ["00e"] = "Profile", ["0Hn"] = "ContentDocument",
        ["068"] = "ContentVersion", ["015"] = "Document", ["00P"] = "Attachment",
    };

    /// <summary>Columns Salesforce always points at a fixed object.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Builtin = new Dictionary<string, string[]>
    {
        ["ownerid"] = ["User", "Group"],
        ["createdbyid"] = ["User"],
        ["lastmodifiedbyid"] = ["User"],
        ["recordtypeid"] = ["RecordType"],
        ["contactid"] = ["Contact"],
        ["accountid"] = ["Account"],
    };

    /// <summary>Columns that legitimately point at more than one object.</summary>
    public static readonly IReadOnlySet<string> Polymorphic = new HashSet<string>
    {
        "whatid", "whoid", "parentid", "ownerid", "relatedtoid",
        "targetobjectid", "linkedentityid", "subjectid",
    };

    public override bool EncodesTypeInValue => true;

    public override string? IdToken(string value) =>
        value.Length is 15 or 18 && value.All(char.IsAsciiLetterOrDigit) ? value[..3] : null;

    public override bool LooksLikeId(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return false;
        var hits = values.Count(v => IdToken(v) is not null);
        return hits / (double)values.Count >= 0.9;
    }

    public override int KeyRank(string column, string node)
    {
        var c = column.ToLowerInvariant();
        var n = node.ToLowerInvariant();
        if (c == "id")
            return 0;
        if (c == $"{n}id" || c == $"{n}__c")
            return 1;
        return 99;
    }

    public override string? ReferenceBase(string column)
    {
        // Custom lookups: My_Account__c -> My_Account
        var m = CustomSuffix().Match(column);
        if (m.Success)
            return m.Groups[1].Value;
        m = IdSuffix().Match(column);
        return m.Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : null;
    }

    public override HashSet<string> NameAliases(string @base)
    {
        var output = base.NameAliases(@base);
        output.Add($"{@base.ToLowerInvariant()}__c");
        output.Add(@base.ToLowerInvariant().Replace("_", ""));
        return output;
    }

    public override IReadOnlyList<string> BuiltinTargets(string column) =>
        Builtin.TryGetValue(column.ToLowerInvariant(), out var targets) ? targets : [];

    public override bool IsPolymorphic(string column) => Polymorphic.Contains(column.ToLowerInvariant());

    public override string? NameForToken(string token) => KnownPrefixes.GetValueOrDefault(token);

    [GeneratedRegex(@"^(.+?)__c\z", RegexOptions.IgnoreCase)]
    private static partial Regex CustomSuffix();

    [GeneratedRegex(@"^(.+?)id\z", RegexOptions.IgnoreCase)]
    private static partial Regex IdSuffix();
}

/// <summary>A problem with the input or the options: reported as one line, without a stack trace.</summary>
public sealed class UserError(string message) : Exception(message);
