// ProbeBenchmark — measures alternative SQL strategies for IVectorStore.GetBatchAsync
// against a replica of the real `embeddings` table (same DDL, same BLOB format,
// same Microsoft.Data.Sqlite 8.0.2 as the app).
//
// Datasets (probe size = 300,479 files — the user's real J:\Downloads3\body count):
//   D0 early       8,801 cached rows, 0 stale   (mostly-missing state)
//   D1 current    51,313 cached rows, 0 stale   (state of the DB a few days ago)
//   D2 full      300,000 cached rows, 0 stale   (after the one-time encoding finishes)
//   D3 full+stale 300,000 cached rows, 5% stale
//   D4 REAL      the app's actual vector_cache.db (opened read-only) + a live
//                recursive scan of the real search directory
//
// RESULTS (2026-07-05, warm median ms over the 300,479-file probe — full table
// and methodology in README.md):
//
//   strategy        D0 8.8k   D1 51k    D2 300k   D3 300k+stale   D4 REAL
//   IN-500  (app)   1,233     1,523     4,188     4,109            1,887
//   TMP-JOIN        2,309     3,010     8,794     9,040            3,057
//   TMP-EXISTS        753     1,083     4,758     4,616            1,427
//   SCAN-1PHASE        93       375     3,582     3,476              584  <-- winner
//
// CONCLUSIONS:
//  1. SCAN-1PHASE (one sequential scan of the model's cached rows, a single SQL
//     parameter, hit/stale classification in C#) wins on every dataset by 3-13x
//     and is immune to SQLite's host-variable limit (999/32766) -> it is now
//     what SqliteVectorStore.GetBatchAsync implements.
//  2. Bigger IN chunks are dramatically WORSE (IN-32000 ~30x slower than IN-500);
//     500 was already near-optimal among parameterized queries.
//  3. Temp-table variants (JOIN/EXISTS/semi-join) never beat IN-500 on the
//     full-scale datasets -> the rollback of the temp-table probe from the app
//     was the right call.
//  4. The SQL probe itself is only 0.6-1.9 s even on the real 3.4 GB DB, so the
//     "very long" phase users observe is NOT the probe — it is the directory
//     scan (6 pattern passes over ~300k files on the external drive). The app
//     now logs per-phase timings and reports throttled "Scanning images ... N
//     files so far" status ticks for exactly this reason.
//  5. The fast BLOB deserializer (single Buffer.BlockCopy of the little-endian
//     float payload) saves another 0.3-1.3 s and is used by the app as well.
//  6. `inspect` mode verified the real cache: all stored rows are fresh
//     (uniform '...Z' UTC timestamps, sizes match disk). Lesson: compare the RAW
//     stored strings — DateTime.Parse(stored).ToString("O") is NOT a safe
//     normalization (offset-format timestamps do not round-trip and would make
//     every row look stale forever).
//
// Usage:
//   dotnet run -c Release                 -> full matrix (D0..D4)
//   dotnet run -c Release -- D1           -> one dataset
//   dotnet run -c Release -- D1 D4        -> a few datasets
//   dotnet run -c Release -- inspect      -> diagnose the real DB (rows vs disk)

using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;

// ---------------------------------------------------------------------------
// records / state
// ---------------------------------------------------------------------------

internal sealed record CachedRow(string Path, DateTime Lm, long Size, string? LmRaw = null)
{
    public string LmStr => Lm.ToString("O");
    // raw stored timestamp (D4); for synthetic sets it equals LmStr
    public string LmStored => LmRaw ?? LmStr;
}

internal sealed record Dataset(string Name, int RowCount, int StalePct, int Seed)
{
    public string DbPath { get; set; } = "";
    public string ConnStr { get; set; } = "";   // connection string for strategies (read-only for D4)
    public bool CheckValues { get; set; } = true;
    public List<CachedRow> Rows = null!;      // stored rows (in insertion order)
    public List<CachedRow> Probe = null!;     // probe entries (order as the app passes them)
    public HashSet<string> ExpectedHits = null!;
    public HashSet<string> ExpectedStale = null!;
    public Dictionary<string, CachedRow> ProbeInfo = null!; // path -> probe entry (first wins)
    public Dictionary<string, int> RowIndex = null!;        // path -> row index (for value checks)
    public bool Skip { get; set; }
    public bool Generated = false;
}

// expected BLOB content for row i: float[0] = i, float[k] = (k % 7) * 0.01f
static class Blob
{
    public const int Dim = 512;
    public static byte[] Template { get; } = MakeTemplate();

