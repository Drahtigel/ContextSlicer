using System.Windows;

namespace ContextSlicer
{
    public partial class FilterWindow : Window
    {
        public FilterWindow()
        {
            InitializeComponent();
            DataContext = new FilterViewModel();

            // Окно фильтров автоматически подстраивается под состояние темы главного приложения
            this.SourceInitialized += (s, e) => {
                var mainWin = System.Windows.Application.Current.MainWindow as MainWindow;
                bool isDark = (mainWin?.DataContext as MainViewModel)?.IsDarkTheme ?? false;
                InverseBooleanConverter.ApplyTitleBarColor(this, isDark);
            };
        }


        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Просто закрываем окно — автосохранение списков в JSON происходит на лету
            this.Close();
        }
    }
}
