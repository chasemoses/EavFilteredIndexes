using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace EavFilteredIndexes;

public sealed record IndexStats(string Name, long Rows, long Pages);

public sealed record ImportResult(IndexStrategy Strategy, long Rows, IReadOnlyList<double> RunSeconds, IReadOnlyList<IndexStats> Indexes)
{
    public double MedianSeconds => RunSeconds.Order().ElementAt(RunSeconds.Count / 2);
    public long NonclusteredEntries => Indexes.Where(i => i.Name != "PK_ItemValues").Sum(i => i.Rows);
    public long NonclusteredPages => Indexes.Where(i => i.Name != "PK_ItemValues").Sum(i => i.Pages);
}

public sealed record PlanCheck(string Description, string Sql, IReadOnlyList<string> IndexesUsed, bool Seek);

public sealed class Benchmark(string serverConnectionString, string database)
{
    private string Db => new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = database }.ConnectionString;

    public async Task CreateDatabaseAsync()
    {
        await using var c = new SqlConnection(serverConnectionString);
        await c.OpenAsync();
        await Exec(c, $"""
            IF DB_ID('{database}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{database}];
            END
            CREATE DATABASE [{database}];
            """);
        await Exec(c, $"ALTER DATABASE [{database}] SET RECOVERY SIMPLE;");
    }

    public async Task<ImportResult> RunImportAsync(IndexStrategy strategy, DataGenerator generator, int items, int runs)
    {
        var seconds = new List<double>();
        long rows = 0;
        for (var run = 0; run < runs; run++)
        {
            await RecreateTableAsync(strategy);
            (rows, var elapsed) = await ImportAsync(generator, items);
            seconds.Add(elapsed.TotalSeconds);
        }
        return new ImportResult(strategy, rows, seconds, await IndexStatsAsync());
    }

    private async Task RecreateTableAsync(IndexStrategy strategy)
    {
        await using var c = new SqlConnection(Db);
        await c.OpenAsync();
        await Exec(c, $"DROP TABLE IF EXISTS {Schema.Table};");
        await Exec(c, Schema.CreateTable());
        foreach (var ddl in Schema.CreateIndexes(strategy)) await Exec(c, ddl);
    }

    /// <summary>
    /// The indexes exist before the import, as they did in production: the import pays to
    /// maintain every index on every insert. Timing covers only the inserts, not data generation.
    /// </summary>
    private async Task<(long Rows, TimeSpan Elapsed)> ImportAsync(DataGenerator generator, int items)
    {
        await using var c = new SqlConnection(Db);
        await c.OpenAsync();
        long rows = 0;
        var timer = new Stopwatch();
        foreach (var batch in generator.Batches(items, batchSize: 5_000))
        {
            using var bulk = new SqlBulkCopy(c) { DestinationTableName = Schema.Table, BulkCopyTimeout = 0 };
            foreach (System.Data.DataColumn col in batch.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            timer.Start();
            await bulk.WriteToServerAsync(batch);
            timer.Stop();
            rows += batch.Rows.Count;
        }
        return (rows, timer.Elapsed);
    }

    public async Task<IReadOnlyList<IndexStats>> IndexStatsAsync()
    {
        await using var c = new SqlConnection(Db);
        await c.OpenAsync();
        await using var cmd = new SqlCommand($"""
            SELECT i.name, SUM(ps.row_count), SUM(ps.used_page_count)
            FROM sys.dm_db_partition_stats ps
            JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
            WHERE ps.object_id = OBJECT_ID('{Schema.Table}')
            GROUP BY i.name, i.index_id
            ORDER BY i.index_id;
            """, c);
        var result = new List<IndexStats>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add(new IndexStats(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return result;
    }

    /// <summary>
    /// A smaller index is only a win if the queries that need it can still use it.
    /// Run the query and read the actual plan SQL Server used.
    /// </summary>
    public async Task<PlanCheck> CheckPlanAsync(string description, string sql, params SqlParameter[] parameters)
    {
        await using var c = new SqlConnection(Db);
        await c.OpenAsync();
        await Exec(c, "SET STATISTICS XML ON;");
        await using var cmd = new SqlCommand(sql, c);
        cmd.Parameters.AddRange(parameters);
        // Parameterized commands go over RPC, and the plan may not be the first result set.
        string? xml = null;
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            do
            {
                while (xml is null && await r.ReadAsync())
                    if (r.GetValue(0) is string s && s.StartsWith("<ShowPlanXML")) xml = s;
            } while (xml is null && await r.NextResultAsync());
        }
        await Exec(c, "SET STATISTICS XML OFF;");
        if (xml is null) throw new InvalidOperationException($"No plan returned for: {sql}");

        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var plan = XDocument.Parse(xml);
        var used = plan.Descendants(ns + "Object")
            .Select(o => (string?)o.Attribute("Index"))
            .Where(i => i is not null)
            .Select(i => i!.Trim('[', ']'))
            .Distinct()
            .ToList();
        var seek = plan.Descendants(ns + "RelOp").Any(r => (string?)r.Attribute("PhysicalOp") == "Index Seek");
        return new PlanCheck(description, sql, used, seek);
    }

    /// <summary>
    /// Filtered indexes have a known failure mode: a session with the wrong SET options
    /// (for example QUOTED_IDENTIFIER OFF, common in old scripts) cannot insert into the table
    /// at all. Returns the error, or null if the insert succeeded.
    /// </summary>
    public async Task<string?> InsertWithLegacySetOptionsAsync()
    {
        await using var c = new SqlConnection(Db);
        await c.OpenAsync();
        await Exec(c, "SET QUOTED_IDENTIFIER OFF;");
        try
        {
            await Exec(c, $"INSERT INTO {Schema.Table} (ItemId, AttributeId, NumberValue) VALUES (1, 161, 1.5);");
            return null;
        }
        catch (SqlException ex)
        {
            return $"Error {ex.Number}: {ex.Message}";
        }
    }

    private static async Task Exec(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync();
    }
}
