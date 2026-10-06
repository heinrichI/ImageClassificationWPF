using ImageClassification.VectorStore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ImageClassification.Tests.Services;

/// <summary>
/// Opt-in harness: opens the real store (which runs the one-time legacy migration)
/// against a database file supplied via the ICS_MIGRATE_DB environment variable,
/// timing the whole operation. Skips silently when the variable is unset.
/// </summary>
public sealed class VectorStoreRealDbMigrationHarness
{
    private readonly ITestOutputHelper _output;

    public VectorStoreRealDbMigrationHarness(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MigrateDbFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("ICS_MIGRATE_DB");
        if (string.IsNullOrWhiteSpace(path)) return; // opt-in only

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var store = new SqliteVectorStore(path, NullLogger<SqliteVectorStore>.Instance);
        // The migration now runs on a background thread; wait for it explicitly.
        await store.WhenReady;
        sw.Stop();
        _output.WriteLine($"[harness] store initialized + migration done in {sw.Elapsed} for {path}");
    }
}