namespace ImageClassification.Core.Models;

/// <summary>
/// Outcome of a vector-cache maintenance operation ("optimize" = WAL checkpoint, "vacuum" = full rebuild).
/// </summary>
public sealed record VectorCacheMaintenanceResult(string Operation, bool Success, string Detail);