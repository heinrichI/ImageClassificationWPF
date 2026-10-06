# ProbeBenchmark

Standalone benchmark that compares cache-probe strategies for the app's
`SqliteVectorStore.GetBatchAsync` — the step that checks which of the (potentially
hundreds of thousands of) image files already have a CLIP embedding in the
`embeddings` table before a Search Image / Analyze run.

Built to answer: **"Probing embedding cache..." feels very long in the app — which
SQL strategy is actually fastest, and is the slowness even in SQL?**

## How to run

```
cd benchmarks/ProbeBenchmark
dotnet build -c Release

dotnet run -c Release              # all datasets (D0..D4, full matrix)
dotnet run -c Release -- D1 D4     # only the listed datasets
dotnet run -c Release -- inspect   # diagnose the app's real DB (rows vs disk state)
```

- Synthetic datasets are generated into `%TEMP%\probe-bench\probe_dX.db` and reused
  on later runs (delete those files to regenerate).
- **D4 uses the real app database** — `vector_cache.db` is located relative to this
  project (`..\..\src\ImageClassification.UI\bin\<config>\net8.0-windows\`) and is
  opened **read-only**; the probe set is a live recursive scan of the configured
  search directory (`J:\Downloads3\body`, ~300,500 files). The app may be closed or
  open — the read-only connection tolerates both.
- Each strategy runs cold (first use of a fresh connection) and warm ×3; the
  results are verified against a file-level oracle (mtime+size vs stored values)
  before timing — a strategy that misclassifies rows is reported as FAIL and its
  time is not to be trusted.
- The BLOB deserializer is tested in two variants (the app's old per-float loop
  vs a single `Buffer.BlockCopy`) and the difference is reported separately.

## Datasets

| Name            | Meaning                                                             |
|-----------------|---------------------------------------------------------------------|
| D0 `early(8.8k)`     | ~8.8k cached rows (first app state), 300k probe files  |
| D1 `current(51.3k)`  | ~51k cached rows, 300k probe files                          |
| D2 `full(300k)`      | every probed file is cached and fresh                        |
| D3 `full+5%stale`    | as D2 but 5 % of files "changed" on disk (stale rows)        |
| D4 `REAL`            | the app's real `vector_cache.db` + live scan of the search dir |

## Strategies under test

| Key | Strategy |
|-----|----------|
| `IN-500` | chunked `IN (…)` probes, 500 paths per query — the code the app shipped with after the "too many SQL variables" fix |
| `IN-2000` / `IN-16000` / `IN-32000` | same idea with bigger chunks (32k = the host's max parameter limit) |
| `TMP-JOIN` | temp table of the probe set + `JOIN` against `embeddings` (the variant that was implemented and then rolled back from the app) |
| `TMP-JOIN-mem` | same with `PRAGMA temp_store=MEMORY` |
| `TMP-EXISTS` | temp table + correlated `EXISTS` (drives the probe from the cached rows) |
| `TMP-SEMI` | temp table + `IN (SELECT …)` semi-join |
| `SCAN-2PHASE` | sequential scan of the model's rows (path/lm/size only) → classify in C# → read the BLOBs of hits |
| `SCAN-2PH-IN` | same, phase B fetches hit BLOBs via chunked `IN` |
| `SCAN-1PHASE` | **one** sequential scan of the model's rows including the BLOB column; everything classified in C# — **the winner, now implemented in `SqliteVectorStore.GetBatchAsync`** |

## Results (warm median, ms; probe set = 300,479 files; 2026-07-05, machine with
16 GB RAM, app DB at 3.36 GB / ~89,600 ClipViTB32 rows)

| strategy           | D0 early(8.8k) | D1 current(51.3k) | D2 full(300k) | D3 full+5%stale | D4 REAL |
|--------------------|---------------:|------------------:|--------------:|----------------:|--------:|
| IN-500 (app then)  |          1,233 |             1,523 |         4,188 |           4,109 |   1,887 |
| IN-2000            |          3,574 |             3,808 |         6,453 |           6,474 |   4,171 |
| IN-16000           |         23,490 |            23,961 |        26,879 |          26,443 |  24,126 |
| IN-32000           |         46,699 |            47,125 |        49,599 |          50,197 |  47,617 |
| TMP-JOIN           |          2,309 |             3,010 |         8,794 |           9,040 |   3,057 |
| TMP-JOIN-mem       |          2,149 |             2,512 |         6,258 |           6,397 |   2,885 |
| TMP-EXISTS         |            753 |             1,083 |         4,758 |           4,616 |   1,427 |
| TMP-SEMI           |            848 |             1,169 |         4,725 |           4,812 |   1,531 |
| SCAN-2PHASE        |            190 |               362 |         6,136 |           6,372 |     962 |
| SCAN-2PH-IN        |            174 |               376 |         7,467 |           7,247 |   1,307 |
| **SCAN-1PHASE**    |            **93** |             **375** |         **3,582** |         **3,476** |   **584** |

### What is faster, and why

1. **`SCAN-1PHASE` wins everywhere** — 3–13× faster than the chunked `IN` the app
   shipped with: a plain sequential scan of the (much smaller) cached row set
   touches pages in order, uses a single statement with **one** parameter, and is
   immune to SQLite's host-variable limit (999 / 32,766) by construction. Its cost
   scales with the number of **cached** rows, not with the number of probed files.
   → **This is what the app uses now** (`SqliteVectorStore.GetBatchAsync`).
2. **Bigger `IN` chunks are dramatically worse** (IN-32000 ≈ 30× slower than
   IN-500): SQLite's b-tree machinery per statement parameter dominates. The
   500-chunk size was already near-optimal among parameterized queries — but a
   scan beats even that.
3. **Temp-table variants never beat `IN-500` on full-scale data** (D2/D3): the
   join/hash machinery on 300k probe rows costs more than the chunked index
   seeks. They only win on small caches (D0/D1). This confirmed — with numbers —
   that rolling the temp-table variant back from the app was the right call.
4. **The SQL probe is NOT the user-visible bottleneck.** On the real 3.36 GB
   database the probe is 0.6–1.9 s at most. `inspect` mode verified that all
   cached rows are fresh (stored timestamps are uniform `...Z` UTC, sizes match),
   so a real search is: directory scan → 89,649 instant cache hits → the rest
   encoded on the GPU. The "very long" phase users observe is the **directory
   scan** (6 pattern passes over ~300k files on the external drive), which now
   emits throttled "Scanning images: … (N files so far)" status ticks and a
   per-phase elapsed-time log line from `ImageSearchService`.
5. **Deserializer**: the app's old per-float `Array.Copy` loop adds ~0.3–1.3 s on
   top of any strategy; a single `Buffer.BlockCopy` of the little-endian float
   payload is 3–10× cheaper and was also ported into the store.

## Notes / gotchas discovered while writing this benchmark

- `DateTime.Parse(stored).ToString("O")` is **not** a safe normalization: a stored
  timestamp written in a different offset format (e.g. `+03:00`) round-trips to a
  *different* `O` string and looks "stale" forever. Compare the **raw** stored
  string with the raw current one (the app stores and compares `DateTime.ToString("O")`
  on both sides, which is consistent — verified on the real DB via `inspect`).
- Synthetic BLOBs must be **per-row unique** (`Blob.Template.Clone()`): sharing one
  array makes every row identical and silently breaks hit/stale verification.
- SQLite `COMMIT` does **not** drop `TEMP TABLE`s; pooled connections can reuse a
  connection that still holds a leftover temp table — unique table names + an
  explicit `DROP` are mandatory (the app's temp-table variant had this bug before
  it was rolled back).
- The real app DB is 3.36 GB while live data is only ~250 MB — dead space from
  repeated delete/insert churn of cache rows; `VACUUM` (planned as a
  "Compact vector cache" command) would shrink it back down.