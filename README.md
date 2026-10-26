# Filtered indexes for a slow EAV import

A reproducible SQL Server benchmark of a fix I made at work: a bulk import into an entity-attribute-value (EAV) table was slow, and the cause was index maintenance, not the inserts themselves. Replacing seven standard indexes with filtered indexes cut the index entries SQL Server maintains per inserted row from **7.00 to 0.20** and the import time by **77%** in this reproduction.

This is a generic rebuild. The schema, data, and code are new; nothing is copied from my employer's system. The production numbers were similar in shape: about 196,000 index entries down to about 2,300 for one import (~98.8%).

**Skills:** SQL Server indexing and execution plans, performance investigation, C# / .NET 9, ADO.NET (`SqlBulkCopy`, `Microsoft.Data.SqlClient`), xUnit, technical writing.

## Run it

Requires the .NET 9 SDK and SQL Server (LocalDB works).

```bash
dotnet test
dotnet run --project src/EavFilteredIndexes -c Release -- --items 50000 --runs 3
```

`--server "<connection string>"` points it at another SQL Server. It creates and drops a database named `EavIndexDemo`. The report prints and saves to [results/latest.md](results/latest.md).

## Results

600,000 rows, median of 3 runs, LocalDB on a desktop. Full output in [results/latest.md](results/latest.md).

| | Standard indexes | Filtered indexes | Change |
|---|---:|---:|---:|
| Nonclustered index entries | 4,200,000 | 120,296 | −97.1% |
| Entries maintained per inserted row | 7.00 | 0.20 | |
| Index pages | 14,792 | 582 | −96.1% |
| Import time | 11.34 s | 2.65 s | −76.6% |

| Query | Standard plan | Filtered plan |
|---|---|---|
| `NumberValue = @v` (parameterized) | Index seek | Index seek |
| `DateValue BETWEEN @from AND @to` | Index seek | Index seek |
| `LookupId IS NULL` | Index seek | **Clustered scan** |

## How I worked through it

### 1. Symptom
An import of tracked items got slower as the table grew. The application code was batching sensibly, and the inserted data was small. That points away from the inserts and toward everything SQL Server does *because* of each insert.

### 2. Look at the table, not the code
The table is EAV: one row per (item, attribute, value), with a typed value column for each kind of data (number, date, lookup, user...). Only one of those columns is filled on any row, and most rows are free text, which isn't indexed. But each typed column had its own nonclustered index so it could be searched.

A standard index stores an entry for **every** row, including rows where the indexed column is null. So every inserted row cost the clustered index plus seven nonclustered entries, and on most rows all seven entries were for nulls nobody would ever search for.

### 3. Hypothesis and fix
A filtered index (`WHERE NumberValue IS NOT NULL`) only stores rows that match its filter. Searches never look for "the number is null" on a number attribute, so the filtered version should serve the same queries while skipping almost every row on insert.

```sql
CREATE NONCLUSTERED INDEX IX_ItemValues_NumberValue
    ON dbo.ItemValues (AttributeId, NumberValue) INCLUDE (ItemId)
    WHERE NumberValue IS NOT NULL;
```

### 4. Measure, don't assume
The benchmark counts index entries and pages from `sys.dm_db_partition_stats`, which is the direct measurement of the hypothesis. Time is secondary, because it varies by machine. Both strategies load identical data from a fixed seed, with the indexes in place before the load, as they were in production.

### 5. Check what it costs
A smaller index is only a win if the queries that need it still use it. The benchmark runs each query and reads the **actual** execution plan:

- **Parameterized equality and range searches still seek.** SQL Server knows `col = @v` can never match a null, so it can use an `IS NOT NULL` filtered index even when the value is a parameter.
- **`IS NULL` searches lose the index.** "Which items are missing a lookup value?" now scans the table. Before shipping, I confirmed no screen or report asked that. If one had, I'd have kept a standard index on that one column.
- **Session SET options matter.** A table with filtered indexes rejects inserts from any session with `QUOTED_IDENTIFIER OFF` (error 1934). Modern drivers default to `ON`, but old scripts and some legacy tools don't. The benchmark demonstrates the failure, so it's on the checklist before deploying.

## What I'd do differently at larger scale
- EAV keeps the schema flexible but pushes the cost onto indexing and querying. For attributes that every item has, I'd promote them to real columns.
- For very large one-time loads, dropping the nonclustered indexes and rebuilding them afterward can beat maintaining them row by row. That only works if nothing needs to read the table during the load.

## Project layout

| Path | What it is |
|---|---|
| [`Schema.cs`](src/EavFilteredIndexes/Schema.cs) | Table DDL; the two index strategies differ only by the `WHERE` clause |
| [`DataGenerator.cs`](src/EavFilteredIndexes/DataGenerator.cs) | Deterministic EAV data, weighted 80% free text |
| [`Benchmark.cs`](src/EavFilteredIndexes/Benchmark.cs) | Bulk import, index statistics, actual-plan capture, SET-options check |
| [`tests/`](tests/EavFilteredIndexes.Tests) | Generator and DDL tests |

Built with AI assistance (Claude Code). I directed the investigation, chose what to measure, and checked every result against SQL Server.
