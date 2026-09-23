using System;
using System.Globalization;
using System.Windows.Data;

namespace ContextSlicer.Filesystem;

public class NullToBoolConverter : IValueConverter
{
    // Если объект НЕ null и НЕ пустая строка — возвращаем true (кнопка активна)
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            return !string.IsNullOrWhiteSpace(str);
        }
        return value != null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
