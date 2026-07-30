using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ImageClassification.UI.Converters;

/// <summary>
/// Converts a similarity score (0..1) to a color brush:
/// high scores → green, medium → orange, low → gray.
/// </summary>
public class ScoreToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is float score)
        {
            // Thresholds calibrated for CLIP raw cosine similarity ranges
            // matched pairs: 0.27-0.37, unmatched: 0.15-0.25
            if (score >= 0.28f)
                return new SolidColorBrush(Colors.Green);
            if (score >= 0.20f)
                return new SolidColorBrush(Colors.Orange);
            return new SolidColorBrush(Colors.Gray);
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}