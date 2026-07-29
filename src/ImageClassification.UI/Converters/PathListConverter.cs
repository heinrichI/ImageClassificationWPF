using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace ImageClassification.UI.Converters;

public class PathListConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is List<string> paths)
        {
            int show = Math.Min(paths.Count, 3);
            var names = paths.Take(show).Select(p => Path.GetFileName(p)).ToList();
            string result = string.Join(", ", names);
            if (paths.Count > show)
                result += $" ... (+{paths.Count - show} more)";
            return result;
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