    private static byte[] MakeTemplate()
    {
        var b = new byte[4 + Dim * 4];
        BitConverter.GetBytes(Dim).CopyTo(b, 0);
        for (var k = 1; k < Dim; k++)
            BitConverter.GetBytes((k % 7) * 0.01f).CopyTo(b, 4 + k * 4);
        return b;
    }

    public static void StampRow(byte[] b, int rowIndex)
    {
        BitConverter.GetBytes((float)rowIndex).CopyTo(b, 4); // b is a working copy of Template
    }
}

// deserializers — the app's current one (slow, per-float Array.Copy) and a fast one
static class Deser
{
    public static float[] Slow(byte[] blob)
    {
        int dim = BitConverter.ToInt32(blob, 0);
        var result = new float[dim];
        for (int i = 0; i < dim; i++)
        {
            var fBytes = new byte[4];
            Array.Copy(blob, 4 + i * 4, fBytes, 0, 4);
            result[i] = BitConverter.ToSingle(fBytes, 0);
        }
        return result;
    }

    public static float[] Fast(byte[] blob)
    {
        int dim = BitConverter.ToInt32(blob, 0);
        var result = new float[dim];
        Buffer.BlockCopy(blob, 4, result, 0, dim * 4);
        return result;
    }
}

// ---------------------------------------------------------------------------
// program
// ---------------------------------------------------------------------------

