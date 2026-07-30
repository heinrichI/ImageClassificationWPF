using System.Globalization;
using System.Windows.Data;

namespace ImageClassification.UI.Converters;

/// <summary>
/// Returns true when the int value is 0 (useful for IsIndeterminate of ProgressBar).
/// </summary>
public class ZeroToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int i)
            return i == 0;
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}