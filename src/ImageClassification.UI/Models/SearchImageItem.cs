using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageClassification.UI.Models;

/// <summary>
/// ViewModel item for a single image search result tile in the results grid.
/// </summary>
public class SearchImageItem : ObservableObject
{
    private string _filePath = string.Empty;
    /// <summary>Full path of the image file (used to open/copy the file).</summary>
    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    private string _fileName = string.Empty;
    /// <summary>File name (shown under the tile).</summary>
    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    private BitmapSource? _thumbnail;
    /// <summary>Decoded preview; starts as a placeholder until the tile becomes visible.</summary>
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    private float _similarityScore;
    /// <summary>Cosine similarity of the query and image embeddings (0..1).</summary>
    public float SimilarityScore
    {
        get => _similarityScore;
        set => SetProperty(ref _similarityScore, value);
    }

    /// <summary>Percent-formatted similarity for display (e.g. "53.2%").</summary>
    public string SimilarityText => $"{SimilarityScore:P1}";
}