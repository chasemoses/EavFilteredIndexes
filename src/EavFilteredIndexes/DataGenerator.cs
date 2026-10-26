using System.Data;

namespace EavFilteredIndexes;

/// <summary>
/// Builds a deterministic EAV import. Each attribute has a fixed type, and the attribute
/// catalogue is weighted the way real tracked-item data tends to be: mostly free text.
/// </summary>
public sealed class DataGenerator
{
    // How many attributes of each type the catalogue has (out of 200).
    private static readonly (string Column, int Count)[] CatalogueShape =
    [
        ("TextValue", 160),
        ("NumberValue", 12),
        ("DateValue", 10),
        ("FlagValue", 6),
        ("LookupId", 6),
        ("ReferenceItemId", 3),
        ("UserId", 2),
        ("CodeValue", 1),
    ];

    public const int AttributesPerItem = 12;

    private readonly string[] _attributeColumn; // index = AttributeId - 1
    private readonly int _seed;

    public DataGenerator(int seed = 42)
    {
        _seed = seed;
        _attributeColumn = CatalogueShape.SelectMany(s => Enumerable.Repeat(s.Column, s.Count)).ToArray();
    }

    public int AttributeCount => _attributeColumn.Length;

    public string ColumnFor(int attributeId) => _attributeColumn[attributeId - 1];

    /// <summary>An empty table whose columns match <see cref="Schema"/>, for SqlBulkCopy.</summary>
    public static DataTable CreateBuffer()
    {
        var t = new DataTable();
        t.Columns.Add("ItemId", typeof(int));
        t.Columns.Add("AttributeId", typeof(int));
        t.Columns.Add("TextValue", typeof(string));
        t.Columns.Add("NumberValue", typeof(decimal));
        t.Columns.Add("DateValue", typeof(DateTime));
        t.Columns.Add("FlagValue", typeof(bool));
        t.Columns.Add("LookupId", typeof(int));
        t.Columns.Add("ReferenceItemId", typeof(int));
        t.Columns.Add("UserId", typeof(int));
        t.Columns.Add("CodeValue", typeof(string));
        return t;
    }

    /// <summary>Yields rows in batches so a large import never sits in memory at once.</summary>
    public IEnumerable<DataTable> Batches(int items, int batchSize)
    {
        var rng = new Random(_seed);
        var buffer = CreateBuffer();
        for (var itemId = 1; itemId <= items; itemId++)
        {
            foreach (var attributeId in PickAttributes(rng))
            {
                buffer.Rows.Add(BuildRow(buffer, rng, itemId, attributeId, items));
                if (buffer.Rows.Count == batchSize)
                {
                    yield return buffer;
                    buffer = CreateBuffer();
                }
            }
        }
        if (buffer.Rows.Count > 0) yield return buffer;
    }

    private IEnumerable<int> PickAttributes(Random rng)
    {
        var picked = new HashSet<int>();
        while (picked.Count < AttributesPerItem) picked.Add(rng.Next(1, AttributeCount + 1));
        return picked;
    }

    private DataRow BuildRow(DataTable t, Random rng, int itemId, int attributeId, int items)
    {
        var row = t.NewRow();
        row["ItemId"] = itemId;
        row["AttributeId"] = attributeId;
        var column = ColumnFor(attributeId);
        row[column] = column switch
        {
            "TextValue" => $"Note {rng.Next(1_000_000):D6}",
            "NumberValue" => Math.Round((decimal)rng.NextDouble() * 10_000m, 4),
            "DateValue" => new DateTime(2015, 1, 1).AddDays(rng.Next(4000)),
            "FlagValue" => rng.Next(2) == 1,
            "LookupId" => rng.Next(1, 500),
            "ReferenceItemId" => rng.Next(1, items + 1),
            "UserId" => rng.Next(1, 300),
            "CodeValue" => $"C-{rng.Next(100_000):D5}",
            _ => throw new InvalidOperationException(column),
        };
        return row;
    }
}
