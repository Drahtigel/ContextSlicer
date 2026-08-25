using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace ContextSlicer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            // 1. Сначала принудительно инициализируем язык интерфейса
            InitializeLanguage();

            // 2. Инициализируем компоненты формы и ViewModel
            InitializeComponent();
            DataContext = new MainViewModel();
        }

        private void InitializeLanguage()
        {
            // Определяем язык операционной системы Windows
            string currentCulture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

            // Выбираем нужный файл словаря (по умолчанию для всех — английский, для ru — русский)
            string languageFile = currentCulture.Equals("ru", StringComparison.OrdinalIgnoreCase)
                ? "Strings.ru.xaml"
                : "Strings.en.xaml";

            try
            {
                var dict = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Localization/{languageFile}", UriKind.Absolute)
                };

                // Добавляем строковые ресурсы напрямую в контекст этого окна
                this.Resources.MergedDictionaries.Add(dict);

                // Синхронизируем заголовок окна из загруженного словаря
                if (this.Resources.Contains("Str_AppTitle"))
                {
                    this.Title = this.Resources["Str_AppTitle"] as string;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load language file: {ex.Message}", "Language Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.OnWindowClosing();
            }
        }
    }
}
