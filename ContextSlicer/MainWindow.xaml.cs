using ContextSlicer.Filesystem;
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

namespace ContextSlicer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeLanguage();
        InitializeComponent();
        DataContext = new MainViewModel();

        // Красим заголовок главного окна при старте (считываем текущее состояние темы из App)
        this.SourceInitialized += (s, e) => {
            bool isDark = (DataContext as MainViewModel)?.IsDarkTheme ?? false;
            InverseBooleanConverter.ApplyTitleBarColor(this, isDark);
        };
    }

    // Импортируем функцию из системной библиотеки Windows для управления внешним видом окон
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // Публичный метод, который сможет вызывать наша ViewModel при смене темы
    public void UpdateTitleBarColor(bool isDark)
    {
        try
        {
            var interopHelper = new System.Windows.Interop.WindowInteropHelper(this);
            IntPtr hwnd = interopHelper.Handle;

            if (hwnd == IntPtr.Zero) return;

            // В Windows 11 константа 35 отвечает за цвет фона заголовка (DWMWA_CAPTION_COLOR)
            int attribute = 35;

            // Переводим HEX-цвета в формат BGR, который понимает Windows:
            // #1E1E1E (темный) превращается в 0x1E1E1E
            // #F5F5F5 (светлый) превращается в 0xF5F5F5
            int colorBGR = isDark ? 0x1E1E1E : 0xF5F5F5;

            DwmSetWindowAttribute(hwnd, attribute, ref colorBGR, sizeof(int));

            // Дополнительно меняем цвет текста заголовка (DWMWA_TEXT_COLOR = 36), чтобы он оставался контрастным
            int textAttribute = 36;
            int textColorBGR = isDark ? 0xF1F1F1 : 0x000000;
            DwmSetWindowAttribute(hwnd, textAttribute, ref textColorBGR, sizeof(int));
        }
        catch { /* Игнорируем на старых версиях Windows 10, где этот атрибут не поддерживается */ }
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

    private void TxtProjectName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.UpdateCurrentProjectNameCommand.CanExecute(null))
        {
            vm.UpdateCurrentProjectNameCommand.Execute(null);
        }
    }


    // Обработчик ручного раскрытия узла дерева (Ленивая загрузка)
    private async void TreeView_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is FileSystemNode node)
        {
            // Строгое условие: парсим только если внутри РЕАЛЬНО лежит наша текстовая заглушка
            if (node.Children.Any(c => c.Name == "LoadingStub..."))
            {
                if (DataContext is MainViewModel vm)
                {
                    try
                    {
                        // Метод удалит LoadingStub, вызовет ParseFileAsync и подселит главы-листья (IsFile=true)
                        await vm.PopulateSyntaxNodesAsync(node);
                        node.VerifyCheckState();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TreeView Expand Error] {ex.Message}");
                    }
                }
            }
        }
    }

    private void TxtModuleName_TextChanged(object sender, TextChangedEventArgs e)
    {

    }

    private void ListBox_PreviewRightMouseButtonDown(object sender, MouseButtonEventArgs e)
    {
        var listBox = sender as ListBox;
        if (listBox == null) return;

        // Шаг 1. Находим контейнер строки (ListBoxItem), над которым находится курсор
        DependencyObject dep = (DependencyObject)e.OriginalSource;
        while (dep != null && dep != listBox && !(dep is ListBoxItem))
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        // Отсечка: клик по пустому месту — игнорируем
        if (!(dep is ListBoxItem item))
        {
            e.Handled = true;
            return;
        }

        // Шаг 2. Принудительно выделяем строку проекта в UI
        item.IsSelected = true;

        // Шаг 3. ИСПРАВЛЕНО: Безопасный поиск конкретного пункта меню по имени через контекст
        if (item.ContextMenu != null && item.DataContext is ProjectConfig project)
        {
            // Перебираем элементы коллекции Items и ищем нужный нам MenuItem
            foreach (var menuObject in item.ContextMenu.Items)
            {
                if (menuObject is MenuItem mnuItem && mnuItem.Name == "MnuRecreateGoogle")
                {
                    // Пункт отображается только для проектов типа GoogleDoc
                    mnuItem.Visibility = (project.Type == ProjectType.GoogleDoc)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    break; // Элемент найден, прерываем цикл
                }
            }
        }
    }

    /// <summary>
    /// Обработчик пункта меню "Обновить структуру"
    /// </summary>
    // Внутри MainWindow.xaml.cs

    /// <summary>
    /// ИСПРАВЛЕНО: Сигнатура приведена к универсальному RoutedEventArgs для полной совместимости с автогенератором WPF
    /// </summary>
    private async void MenuUpdate_Click(object sender, RoutedEventArgs e)
    {
        // Получаем нашу ViewModel из DataContext окна
        if (DataContext is MainViewModel viewModel)
        {
            // ИСПРАВЛЕНО: Безопасно вытаскиваем путь к текущему проекту.
            // Так как тип обновления зависит от источника данных, мы проверяем расширение или тип.
            // Для сценария ReadDirectory (вычитка каталогов кода/файлов):

            // В вашей конфигурации путь обычно лежит в настройках или выбранном проекте.
            // Передаем путь к корневой папке проекта:
            string projectPath = viewModel.SelectedProject?.RootPath ?? string.Empty;

            // Если у вас свойство называется по-другому (например, просто RootPath), 
            // парсер try-catch оверлея всё равно отработает сбои, если путь окажется пустым.

            await viewModel.StartProjectUpdateAsync(
                ContextSlicer.Filesystem.UpdateType.ReadDirectory,
                projectPath
            );
        }
    }


 
    /// <summary>
    /// ИСПРАВЛЕНО: Универсальная сигнатура клика для пункта "Удалить"
    /// </summary>
    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as MainViewModel;
        if (vm != null && vm.DeleteCurrentProjectCommand.CanExecute(null))
        {
            vm.DeleteCurrentProjectCommand.Execute(null);
        }
    }


}
