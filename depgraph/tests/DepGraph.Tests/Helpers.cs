using System.Text.Json.Nodes;

namespace DepGraph.Tests;

/// <summary>Tests in this collection change the working directory, so they run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkingDirectory
{
    public const string Name = "working directory";

    /// <summary>Run <paramref name="body"/> with <paramref name="dir"/> as the working directory.</summary>
    public static void In(string dir, Action body)
    {
        var before = Environment.CurrentDirectory;
        Environment.CurrentDirectory = dir;
        try
        {
            body();
        }
        finally
        {
            Environment.CurrentDirectory = before;
        }
    }
}

internal static class Helpers
{
    /// <summary>The tool's exit code and everything it logged.</summary>
    public static (int Code, string Log) Depgraph(params string[] args)
    {
        var log = new StringWriter();
        var code = Cli.Run(args, log, TextWriter.Null);
        return (code, log.ToString());
    }

    /// <summary>Run with <c>-o dir/name.json -q</c> and return the parsed graph.</summary>
    public static JsonNode Run(IEnumerable<string> args, string dir, string name = "g")
    {
        var output = Path.Combine(dir, $"{name}.json");
        var (code, log) = Depgraph([.. args, "-o", output, "-q"]);
        Assert.True(code == 0, log);
        return Read(output);
    }

    public static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    public static IEnumerable<JsonNode> Each(this JsonNode g, string field) =>
        g[field]?.AsArray().Select(n => n!) ?? [];

    public static string Str(this JsonNode n, string field) => n[field]!.GetValue<string>();

    public static double Num(this JsonNode n, string field) => n[field]?.GetValue<double>() ?? 0;

    public static HashSet<(string, string)> Edges(JsonNode g) =>
        g.Each("edges").Select(e => (e.Str("from"), e.Str("to"))).ToHashSet();

    public static JsonNode Node(JsonNode g, string id) => g.Each("nodes").Single(n => n.Str("id") == id);

    public static HashSet<string> NodeIds(JsonNode g) => g.Each("nodes").Select(n => n.Str("id")).ToHashSet();

    public static List<string> Strings(this JsonNode? array) =>
        array?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];

    public static readonly HashSet<(string, string)> Expected =
    [
        ("Account.OwnerId", "User.Id"),
        ("Account.ParentId", "Account.Id"),
        ("Contact.AccountId", "Account.Id"),
        ("Contact.OwnerId", "User.Id"),
        ("Contact.CreatedById", "User.Id"),
        ("Contact.ReportsToId", "Contact.Id"),
        ("Opportunity.AccountId", "Account.Id"),
        ("Opportunity.OwnerId", "User.Id"),
        ("Task.OwnerId", "User.Id"),
        ("Task.WhatId", "Account.Id"),
        ("Task.WhatId", "Opportunity.Id"),
        ("Task.WhoId", "Contact.Id"),
        ("User.ManagerId", "User.Id"),
        ("Custom_Project__c.Account__c", "Account.Id"),
        ("Custom_Project__c.OwnerId", "User.Id"),
        ("Custom_Project__c.Primary_Contact__c", "Contact.Id"),
    ];
}
