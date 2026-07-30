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
}