using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageClassification.UI.Services;

namespace ImageClassification.UI.Models;

public partial class EditableTagItem : ObservableObject
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    public ObservableCollection<EditableTag> Tags { get; set; } = new();

    public void LoadThumbnail(ThumbnailProvider provider)
    {
        if (string.IsNullOrEmpty(FilePath)) return;

        var result = provider.GetBitmap(FilePath, 80, r => { if (r is not null && r.Value.Image is not null) Thumbnail = r.Value.Image; });
        if (result is not null && result.Value.Image is not null) Thumbnail = result.Value.Image;
    }
}

public partial class EditableTag : ObservableObject
{
    [ObservableProperty] private string _tag = string.Empty;
    [ObservableProperty] private float _score;
    [ObservableProperty] private bool _isSelected = true;

    public string ScoreText => $"{Score:P0}";
}
