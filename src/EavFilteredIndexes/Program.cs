using System.Data;
using System.Globalization;
using System.Text;
using EavFilteredIndexes;
using Microsoft.Data.SqlClient;

var items = IntArg("--items", 50_000);
var runs = IntArg("--runs", 3);
var server = StringArg("--server", @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true");
const string database = "EavIndexDemo";

var generator = new DataGenerator(seed: 42);
var bench = new Benchmark(server, database);

Console.WriteLine($"Creating database {database}...");
await bench.CreateDatabaseAsync();

var results = new List<ImportResult>();
var plans = new Dictionary<IndexStrategy, List<PlanCheck>>();
foreach (var strategy in new[] { IndexStrategy.Standard, IndexStrategy.Filtered })
{
    Console.WriteLine($"Importing {items:N0} items x {DataGenerator.AttributesPerItem} attributes with {strategy} indexes ({runs} runs)...");
    var result = await bench.RunImportAsync(strategy, generator, items, runs);
    results.Add(result);
    plans[strategy] = await CheckPlansAsync(bench);
}

var legacyInsertError = await bench.InsertWithLegacySetOptionsAsync();

var report = BuildReport(results, plans, legacyInsertError, items, runs);
Console.WriteLine();
Console.WriteLine(report);
Directory.CreateDirectory("results");
await File.WriteAllTextAsync(Path.Combine("results", "latest.md"), report);
Console.WriteLine("Saved to results/latest.md");

static async Task<List<PlanCheck>> CheckPlansAsync(Benchmark bench) =>
[
    await bench.CheckPlanAsync(
        "Equality lookup on a typed value (parameterized)",
        "SELECT ItemId FROM dbo.ItemValues WHERE AttributeId = @a AND NumberValue = @v;",
        new SqlParameter("@a", 165), new SqlParameter("@v", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = 1234.5m }),
    await bench.CheckPlanAsync(
        "Range lookup on a date value (parameterized)",
        "SELECT ItemId FROM dbo.ItemValues WHERE AttributeId = @a AND DateValue BETWEEN @from AND @to;",
        new SqlParameter("@a", 175), new SqlParameter("@from", SqlDbType.Date) { Value = new DateTime(2020, 1, 1) },
        new SqlParameter("@to", SqlDbType.Date) { Value = new DateTime(2020, 3, 31) }),
    await bench.CheckPlanAsync(
        "Searching for a missing value (IS NULL)",
        "SELECT ItemId FROM dbo.ItemValues WHERE AttributeId = @a AND LookupId IS NULL;",
        new SqlParameter("@a", 190)),
];

static string BuildReport(List<ImportResult> results, Dictionary<IndexStrategy, List<PlanCheck>> plans, string? legacyError, int items, int runs)
{
    var std = results.Single(r => r.Strategy == IndexStrategy.Standard);
    var flt = results.Single(r => r.Strategy == IndexStrategy.Filtered);
    var sb = new StringBuilder();
    sb.AppendLine("# Results");
    sb.AppendLine();
    sb.AppendLine($"{std.Rows:N0} rows ({items:N0} items x {DataGenerator.AttributesPerItem} attributes), seed 42, median of {runs} runs, {DateTime.Now:yyyy-MM-dd}.");
    sb.AppendLine();
    sb.AppendLine("## Import cost");
    sb.AppendLine();
    sb.AppendLine("| | Standard | Filtered | Change |");
    sb.AppendLine("|---|---:|---:|---:|");
    sb.AppendLine($"| Nonclustered index entries | {std.NonclusteredEntries:N0} | {flt.NonclusteredEntries:N0} | {Pct(std.NonclusteredEntries, flt.NonclusteredEntries)} |");
    sb.AppendLine($"| Nonclustered index pages | {std.NonclusteredPages:N0} | {flt.NonclusteredPages:N0} | {Pct(std.NonclusteredPages, flt.NonclusteredPages)} |");
    sb.AppendLine($"| Entries maintained per inserted row | {(double)std.NonclusteredEntries / std.Rows:F2} | {(double)flt.NonclusteredEntries / flt.Rows:F2} | |");
    sb.AppendLine($"| Import time (s, median) | {std.MedianSeconds:F2} | {flt.MedianSeconds:F2} | {Pct(std.MedianSeconds, flt.MedianSeconds)} |");
    sb.AppendLine();
    sb.AppendLine("## Entries per index");
    sb.AppendLine();
    sb.AppendLine("| Index | Standard | Filtered |");
    sb.AppendLine("|---|---:|---:|");
    foreach (var s in std.Indexes)
    {
        var f = flt.Indexes.Single(i => i.Name == s.Name);
        sb.AppendLine($"| {s.Name} | {s.Rows:N0} | {f.Rows:N0} |");
    }
    sb.AppendLine();
    sb.AppendLine("## Do queries still use the indexes?");
    sb.AppendLine();
    sb.AppendLine("| Query | Standard plan | Filtered plan |");
    sb.AppendLine("|---|---|---|");
    for (var i = 0; i < plans[IndexStrategy.Standard].Count; i++)
    {
        var s = plans[IndexStrategy.Standard][i];
        var f = plans[IndexStrategy.Filtered][i];
        sb.AppendLine($"| {s.Description} | {Describe(s)} | {Describe(f)} |");
    }
    sb.AppendLine();
    sb.AppendLine("## Insert from a session with QUOTED_IDENTIFIER OFF");
    sb.AppendLine();
    sb.AppendLine(legacyError is null ? "Succeeded." : $"Failed, as expected for a table with filtered indexes: `{legacyError}`");
    return sb.ToString();
}

static string Describe(PlanCheck p) =>
    (p.Seek ? "Seek on " : "Scan of ") + string.Join(", ", p.IndexesUsed.Select(i => $"`{i}`"));

static string Pct(double before, double after) =>
    (after - before) / before * 100 is var d ? d.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + "%" : "";

int IntArg(string name, int fallback) => StringArg(name, null) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

string StringArg(string name, string? fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback!;
}
