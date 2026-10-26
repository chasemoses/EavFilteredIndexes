# Results

600,000 rows (50,000 items x 12 attributes), seed 42, median of 3 runs, 2026-10-26.

## Import cost

| | Standard | Filtered | Change |
|---|---:|---:|---:|
| Nonclustered index entries | 4,200,000 | 120,296 | -97.1% |
| Nonclustered index pages | 14,792 | 582 | -96.1% |
| Entries maintained per inserted row | 7.00 | 0.20 | |
| Import time (s, median) | 11.34 | 2.65 | -76.6% |

## Entries per index

| Index | Standard | Filtered |
|---|---:|---:|
| PK_ItemValues | 600,000 | 600,000 |
| IX_ItemValues_NumberValue | 600,000 | 36,255 |
| IX_ItemValues_DateValue | 600,000 | 29,948 |
| IX_ItemValues_FlagValue | 600,000 | 18,000 |
| IX_ItemValues_LookupId | 600,000 | 18,055 |
| IX_ItemValues_ReferenceItemId | 600,000 | 8,947 |
| IX_ItemValues_UserId | 600,000 | 6,041 |
| IX_ItemValues_CodeValue | 600,000 | 3,050 |

## Do queries still use the indexes?

| Query | Standard plan | Filtered plan |
|---|---|---|
| Equality lookup on a typed value (parameterized) | Seek on `IX_ItemValues_NumberValue` | Seek on `IX_ItemValues_NumberValue` |
| Range lookup on a date value (parameterized) | Seek on `IX_ItemValues_DateValue` | Seek on `IX_ItemValues_DateValue` |
| Searching for a missing value (IS NULL) | Seek on `IX_ItemValues_LookupId` | Scan of `PK_ItemValues` |

## Insert from a session with QUOTED_IDENTIFIER OFF

Failed, as expected for a table with filtered indexes: `Error 1934: INSERT failed because the following SET options have incorrect settings: 'QUOTED_IDENTIFIER'. Verify that SET options are correct for use with indexed views and/or indexes on computed columns and/or filtered indexes and/or query notifications and/or XML data type methods and/or spatial index operations.`
