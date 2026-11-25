using System.Globalization;
using System.Windows.Data;

namespace ADM.App;

/// <summary>A file name to the small icon Windows shows for its type, for the list's File name column.</summary>
public sealed class FileIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ShellInfo.SmallIconFor(value as string ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
