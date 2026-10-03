namespace ImageClassification.Core.Models;

/// <summary>
/// Snapshot of the vector cache state, for informational display (menu → "Vector cache: Info").
/// All timestamps are UTC. File sizes are -1 when the backing file does not exist (in-memory store).
/// </summary>
public sealed record VectorCacheInfo(
    string? DatabasePath,
    long DatabaseFileSizeBytes,
    long WalFileSizeBytes,
    long TotalEntries,
    long CoversEntries,
    long AllPagesEntries,
    DateTime? OldestEntryUtc,
    DateTime? NewestEntryUtc,
    string Models);