using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ImageClassification.UI.ViewModels;

/// <summary>
/// Pure ViewModel for the Comic Cover Search tab.
/// No code-behind logic — all commands and bindings are here.
/// </summary>
public partial class ComicCoverSearchViewModel : ObservableObject
{
    private readonly IComicCoverSearchService _searchService;
    private readonly ILogger<ComicCoverSearchViewModel> _logger;
    private CancellationTokenSource? _cts;

    public ComicCoverSearchViewModel(IComicCoverSearchService searchService, ILogger<ComicCoverSearchViewModel> logger)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ────────────────────────────── Observable properties ──────────────────────────────

    [ObservableProperty]
    private string _directoryPath = string.Empty;

    [ObservableProperty]
    private string _queryText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private float _similarityThreshold = 0.25f;

    [ObservableProperty]
    private int _progressCurrent;
    partial void OnProgressCurrentChanged(int value) => OnPropertyChanged(nameof(ProgressPercent));

    [ObservableProperty]
    private int _progressTotal;
    partial void OnProgressTotalChanged(int value) => OnPropertyChanged(nameof(ProgressPercent));

    public float ProgressPercent => ProgressTotal > 0
        ? (ProgressCurrent / (float)ProgressTotal) * 100f
        : 0f;

    public ObservableCollection<ComicCoverItem> Results { get; } = new();

    // ────────────────────────────── Commands ──────────────────────────────

    [RelayCommand]
    private void BrowseDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select directory with comic archives (CBZ/CBR)"
        };

        if (dialog.ShowDialog() == true)
        {
            DirectoryPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        // Validate inputs
        if (string.IsNullOrWhiteSpace(DirectoryPath))
        {
            StatusMessage = "Please select a directory with comic archives first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(QueryText))
        {
            StatusMessage = "Please enter a search query (e.g. 'beach', 'summer').";
            return;
        }

        if (IsBusy)
            return;

        IsBusy = true;
        StatusMessage = "Searching...";
        Results.Clear();
        ProgressCurrent = 0;
        ProgressTotal = 0;

        try
        {
            // Cancel any previous operation
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            var progress = new Progress<(int Current, int Total)>(p =>
            {
                ProgressCurrent = p.Current;
                ProgressTotal = p.Total;
            });

            var results = await _searchService.SearchAsync(
                DirectoryPath,
                QueryText,
                progress,
                ct);

            // Filter by similarity threshold
            var filtered = results.Where(r => r.SimilarityScore >= SimilarityThreshold).ToList();

            // Populate the observable collection
            foreach (var result in filtered)
            {
                Results.Add(new ComicCoverItem
                {
                    ArchivePath = result.ArchivePath,
                    ArchiveFileName = result.ArchiveFileName,
                    CoverImagePath = result.CoverImagePath,
                    SimilarityScore = result.SimilarityScore
                });
            }

            StatusMessage = Results.Count > 0
                ? $"Found {Results.Count} matching covers (filtered from {results.Count} total, scores: {filtered.Min(r => r.SimilarityScore):P1}–{filtered.Max(r => r.SimilarityScore):P1}, median: {filtered.OrderBy(r => r.SimilarityScore).ElementAt(filtered.Count / 2).SimilarityScore:P1})"
                : "No matching covers found.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Search cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed: {Message}", ex.Message);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressCurrent = 0;
            ProgressTotal = 0;
        }
    }

    [RelayCommand]
    private void OpenComic(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(archivePath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot open file {ArchivePath}: {Message}", archivePath, ex.Message);
            StatusMessage = $"Cannot open file: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusMessage = "Cancelling...";
    }
}

/// <summary>
/// ViewModel item for a single comic cover result in the list.
/// </summary>
public partial class ComicCoverItem : ObservableObject
{
    [ObservableProperty]
    private string _archivePath = string.Empty;

    [ObservableProperty]
    private string _archiveFileName = string.Empty;

    [ObservableProperty]
    private string _coverImagePath = string.Empty;

    [ObservableProperty]
    private float _similarityScore;

    public string SimilarityText => $"{SimilarityScore:P1}";
}