using System;
using System.Globalization;
using System.Windows.Data;

namespace ContextSlicer;

// Конвертер для инверсии булевых значений (True <-> False) в разметке RadioButton
public class InverseBooleanConverter : IValueConverter
{
    // Статический экземпляр для прямого вызова через {x:Static} без регистрации в ресурсах
    public static readonly InverseBooleanConverter Instance = new InverseBooleanConverter();

    // Из ViewModel в Интерфейс (если IsPdfFormat == false, то Радиокнопка TXT нажимается в True)
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool booleanValue)
        {
            return !booleanValue;
        }
        return false;
    }

    // Из Интерфейса во ViewModel (если пользователь кликнул на TXT, во ViewModel IsPdfFormat пишется как False)
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool booleanValue)
        {
            return !booleanValue;
        }
        return false;
    }

    // Универсальный метод перекрашивания заголовка любого WPF-окна
    public static void ApplyTitleBarColor(System.Windows.Window window, bool isDark)
    {
        try
        {
            var interopHelper = new System.Windows.Interop.WindowInteropHelper(window);
            IntPtr hwnd = interopHelper.Handle;
            if (hwnd == IntPtr.Zero) return;

            int attribute = 35; // DWMWA_CAPTION_COLOR
            int colorBGR = isDark ? 0x1E1E1E : 0xF5F5F5;
            DwmSetWindowAttribute(hwnd, attribute, ref colorBGR, sizeof(int));

            int textAttribute = 36; // DWMWA_TEXT_COLOR
            int textColorBGR = isDark ? 0xF1F1F1 : 0x000000;
            DwmSetWindowAttribute(hwnd, textAttribute, ref textColorBGR, sizeof(int));
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

}