static class Program
{
    const int ProbeCount = 300_479;
    const string ModelName = "ClipViTB32";
    static readonly DateTime ProbeBaseDate = new(2027, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    static readonly string BenchDir = Path.Combine(Path.GetTempPath(), "probe-bench");

    // the app's real data (D4): resolve the real DB relative to the CWD or the binary location
    static string? FindRealDb()
    {
        string[] candidates =
        {
            Path.GetFullPath(@"src\ImageClassification.UI\bin\Debug\net8.0-windows\vector_cache.db"),
            Path.GetFullPath(@"../../src/ImageClassification.UI/bin/Debug/net8.0-windows/vector_cache.db"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ImageClassification.UI", "bin", "Debug", "net8.0-windows", "vector_cache.db")),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    static readonly string[] RealDirs = { @"J:\Downloads3\body" };
    static readonly string[] RealExts = { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".webp" };

    static readonly Dataset[] Datasets =
    {
        new Dataset("D0 early(8.8k)",    8_801,  0, 101),
        new Dataset("D1 current(51.3k)", 51_313, 0, 202),
        new Dataset("D2 full(300k)",    300_000, 0, 303),
        new Dataset("D3 full+5%stale",  300_000, 5, 404),
        new Dataset("D4 REAL",              0, 0, 505) { CheckValues = false },
    };

    // one probe run: takes the deserializer to use, returns the results map
    private delegate Dictionary<string, float[]?> ProbeRun(Func<byte[], float[]> deserializer);

    // strategy: dataset -> run closure
    static readonly (string Name, string Note, Func<Dataset, ProbeRun> Fn)[] Strategies =
    {
        ("IN-500",      "chunked IN, 500/query [current app code]",        d => ProbeInChunks(d, 500)),
        ("IN-2000",     "chunked IN, 2000/query",                          d => ProbeInChunks(d, 2000)),
        ("IN-16000",    "chunked IN, 16000/query",                         d => ProbeInChunks(d, 16_000)),
        ("IN-32000",    "chunked IN, 32000/query [max host-param limit]",  d => ProbeInChunks(d, 32_000)),
        ("TMP-JOIN",    "temp table + JOIN [reverted variant]",            d => ProbeTempJoin(d, mem: false)),
        ("TMP-JOIN-mem","temp table + JOIN, PRAGMA temp_store=MEMORY",     d => ProbeTempJoin(d, mem: true)),
        ("TMP-EXISTS",    "temp table + correlated EXISTS (drive from embeddings)", d => ProbeTempExists(d)),
        ("TMP-SEMI",    "temp table + IN (SELECT ...) semi-join",          d => ProbeTempSemi(d)),
        ("SCAN-2PHASE", "scan metadata (no BLOB) -> classify -> scan BLOBs for hits", d => ProbeScanTwoPhase(d, fetchHitsByIn: false)),
        ("SCAN-2PH-IN", "scan metadata (no BLOB) -> classify -> IN-500 over hits",    d => ProbeScanTwoPhase(d, fetchHitsByIn: true)),
        ("SCAN-1PHASE", "single scan, all columns incl. BLOB",             d => ProbeScanOnePhase(d)),
    };

    static int Main(string[] args)
    {
        Directory.CreateDirectory(BenchDir);

        if (args.Length > 0 && args[0] == "inspect")
        {
            return InspectRealDb();
        }

        var selected = args.Length == 0
            ? Datasets
            : Datasets.Where(d => args.Any(a => a.Trim() == d.Name[..2])).ToArray();
        if (selected.Length == 0)
        {
            Console.WriteLine("unknown datasets; use e.g.: dotnet run -c Release -- D1 D2");
            return 2;
        }

        Console.WriteLine("generating datasets (skipped when the DB file already exists)...");
        foreach (var ds in selected)
            EnsureDataset(ds);
        selected = selected.Where(d => !d.Skip).ToArray();

        Console.WriteLine($"\nprobe set: {selected[0].Probe.Count:N0} files | deserializer: Deser.Fast\n");

        var medianMs = new Dictionary<string, Dictionary<string, double>>();
        var coldMs = new Dictionary<string, Dictionary<string, double>>();

        foreach (var ds in selected)
        {
            Console.WriteLine($"=== {ds.Name}  ({ds.Rows.Count:N0} rows, {ds.ExpectedStale.Count:N0} stale) ===");
            medianMs[ds.Name] = new Dictionary<string, double>();
            coldMs[ds.Name] = new Dictionary<string, double>();

            foreach (var (name, note, strategy) in Strategies)
            {
                Func<byte[], float[]> fast = Deser.Fast;

                // 1) COLD run (first touch) — also used for the correctness check
                Dictionary<string, float[]?> first;
                double cold;
                using (var sw = NewStopwatch())
                {
                    first = strategy(ds)(fast);
                    cold = sw.ElapsedMilliseconds;
                }
                string check = Verify(first, ds);

                // 2) warm runs
                var times = new List<double>(2);
                for (int r = 0; r < 2; r++)
                {
                    using var sw = NewStopwatch();
                    strategy(ds)(fast);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                double med = Median(times);
                medianMs[ds.Name][name] = med;
                coldMs[ds.Name][name] = cold;
                Console.WriteLine($"  {name,-13} {note,-56} cold={cold,8:N0} ms   warm={med,8:N0} ms   {check}");
            }
            Console.WriteLine();
        }

        var names = Strategies.Select(s => s.Name).ToList();
        Console.WriteLine("WARM median (ms) — lower is better:");
        PrintTable(selected, medianMs, names);
        Console.WriteLine("\nCOLD first run (ms):");
        PrintTable(selected, coldMs, names);

        Console.WriteLine("\nDeserializer impact (IN-500 SQL, only BLOB decoding differs):");
        Console.WriteLine("  " + "dataset".PadRight(20) + "slow (app today)".PadLeft(16) + "fast".PadLeft(12));
        foreach (var ds in selected)
        {
            double tSlow, tFast;
            using (var sw = NewStopwatch()) { ProbeInChunksCore(ds, Deser.Slow, 500); tSlow = sw.Elapsed.TotalMilliseconds; }
            using (var sw = NewStopwatch()) { ProbeInChunksCore(ds, Deser.Fast, 500); tFast = sw.Elapsed.TotalMilliseconds; }
            Console.WriteLine($"  {ds.Name,-20} {tSlow,15:N0} ms {tFast,11:N0} ms   (hits={ds.ExpectedHits.Count:N0})");
        }

        return 0;
    }

    static void PrintTable(Dataset[] selected, Dictionary<string, Dictionary<string, double>> data, List<string> strategyOrder)
    {
        Console.WriteLine("  " + "strategy".PadRight(14) + string.Concat(selected.Select(d => d.Name.PadLeft(20))));
        foreach (var s in strategyOrder)
            Console.WriteLine("  " + s.PadRight(14) + string.Concat(selected.Select(d => $"{data[d.Name][s],19:N0} ms")));
    }

    static double Median(List<double> xs)
    {
        xs.Sort();
        return xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) / 2;
    }

    // ------------------------------------------------------------------
    // dataset generation (real DDL, real BLOB format)
    // ------------------------------------------------------------------

    static void EnsureDataset(Dataset ds)
    {
        if (ds.Generated) return;
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ds.Rows = new List<CachedRow>(ds.RowCount);

        if (ds.Seed == 505)
        {
            // D4: the app's real DB (read-only) + a fresh scan of the real directories
            string? realDb = FindRealDb();
            if (realDb is null || RealDirs.Any(d => !Directory.Exists(d)))
            {
                Console.WriteLine($"  [D4 skipped: {(realDb is null ? "real DB not found" : "real directory not found")}]");
                ds.Generated = true;
                ds.Skip = true;
                return;
            }
            ds.ConnStr = $"Data Source={realDb};Mode=ReadOnly";
            ds.DbPath = realDb;

            using (var conn = new SqliteConnection(ds.ConnStr))
            {
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT file_path, last_modified, file_size FROM embeddings WHERE model_name = 'ClipViTB32' ORDER BY rowid", conn);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    string rawLm = rd.GetString(1);
                    ds.Rows.Add(new CachedRow(rd.GetString(0), DateTime.Parse(rawLm), rd.GetInt64(2), LmRaw: rawLm));
                }
            }

            ds.Probe = new List<CachedRow>(ProbeCount);
            foreach (var dir in RealDirs)
            {
                var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                    .Where(f => RealExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .ToList();
                foreach (var f in files)
                {
                    var fi = new FileInfo(f);
                    ds.Probe.Add(new CachedRow(fi.FullName, fi.LastWriteTimeUtc, fi.Length));
                }
            }

            ds.ProbeInfo = new Dictionary<string, CachedRow>(ds.Probe.Count);
            foreach (var e in ds.Probe) ds.ProbeInfo.TryAdd(e.Path, e);

            // oracle: raw stored timestamp vs current file "O" string (exactly what the app compares)
            ds.ExpectedHits = new HashSet<string>(ds.Rows.Count);
            ds.ExpectedStale = new HashSet<string>(ds.Rows.Count);
            foreach (var row in ds.Rows)
            {
                if (!ds.ProbeInfo.TryGetValue(row.Path, out var info)) continue; // deleted file
                if (info.LmStr == row.LmStored && info.Size == row.Size) ds.ExpectedHits.Add(row.Path);
                else ds.ExpectedStale.Add(row.Path);
            }
            ds.RowIndex = new Dictionary<string, int>();
            ds.Generated = true;
            return;
        }

        ds.DbPath = Path.Combine(BenchDir, $"probe_{ds.Name[..2].ToLowerInvariant()}.db");
        ds.ConnStr = $"Data Source={ds.DbPath}";
        var blob = (byte[])Blob.Template.Clone();

        if (!File.Exists(ds.DbPath))
        {
            using var conn = new SqliteConnection($"Data Source={ds.DbPath}");
            conn.Open();
            using (var wal = new SqliteCommand("PRAGMA journal_mode=WAL;", conn)) wal.ExecuteNonQuery();
            using (var ddl = new SqliteCommand(@"
                CREATE TABLE embeddings (
                    model_name    TEXT    NOT NULL,
                    file_path     TEXT    NOT NULL,
                    last_modified TEXT    NOT NULL,
                    file_size     INTEGER NOT NULL,
                    embedding     BLOB    NOT NULL,
                    created_at    TEXT    NOT NULL,
                    PRIMARY KEY (model_name, file_path)
                );", conn)) ddl.ExecuteNonQuery();

            using var tx = conn.BeginTransaction();
            string caLiteral = $"'{baseDate.ToString("O")}'";
            for (var start = 0; start < ds.RowCount; start += 1000)
            {
                int count = Math.Min(1000, ds.RowCount - start);
                using var cmd = new SqliteCommand();
                cmd.Connection = conn;
                cmd.Transaction = tx;

                var values = new StringBuilder(count * 40);
                for (int i = 0; i < count; i++)
                {
                    int gi = start + i;
                    if (i > 0) values.Append(',');
                    // model_name and created_at are constants — inlined to avoid
                    // duplicate parameter names across the multi-row VALUES list
                    values.Append($"('ClipViTB32', @p{i}, @lm{i}, @fs{i}, @b{i}, {caLiteral})");
                    cmd.Parameters.AddWithValue($"@p{i}", $"J:\\BenchBody\\sub{(gi % 199):000}\\f{gi:000000}.jpg");
                    cmd.Parameters.AddWithValue($"@lm{i}", baseDate.AddMilliseconds(gi).ToString("O"));
                    cmd.Parameters.AddWithValue($"@fs{i}", 100_000 + gi % 40_000);
                    // each row gets its OWN blob copy — the provider binds arrays by reference
                    var rowBlob = (byte[])Blob.Template.Clone();
                    Blob.StampRow(rowBlob, gi);
                    cmd.Parameters.AddWithValue($"@b{i}", rowBlob);

                    ds.Rows.Add(new CachedRow(
                        $"J:\\BenchBody\\sub{(gi % 199):000}\\f{gi:000000}.jpg",
                        baseDate.AddMilliseconds(gi),
                        100_000 + gi % 40_000));
                }

                cmd.CommandText =
                    "INSERT INTO embeddings (model_name, file_path, last_modified, file_size, embedding, created_at) VALUES " + values;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            using (var cp = new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE);", conn)) cp.ExecuteNonQuery();
        }
        else
        {
            using var conn = new SqliteConnection($"Data Source={ds.DbPath}");
            conn.Open();
            using var cmd = new SqliteCommand(
                "SELECT file_path, last_modified, file_size FROM embeddings ORDER BY rowid", conn);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                ds.Rows.Add(new CachedRow(rd.GetString(0), DateTime.Parse(rd.GetString(1)), rd.GetInt64(2)));
        }

        if (ds.RowCount > 0 && ds.Rows.Count != ds.RowCount)
            throw new InvalidOperationException($"{ds.Name}: row count mismatch {ds.Rows.Count} != {ds.RowCount}");

        ds.RowIndex = ds.CheckValues
            ? new Dictionary<string, int>(ds.RowCount)
            : new Dictionary<string, int>();
        if (ds.CheckValues)
            for (int i = 0; i < ds.RowCount; i++)
                ds.RowIndex[ds.Rows[i].Path] = i;

        // probe = rows (fresh or stale) + missing paths
        ds.Probe = new List<CachedRow>(ProbeCount);
        ds.ExpectedHits = new HashSet<string>(ds.RowCount);
        ds.ExpectedStale = new HashSet<string>(ds.RowCount);

        int staleEvery = ds.StalePct > 0 ? 100 / ds.StalePct : int.MaxValue; // 5% -> every 20th
        for (int i = 0; i < ProbeCount; i++)
        {
            if (i < ds.RowCount)
            {
                var row = ds.Rows[i];
                bool stale = ds.StalePct > 0 && i % staleEvery == staleEvery / 2;
                var entry = stale
                    ? new CachedRow(row.Path, row.Lm.AddMilliseconds(1), row.Size)
                    : row;
                ds.Probe.Add(entry);
                if (stale) ds.ExpectedStale.Add(row.Path); else ds.ExpectedHits.Add(row.Path);
            }
            else
            {
                ds.Probe.Add(new CachedRow(
                    $"J:\\BenchBody\\missing\\m{i:000000}.jpg", ProbeBaseDate, 200_000));
            }
        }

        ds.ProbeInfo = new Dictionary<string, CachedRow>(ProbeCount);
        foreach (var e in ds.Probe)
            ds.ProbeInfo.TryAdd(e.Path, e);

        ds.Generated = true;
    }

    // ------------------------------------------------------------------
    // strategies
    // ------------------------------------------------------------------

    // chunked IN — exactly the app's current algorithm
    static ProbeRun ProbeInChunks(Dataset ds, int chunk)
        => (deser) => ProbeInChunksCore(ds, deser, chunk);

    static Dictionary<string, float[]?> ProbeInChunksCore(Dataset ds, Func<byte[], float[]> deser, int chunk)
    {
        var probe = ds.Probe;
        var results = SeedResults(probe);
        var probeInfo = ds.ProbeInfo;

        using var conn = new SqliteConnection(ds.ConnStr);
        conn.Open();

        for (var start = 0; start < probe.Count; start += chunk)
        {
            int count = Math.Min(chunk, probe.Count - start);
            using var cmd = new SqliteCommand();
            cmd.Connection = conn;

            var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                names[i] = $"@fp{i}";
                cmd.Parameters.AddWithValue(names[i], probe[start + i].Path);
            }
            cmd.CommandText = "SELECT file_path, embedding, last_modified, file_size " +
                              "FROM embeddings " +
                              "WHERE model_name = @mn AND file_path IN (" + string.Join(",", names) + ")";
            cmd.Parameters.AddWithValue("@mn", ModelName);

            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                string path = rd.GetString(0);
                if (!probeInfo.TryGetValue(path, out var info)) continue;
                if (rd.GetString(2) == info.LmStr && rd.GetInt64(3) == info.Size)
                    results[path] = deser(rd.GetFieldValue<byte[]>(1));
            }
        }
        return results;
    }

    // temp table + JOIN (the reverted variant, incl. memory-temp pragma variant)
    static ProbeRun ProbeTempJoin(Dataset ds, bool mem)
        => (deser) =>
        {
            var results = SeedResults(ds.Probe);
            string table = $"_bp_{Guid.NewGuid():N}";

            using var conn = new SqliteConnection(ds.ConnStr);
            conn.Open();
            if (mem)
                using (var p = new SqliteCommand("PRAGMA temp_store=2;", conn)) p.ExecuteNonQuery();

            using var tx = conn.BeginTransaction();
            try
            {
                using (var c = new SqliteCommand(
                    $"CREATE TEMP TABLE {table} (file_path TEXT PRIMARY KEY, last_modified TEXT NOT NULL, file_size INTEGER NOT NULL)", conn))
                {
                    c.Transaction = tx;
                    c.ExecuteNonQuery();
                }
                FillTemp(conn, tx, table, ds.Probe);

                string sql =
                    "SELECT e.file_path, e.embedding, e.last_modified, e.file_size, p.last_modified, p.file_size " +
                    "FROM embeddings e " +
                    $"JOIN {table} p ON p.file_path = e.file_path" +
                    " WHERE e.model_name = @mn";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Transaction = tx;
                cmd.Parameters.AddWithValue("@mn", ModelName);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    string path = rd.GetString(0);
                    if (rd.GetString(2) == rd.GetString(4) && rd.GetInt64(3) == rd.GetInt64(5))
                        results[path] = deser(rd.GetFieldValue<byte[]>(1));
                }
                tx.Commit();
                using (var d = new SqliteCommand($"DROP TABLE {table}", conn)) d.ExecuteNonQuery();
            }
            finally
            {
                tx.Dispose();
            }
            return results;
        };

    // temp table + correlated EXISTS — SQLite drives the loop from "embeddings"
    // (the small, model-filtered side) and does one index seek per row into the
    // temp table; freshness is checked in C# via the probe info map.
    static ProbeRun ProbeTempExists(Dataset ds)
        => (deser) =>
        {
            var results = SeedResults(ds.Probe);
            string table = $"_bp_{Guid.NewGuid():N}";
            var probeInfo = ds.ProbeInfo;

            using var conn = new SqliteConnection(ds.ConnStr);
            conn.Open();

            using var tx = conn.BeginTransaction();
            try
            {
                using (var c = new SqliteCommand(
                    $"CREATE TEMP TABLE {table} (file_path TEXT PRIMARY KEY)", conn))
                {
                    c.Transaction = tx;
                    c.ExecuteNonQuery();
                }
                FillTempPaths(conn, tx, table, ds.Probe);

                using var cmd = new SqliteCommand(
                    "SELECT file_path, embedding, last_modified, file_size " +
                    "FROM embeddings e " +
                    "WHERE e.model_name = @mn AND EXISTS (SELECT 1 FROM " + table + " p WHERE p.file_path = e.file_path)", conn);
                cmd.Transaction = tx;
                cmd.Parameters.AddWithValue("@mn", ModelName);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    string path = rd.GetString(0);
                    if (!probeInfo.TryGetValue(path, out var info)) continue;
                    if (rd.GetString(2) == info.LmStr && rd.GetInt64(3) == info.Size)
                        results[path] = deser(rd.GetFieldValue<byte[]>(1));
                }
                tx.Commit();
                using (var d = new SqliteCommand($"DROP TABLE {table}", conn)) d.ExecuteNonQuery();
            }
            finally
            {
                tx.Dispose();
            }
            return results;
        };

    // temp table + IN (SELECT ...) semi-join
    static ProbeRun ProbeTempSemi(Dataset ds)
        => (deser) =>
        {
            var results = SeedResults(ds.Probe);
            string table = $"_bp_{Guid.NewGuid():N}";
            var probeInfo = ds.ProbeInfo;

            using var conn = new SqliteConnection(ds.ConnStr);
            conn.Open();

            using var tx = conn.BeginTransaction();
            try
            {
                using (var c = new SqliteCommand(
                    $"CREATE TEMP TABLE {table} (file_path TEXT PRIMARY KEY)", conn))
                {
                    c.Transaction = tx;
                    c.ExecuteNonQuery();
                }
                FillTempPaths(conn, tx, table, ds.Probe);

                using var cmd = new SqliteCommand(
                    "SELECT file_path, embedding, last_modified, file_size " +
                    "FROM embeddings " +
                    $"WHERE model_name = @mn AND file_path IN (SELECT file_path FROM {table})", conn);
                cmd.Transaction = tx;
                cmd.Parameters.AddWithValue("@mn", ModelName);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    string path = rd.GetString(0);
                    if (!probeInfo.TryGetValue(path, out var info)) continue;
                    if (rd.GetString(2) == info.LmStr && rd.GetInt64(3) == info.Size)
                        results[path] = deser(rd.GetFieldValue<byte[]>(1));
                }
                tx.Commit();
                using (var d = new SqliteCommand($"DROP TABLE {table}", conn)) d.ExecuteNonQuery();
            }
            finally
            {
                tx.Dispose();
            }
            return results;
        };

    static void FillTemp(SqliteConnection conn, SqliteTransaction tx, string table, List<CachedRow> entries)
    {
        using var cmd = new SqliteCommand();
        cmd.Connection = conn;
        cmd.Transaction = tx;
        for (var start = 0; start < entries.Count; start += 300)
        {
            cmd.Parameters.Clear();
            int count = Math.Min(300, entries.Count - start);
            var values = new StringBuilder(count * 30);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) values.Append(',');
                int n = i * 3;
                values.Append($"(@p{n}, @lm{n}, @fs{n})");
                cmd.Parameters.AddWithValue($"@p{n}", entries[start + i].Path);
                cmd.Parameters.AddWithValue($"@lm{n}", entries[start + i].LmStr);
                cmd.Parameters.AddWithValue($"@fs{n}", entries[start + i].Size);
            }
            cmd.CommandText = $"INSERT INTO {table} (file_path, last_modified, file_size) VALUES " + values;
            cmd.ExecuteNonQuery();
        }
    }

