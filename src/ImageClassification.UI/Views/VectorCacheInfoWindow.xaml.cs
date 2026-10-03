using System.Globalization;
using System.Windows;
using ImageClassification.Core.Models;

namespace ImageClassification.UI.Views;

/// <summary>
/// Modal info dialog about the vector cache (opened from the Comic Search menu).
/// Presentation only — all data arrives as an immutable <see cref="VectorCacheInfo"/> snapshot.
/// </summary>
public partial class VectorCacheInfoWindow : Window
{
    public VectorCacheInfoWindow(VectorCacheInfo info)
    {
        InitializeComponent();

        PathText.Text = string.IsNullOrEmpty(info.DatabasePath)
            ? "<in-memory store — no file>"
            : info.DatabasePath;

        DbSizeText.Text = FormatBytes(info.DatabaseFileSizeBytes);
        WalSizeText.Text = FormatBytes(info.WalFileSizeBytes);
        TotalText.Text = info.TotalEntries.ToString("N0");
        CoversText.Text = info.CoversEntries.ToString("N0");
        AllPagesText.Text = info.AllPagesEntries.ToString("N0");
        ModelsText.Text = string.IsNullOrEmpty(info.Models) ? "(none)" : info.Models;
        OldestText.Text = info.OldestEntryUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
        NewestText.Text = info.NewestEntryUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var units = new[] { "KB", "MB", "GB" };
        double value = bytes;
        int unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);
        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}