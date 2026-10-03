namespace ImageClassification.Core.Models;

/// <summary>
/// A single status update reported by <see cref="Services.IComicCoverSearchService"/> through
/// the unified <c>IProgress&lt;ComicSearchStatus&gt;</c> channel while a search is running.
/// The UI contract:
/// <list type="bullet">
/// <item><c>Detail != null</c> — a fully preformatted line (e.g. a per-archive / GPU-queue
/// snapshot); it is shown verbatim in the status line and takes precedence over <c>Phase</c>.</item>
/// <item><c>Phase != ""</c> and <c>Detail == null</c> — a coarse phase marker
/// ("Scanning archives...", "Processing archives...", ...); the UI combines it with the
/// progress counter when the total is known.</item>
/// <item><c>Current</c> / <c>Total</c> — progress-bar values (processed / found archives);
/// may accompany any of the above.</item>
/// </list>
/// The numeric <c>Gpu*</c> fields carry the GPU-side pipeline state (batches completed,
/// input-queue depth) for the detail lines.
/// </summary>
public sealed record ComicSearchStatus
{
    /// <summary>Coarse phase marker (empty for plain counter ticks and detail lines).</summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>Optional preformatted detail line shown verbatim (takes precedence over <see cref="Phase"/>).</summary>
    public string? Detail { get; init; }

    /// <summary>Progress-bar current value (processed archives). Null when not part of this update.</summary>
    public int? Current { get; init; }

    /// <summary>Progress-bar total (archives found). Null when not part of this update.</summary>
    public int? Total { get; init; }

    /// <summary>Number of image batches already run through the GPU encoder (processing phase only).</summary>
    public int? GpuBatchesDone { get; init; }

    /// <summary>Number of images waiting in the GPU input queue (processing phase only). 0 = GPU caught up.</summary>
    public int? GpuQueueDepth { get; init; }

    /// <summary>
    /// Archive-specific diagnostic (e.g. "ZIP container inside .cbr — re-extracted with the
    /// ZIP handler", or a bulk-pass fallback notice). Unlike <see cref="Detail"/> it is NOT
    /// rate-limited and NOT overwritten by progress ticks — the UI accumulates every note
    /// of the search in a visible list.
    /// </summary>
    public string? Note { get; init; }
}