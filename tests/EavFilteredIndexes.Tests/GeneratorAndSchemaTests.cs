using System.Data;

namespace EavFilteredIndexes.Tests;

public class GeneratorAndSchemaTests
{
    [Fact]
    public void Every_row_fills_exactly_one_value_column_matching_its_attribute()
    {
        var gen = new DataGenerator(seed: 1);
        var valueColumns = Schema.AllValueColumns.Select(c => c.Name).ToArray();

        foreach (DataRow row in gen.Batches(items: 500, batchSize: 1000).SelectMany(b => b.Rows.Cast<DataRow>()))
        {
            var filled = valueColumns.Where(c => row[c] != DBNull.Value).ToArray();
            Assert.Single(filled);
            Assert.Equal(gen.ColumnFor((int)row["AttributeId"]), filled[0]);
        }
    }

    [Fact]
    public void Same_seed_produces_identical_data()
    {
        static string Dump(DataGenerator g) => string.Join("|",
            g.Batches(200, 10_000).SelectMany(b => b.Rows.Cast<DataRow>()).Select(r => string.Join(",", r.ItemArray)));

        Assert.Equal(Dump(new DataGenerator(7)), Dump(new DataGenerator(7)));
    }

    [Fact]
    public void Batches_respect_size_and_cover_every_row()
    {
        var batches = new DataGenerator().Batches(items: 1_000, batchSize: 5_000).ToList();

        Assert.All(batches, b => Assert.True(b.Rows.Count <= 5_000));
        Assert.Equal(1_000 * DataGenerator.AttributesPerItem, batches.Sum(b => b.Rows.Count));
    }

    [Fact]
    public void Each_item_gets_distinct_attributes()
    {
        var rows = new DataGenerator().Batches(300, 100_000).Single().Rows.Cast<DataRow>();

        Assert.All(rows.GroupBy(r => (int)r["ItemId"]),
            g => Assert.Equal(g.Count(), g.Select(r => (int)r["AttributeId"]).Distinct().Count()));
    }

    [Theory]
    [InlineData(IndexStrategy.Standard, false)]
    [InlineData(IndexStrategy.Filtered, true)]
    public void Only_filtered_strategy_adds_where_clause(IndexStrategy strategy, bool expectFilter)
    {
        var ddl = Schema.CreateIndexes(strategy).ToList();

        Assert.Equal(Schema.Indexed.Count, ddl.Count);
        Assert.All(ddl, d => Assert.Equal(expectFilter, d.Contains("IS NOT NULL")));
    }
}
