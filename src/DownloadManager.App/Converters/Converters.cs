using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DownloadManager.App.Converters;

/// <summary>true (Arabic) -> RightToLeft, false -> LeftToRight.</summary>
public sealed class BoolToFlowDirectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
