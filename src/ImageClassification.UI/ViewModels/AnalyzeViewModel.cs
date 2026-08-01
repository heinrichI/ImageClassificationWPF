using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.UI.Models;
using ImageClassification.UI.Services;
using Microsoft.Win32;

namespace ImageClassification.UI.ViewModels;

public partial class AnalyzeViewModel : ObservableObject
{
    private readonly IImageFeatureExtractor _featureExtractor;
    private readonly IClusterService _clusterService;
    private readonly ITagService _tagService;
    private readonly ThumbnailProvider _thumbnailProvider;
    private CancellationTokenSource? _cts;

    public AnalyzeViewModel(
        IImageFeatureExtractor featureExtractor,
        IClusterService clusterService,
        ITagService tagService,
        ThumbnailProvider thumbnailProvider)
    {
        _featureExtractor = featureExtractor;
        _clusterService = clusterService;
        _tagService = tagService;
        _thumbnailProvider = thumbnailProvider;
    }

    // Common
    [ObservableProperty] private string _sourceDirectory = string.Empty;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private float _progressPercent;
    [ObservableProperty] private string _currentFileText = string.Empty;

    // Mode selection
    [ObservableProperty] private bool _isClusterMode = true;

    // Cluster mode
    [ObservableProperty] private int _numClusters = 5;
    [ObservableProperty] private bool _autoDetectClusters = true;
    [ObservableProperty] private string _detectedKText = string.Empty;

    // Tag mode
    [ObservableProperty] private string _candidateTags = "person, animal, landscape, building, food, vehicle, document, art, nature, indoor";
    [ObservableProperty] private int _topK = 5;
    [ObservableProperty] private float _tagConfidenceThreshold = 0.3f;

    // Results
    public ObservableCollection<ClusterInfo> Clusters { get; } = new();
    public ObservableCollection<ImageTagResult> TagResults { get; } = new();
    public ObservableCollection<EditableTagItem> EditableTagResults { get; } = new();

    [RelayCommand]
    private void BrowseSourceDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Select Image Directory" };
        if (dialog.ShowDialog() == true)
            SourceDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrEmpty(SourceDirectory))
        {
            StatusText = "Please select a source directory";
            return;
        }

        IsProcessing = true;
        StatusText = "Loading model...";
        Clusters.Clear();
        TagResults.Clear();

        _cts = new CancellationTokenSource();

        try
        {
            var imagePaths = Directory.GetFiles(SourceDirectory)
                .Where(f => IsImageFile(f))
                .ToList();

            if (imagePaths.Count == 0)
            {
                StatusText = "No images found in directory";
                return;
            }

            StatusText = $"Found {imagePaths.Count} images. Extracting features...";
            var progress = new Progress<int>(p =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ProgressPercent = (float)p / imagePaths.Count * 100;
                    CurrentFileText = $"Processing image {p}/{imagePaths.Count}";
                });
            });

            // Extract features (model loaded lazily inside service)
            var features = await _featureExtractor.ExtractBatchAsync(imagePaths, progress);

            var featureArray = features.Select(f => f.Features).ToArray();

            if (IsClusterMode)
            {
                await RunClusteringAsync(imagePaths, featureArray);
            }
            else
            {
                await RunTagGenerationAsync(imagePaths);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task RunClusteringAsync(List<string> imagePaths, float[][] featureArray)
    {
        StatusText = "Clustering...";

        int k = AutoDetectClusters
            ? await Task.Run(() => _clusterService.FindOptimalK(featureArray))
            : NumClusters;

        DetectedKText = AutoDetectClusters ? $"Auto-detected: {k} clusters" : "";

        var result = await Task.Run(() => _clusterService.Cluster(featureArray, k));

        Application.Current.Dispatcher.Invoke(() =>
        {
            Clusters.Clear();
            for (int c = 0; c < result.NumClusters; c++)
            {
                var indices = result.ImageIndicesPerCluster.ContainsKey(c)
                    ? result.ImageIndicesPerCluster[c]
                    : new List<int>();

                Clusters.Add(new ClusterInfo
                {
                    Id = c,
                    SuggestedName = $"Cluster {c + 1}",
                    ImageCount = indices.Count,
                    ImagePaths = indices.Select(i => imagePaths[i]).ToList()
                });
            }

            StatusText = $"Found {k} clusters with {imagePaths.Count} total images";
        });

        await Task.CompletedTask;
    }

    private async Task RunTagGenerationAsync(List<string> imagePaths)
    {
        StatusText = "Generating tags...";

        var tags = CandidateTags
            .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToArray();

        StatusText = $"Generating tags with {tags.Length} candidates...";

        var progress = new Progress<int>(p =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ProgressPercent = (float)p / imagePaths.Count * 100;
                CurrentFileText = $"Tagging image {p}/{imagePaths.Count}";
            });
        });

        var results = await _tagService.GenerateTagsAsync(imagePaths, tags, TopK, progress);

        Application.Current.Dispatcher.Invoke(() =>
        {
            TagResults.Clear();
            EditableTagResults.Clear();
            foreach (var result in results)
            {
                TagResults.Add(result);

                var editableItem = new EditableTagItem { FilePath = result.FilePath };
                foreach (var (tag, score) in result.Tags)
                {
                    editableItem.Tags.Add(new EditableTag { Tag = tag, Score = score, IsSelected = score >= TagConfidenceThreshold });
                }
                editableItem.LoadThumbnail(_thumbnailProvider);
                EditableTagResults.Add(editableItem);
            }

            StatusText = $"Tagged {results.Count} images with {tags.Length} candidate tags. Review and edit, then Sort.";
        });
    }

    [RelayCommand]
    private async Task SortByClustersAsync()
    {
        if (Clusters.Count == 0) return;

        IsProcessing = true;
        StatusText = "Sorting by clusters...";

        try
        {
            int sorted = 0;
            foreach (var cluster in Clusters)
            {
                var targetDir = Path.Combine(SourceDirectory, cluster.SuggestedName);
                Directory.CreateDirectory(targetDir);

                foreach (var imagePath in cluster.ImagePaths)
                {
                    var targetPath = Path.Combine(targetDir, Path.GetFileName(imagePath));
                    File.Copy(imagePath, targetPath, overwrite: true);
                    sorted++;
                }
            }

            StatusText = $"Sorted {sorted} images into {Clusters.Count} clusters";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task SortByTagsAsync()
    {
        if (EditableTagResults.Count == 0) return;

        IsProcessing = true;
        StatusText = "Sorting by tags...";

        try
        {
            int sorted = 0;
            var tagCounts = new Dictionary<string, int>();

            foreach (var imageItem in EditableTagResults)
            {
                foreach (var tag in imageItem.Tags.Where(t => t.IsSelected))
                {
                    var targetDir = Path.Combine(SourceDirectory, tag.Tag);
                    Directory.CreateDirectory(targetDir);

                    var targetPath = Path.Combine(targetDir, Path.GetFileName(imageItem.FilePath));
                    File.Copy(imageItem.FilePath, targetPath, overwrite: true);
                    sorted++;

                    if (!tagCounts.ContainsKey(tag.Tag))
                        tagCounts[tag.Tag] = 0;
                    tagCounts[tag.Tag]++;
                }
            }

            StatusText = $"Sorted {sorted} image-tag pairs into {tagCounts.Count} tag folders";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tiff" or ".webp";
    }
}
