namespace ImageClassification.Core.Models;

/// <summary>
/// Outcome of a vector-cache maintenance operation ("optimize" = WAL checkpoint, "vacuum" = full
/// rebuild, "prune" = removal of entries for files missing on disk).
/// </summary>
public sealed record VectorCacheMaintenanceResult(string Operation, bool Success, string Detail);