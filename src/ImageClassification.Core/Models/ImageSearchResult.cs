namespace ImageClassification.Core.Models;

/// <summary>
/// A single match returned by the image search: one plain image file ranked
/// by CLIP cosine similarity against the text query.
/// </summary>
public class ImageSearchResult
{
    /// <summary>Full path of the image file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>File name (for display in the results grid).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Cosine similarity between the query and image embeddings (0..1 for
    /// L2-normalized embeddings).
    /// </summary>
    public float SimilarityScore { get; set; }

    /// <summary>Percent-formatted similarity for display (e.g. "53.2%").</summary>
    public string SimilarityText => $"{SimilarityScore:P1}";
}