    static void FillTempPaths(SqliteConnection conn, SqliteTransaction tx, string table, List<CachedRow> entries)
    {
        using var cmd = new SqliteCommand();
        cmd.Connection = conn;
        cmd.Transaction = tx;
        for (var start = 0; start < entries.Count; start += 500)
        {
            cmd.Parameters.Clear();
            int count = Math.Min(500, entries.Count - start);
            var values = new StringBuilder(count * 12);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) values.Append(',');
                values.Append($"(@p{i})");
                cmd.Parameters.AddWithValue($"@p{i}", entries[start + i].Path);
            }
            cmd.CommandText = $"INSERT INTO {table} (file_path) VALUES " + values;
            cmd.ExecuteNonQuery();
        }
    }

    // full scan by model (PK index range) — no per-file parameters at all
    static ProbeRun ProbeScanTwoPhase(Dataset ds, bool fetchHitsByIn)
        => (deser) =>
        {
            var results = SeedResults(ds.Probe);
            var probeInfo = ds.ProbeInfo;
            using var conn = new SqliteConnection(ds.ConnStr);
            conn.Open();

            // phase A: metadata only — SQLite does not touch the BLOB overflow pages
            var hits = new HashSet<string>(ds.RowCount);
            using (var cmd = new SqliteCommand(
                "SELECT file_path, last_modified, file_size FROM embeddings WHERE model_name = @mn", conn))
            {
                cmd.Parameters.AddWithValue("@mn", ModelName);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    string path = rd.GetString(0);
                    if (!probeInfo.TryGetValue(path, out var info)) continue;
                    if (rd.GetString(1) == info.LmStr && rd.GetInt64(2) == info.Size)
                        hits.Add(path);
                }
            }

            if (hits.Count == 0) return results;

            if (!fetchHitsByIn)
            {
                using var cmd = new SqliteCommand(
                    "SELECT file_path, embedding FROM embeddings WHERE model_name = @mn", conn);
                cmd.Parameters.AddWithValue("@mn", ModelName);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    string path = rd.GetString(0);
                    if (hits.Remove(path))
                        results[path] = deser(rd.GetFieldValue<byte[]>(1));
                }
            }
            else
            {
                var hitList = hits.ToList();
                for (var start = 0; start < hitList.Count; start += 500)
                {
                    int count = Math.Min(500, hitList.Count - start);
                    using var cmd = new SqliteCommand();
                    cmd.Connection = conn;
                    var names = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        names[i] = $"@fp{i}";
                        cmd.Parameters.AddWithValue(names[i], hitList[start + i]);
                    }
                    cmd.CommandText = "SELECT file_path, embedding FROM embeddings " +
                                      "WHERE model_name = @mn AND file_path IN (" + string.Join(",", names) + ")";
                    cmd.Parameters.AddWithValue("@mn", ModelName);
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                        results[rd.GetString(0)] = deser(rd.GetFieldValue<byte[]>(1));
                }
            }
            return results;
        };

    static ProbeRun ProbeScanOnePhase(Dataset ds)
        => (deser) =>
        {
            var results = SeedResults(ds.Probe);
            var probeInfo = ds.ProbeInfo;
            using var conn = new SqliteConnection(ds.ConnStr);
            conn.Open();
            using var cmd = new SqliteCommand(
                "SELECT file_path, last_modified, file_size, embedding FROM embeddings WHERE model_name = @mn", conn);
            cmd.Parameters.AddWithValue("@mn", ModelName);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                string path = rd.GetString(0);
                if (!probeInfo.TryGetValue(path, out var info)) continue;
                if (rd.GetString(1) == info.LmStr && rd.GetInt64(2) == info.Size)
                    results[path] = deser(rd.GetFieldValue<byte[]>(3));
            }
            return results;
        };

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    // Stopwatch is not IDisposable — a tiny wrapper so `using` scopes stay tidy
    private sealed class Stopw : IDisposable
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public long ElapsedMilliseconds => _sw.ElapsedMilliseconds;
        public TimeSpan Elapsed => _sw.Elapsed;
        public void Dispose() => _sw.Stop();
    }

    private static Stopw NewStopwatch() => new Stopw();

    // dump samples from the app's real DB to diagnose freshness mismatches
    static int InspectRealDb()
    {
        string? realDb = FindRealDb();
        if (realDb is null)
        {
            Console.WriteLine("real DB not found");
            return 2;
        }
        Console.WriteLine($"DB: {realDb}  size={new FileInfo(realDb).Length / (1024.0 * 1024.0):N0} MB");

        using var conn = new SqliteConnection($"Data Source={realDb};Mode=ReadOnly");
        conn.Open();

        using (var cmd = new SqliteCommand(
            "SELECT model_name, COUNT(*), MIN(rowid), MAX(rowid) FROM embeddings GROUP BY model_name", conn))
        using (var rd = cmd.ExecuteReader())
        {
            Console.WriteLine("\nrow counts per model:");
            while (rd.Read())
                Console.WriteLine($"  {rd.GetString(0),-14} {rd.GetInt64(1),10:N0}  rows (rowid {rd.GetInt64(2):N0}..{rd.GetInt64(3):N0})");
        }

        using (var cmd = new SqliteCommand(
            "SELECT file_path, last_modified, file_size FROM embeddings WHERE model_name = 'ClipViTB32' ORDER BY rowid LIMIT 12", conn))
        using (var rd = cmd.ExecuteReader())
        {
            Console.WriteLine("\nsample stored rows vs the file on disk right now:");
            while (rd.Read())
            {
                string path = rd.GetString(0);
                string storedLm = rd.GetString(1);
                long storedSize = rd.GetInt64(2);
                string file = "<missing on disk>";
                try
                {
                    var fi = new FileInfo(path);
                    file = $"lm={fi.LastWriteTimeUtc:O} size={fi.Length:N0}  " +
                           (fi.LastWriteTimeUtc.ToString("O") == storedLm && fi.Length == storedSize ? "FRESH" : "STALE");
                }
                catch { /* keep <missing on disk> */ }
                Console.WriteLine($"  stored_lm={storedLm}");
                Console.WriteLine($"    {path}  [{file}]");
            }
        }

        using (var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM embeddings WHERE model_name='ClipViTB32' AND last_modified NOT LIKE '%Z'", conn))
        {
            long nonZ = (long)(cmd.ExecuteScalar() ?? 0L);
            Console.WriteLine($"\nClipViTB32 rows whose stored last_modified is NOT '...Z' (UTC-O): {nonZ:N0}");
        }

        using (var cmd = new SqliteCommand(
            "PRAGMA freelist_count; PRAGMA page_count; PRAGMA page_size;", conn))
        using (var rd = cmd.ExecuteReader())
        {
            long free = rd.Read() ? rd.GetInt64(0) : -1;
            long pages = rd.Read() ? rd.GetInt64(0) : -1;
            long size = rd.Read() ? rd.GetInt64(0) : -1;
            Console.WriteLine($"freelist: {free:N0} pages | file: {pages:N0} pages x {size} B");
        }

        var wal = realDb + "-wal";
        if (File.Exists(wal))
            Console.WriteLine($"WAL file: {new FileInfo(wal).Length / (1024.0 * 1024.0):N0} MB");
        return 0;
    }

    static Dictionary<string, float[]?> SeedResults(List<CachedRow> probe)
    {
        var res = new Dictionary<string, float[]?>(probe.Count);
        foreach (var e in probe) res[e.Path] = null;
        return res;
    }

    // verify against the generator's oracle; sample-check deserialized values
    static string Verify(Dictionary<string, float[]?> actual, Dataset ds)
    {
        int wrongClass = 0;
        var rnd = new Random(7);
        int sampled = 0;
        foreach (var e in ds.Probe)
        {
            bool expectHit = ds.ExpectedHits.Contains(e.Path);
            bool gotHit = actual.TryGetValue(e.Path, out var v) && v is not null;
            if (expectHit != gotHit)
            {
                wrongClass++;
                if (wrongClass > 5) return $"FAIL (class mismatches: {wrongClass}+)";
            }
            if (expectHit && gotHit && ds.CheckValues && sampled < 50 && rnd.Next(20) == 0)
            {
                sampled++;
                if (v![0] != (float)ds.RowIndex[e.Path])
                    return $"FAIL (value mismatch at {e.Path})";
            }
        }
        return "OK";
    }
}