namespace ImageClassification.Core.Models;

/// <summary>
/// Progress/diagnostics reported by the image search pipeline.
/// Plain progress + human-readable notes only — no GPU/batch fields, because
/// the per-file pipeline of <c>ImageSearchService</c> has none (ISP: this type
/// is deliberately NOT <see cref="ComicSearchStatus"/>).
/// </summary>
public sealed record ImageSearchStatus
{
    /// <summary>Human-readable phase marker (e.g. "Found 1234 images", "Scoring and ranking results...").</summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>Full preformatted detail line, shown verbatim on the status bar.</summary>
    public string? Detail { get; init; }

    /// <summary>Completed item count (set on progress ticks).</summary>
    public int? Current { get; init; }

    /// <summary>Total item count, when known (set on progress ticks and phase markers).</summary>
    public int? Total { get; init; }

    /// <summary>Rare diagnostic note (e.g. a file that could not be encoded), shown in the notes list.</summary>
    public string? Note { get; init; }

    /// <summary>Full path of the file the pipeline started processing most recently (empty when idle).</summary>
    public string? CurrentFile { get; init; }
}