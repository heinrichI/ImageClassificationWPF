namespace ImageClassification.Core.Models;

/// <summary>
/// Image search options. Kept as a record (not a flag argument on the port) so new
/// options can be added without changing the <c>IImageSearchService</c> signature.
/// </summary>
public sealed record ImageSearchOptions
{
    /// <summary>
    /// Include files from subdirectories. Default: true (a "search directories"
    /// feature is expected to cover the whole tree; the user opts out in the UI).
    /// </summary>
    public bool RecurseSubfolders { get; init; } = true;
}