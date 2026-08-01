namespace ImageClassification.Core.Models;

/// <summary>
/// Represents the result of a comic cover similarity search.
/// </summary>
public class ComicCoverResult
{
    /// <summary>Full path to the archive file (.cbz/.cbr).</summary>
    public string ArchivePath { get; set; } = string.Empty;

    /// <summary>File name of the archive (e.g. "spiderman-001.cbz").</summary>
    public string ArchiveFileName { get; set; } = string.Empty;

    /// <summary>Full path to the extracted cover image temp file.</summary>
    public string CoverImagePath { get; set; } = string.Empty;

    /// <summary>Cosine similarity score between the cover embedding and the text query.</summary>
    public float SimilarityScore { get; set; }

    /// <summary>Formatted similarity percentage for display.</summary>
    public string SimilarityText => $"{SimilarityScore:P1}";

    /// <summary>Zero-based page index within the archive (0 = first page).</summary>
    public int PageIndex { get; set; }

    /// <summary>Total number of image pages in the archive.</summary>
    public int PageCount { get; set; }

    /// <summary>Display label for the page (e.g. "Page 1 of 61").</summary>
    public string PageLabel => PageCount > 0 ? $"Page {PageIndex + 1} of {PageCount}" : string.Empty;
}