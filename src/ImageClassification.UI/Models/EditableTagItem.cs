using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageClassification.UI.Models;

public partial class EditableTagItem : ObservableObject
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);

    [ObservableProperty]
    private BitmapImage? _thumbnail;

    public ObservableCollection<EditableTag> Tags { get; set; } = new();

    public void LoadThumbnail()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(FilePath);
            image.DecodePixelWidth = 80;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            Thumbnail = image;
        }
        catch { /* Skip unsupported formats */ }
    }
}

public partial class EditableTag : ObservableObject
{
    [ObservableProperty] private string _tag = string.Empty;
    [ObservableProperty] private float _score;
    [ObservableProperty] private bool _isSelected = true;

    public string ScoreText => $"{Score:P0}";
}
