using System;
using System.Globalization;
using System.Windows;

namespace ContextSlicer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Получаем текущий язык операционной системы Windows
            string currentCulture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

            // Задаем базовое имя файла локализации
            string themeFile = currentCulture.Equals("ru", StringComparison.OrdinalIgnoreCase)
                ? "Strings.ru.xaml"
                : "Strings.en.xaml";

            try
            {
                var dict = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Localization/{themeFile}", UriKind.Absolute)
                };

                // Внедряем выбранный языковой словарь в глобальные ресурсы приложения
                Resources.MergedDictionaries.Add(dict);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load localization file: {ex.Message}", "Localization Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
