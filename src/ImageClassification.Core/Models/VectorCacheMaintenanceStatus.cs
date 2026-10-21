namespace ImageClassification.Core.Models;

/// <summary>
/// Progress report of a vector-cache maintenance operation (prune / vacuum),
/// surfaced by the UI maintenance window. <see cref="Indeterminate"/> is true
/// while the operation has no predictable total (collection phase, VACUUM —
/// SQLite cannot report its progress).
/// </summary>
public sealed record VectorCacheMaintenanceStatus(
    string Phase,
    int Current,
    int Total,
    int MissingSoFar,
    bool Indeterminate);