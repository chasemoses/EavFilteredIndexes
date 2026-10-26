namespace EavFilteredIndexes;

public enum IndexStrategy { Standard, Filtered }

/// <summary>A typed value column on the EAV table. Each row fills at most one of them.</summary>
public sealed record ValueColumn(string Name, string SqlType);

public static class Schema
{
    public const string Table = "dbo.ItemValues";

    /// <summary>Free text is the common case and is not indexed.</summary>
    public static readonly ValueColumn Text = new("TextValue", "nvarchar(400)");

    /// <summary>
    /// The sparse typed columns. Each one is searchable, so each one carries a nonclustered index.
    /// Most rows leave all seven null.
    /// </summary>
    public static readonly IReadOnlyList<ValueColumn> Indexed =
    [
        new("NumberValue", "decimal(18,4)"),
        new("DateValue", "date"),
        new("FlagValue", "bit"),
        new("LookupId", "int"),
        new("ReferenceItemId", "int"),
        new("UserId", "int"),
        new("CodeValue", "varchar(20)"),
    ];

    public static IEnumerable<ValueColumn> AllValueColumns => Indexed.Prepend(Text);

    public static string CreateTable()
    {
        var columns = string.Join(",\n    ", AllValueColumns.Select(c => $"{c.Name} {c.SqlType} NULL"));
        return $"""
            CREATE TABLE {Table} (
                Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ItemValues PRIMARY KEY CLUSTERED,
                ItemId int NOT NULL,
                AttributeId int NOT NULL,
                {columns}
            );
            """;
    }

    public static string IndexName(ValueColumn c) => $"IX_ItemValues_{c.Name}";

    /// <summary>
    /// The only difference between the strategies is the WHERE clause. A filtered index stores
    /// an entry only for rows that satisfy it, so a row with a null value costs that index nothing.
    /// </summary>
    public static IEnumerable<string> CreateIndexes(IndexStrategy strategy) =>
        Indexed.Select(c =>
            $"CREATE NONCLUSTERED INDEX {IndexName(c)} ON {Table} (AttributeId, {c.Name}) INCLUDE (ItemId)"
            + (strategy == IndexStrategy.Filtered ? $" WHERE {c.Name} IS NOT NULL" : "")
            + ";");
}
