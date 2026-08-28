using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ContextSlicer
{
    public partial class FilterViewModel : ObservableObject
    {
        // Прямые ссылки на глобальные ObservableCollection из нашего сервиса
        public ObservableCollection<string> ExcludedFolders => GlobalFilterService.Current.ExcludedFolders;
        public ObservableCollection<string> ExcludedExtensions => GlobalFilterService.Current.ExcludedExtensions;

        // Поля ввода для добавления новых элементов
        [ObservableProperty] private string _newFolderInput = string.Empty;
        [ObservableProperty] private string _newExtensionInput = string.Empty;

        // Выбранные элементы в списках (для удаления)
        [ObservableProperty] private string? _selectedFolder;
        [ObservableProperty] private string? _selectedExtension;

        // Команда добавления новой папки в черный список
        [RelayCommand]
        private void AddFolder()
        {
            if (string.IsNullOrWhiteSpace(NewFolderInput)) return;

            string cleanFolder = NewFolderInput.Trim();
            if (!ExcludedFolders.Contains(cleanFolder))
            {
                ExcludedFolders.Add(cleanFolder);
                GlobalFilterService.SaveFilters(); // Сразу пишем изменения в json
                NewFolderInput = string.Empty; // Очищаем поле ввода
            }
        }

        // Команда удаления папки из списка
        [RelayCommand]
        private void DeleteFolder()
        {
            if (SelectedFolder == null) return;

            ExcludedFolders.Remove(SelectedFolder);
            GlobalFilterService.SaveFilters();
            SelectedFolder = null;
        }

        // Команда добавления нового расширения в черный список
        [RelayCommand]
        private void AddExtension()
        {
            if (string.IsNullOrWhiteSpace(NewExtensionInput)) return;

            string cleanExt = NewExtensionInput.Trim();
            // Если пользователь забыл поставить точку в начале (например, написал "bak"), ставим её принудительно
            if (!cleanExt.StartsWith("."))
            {
                cleanExt = "." + cleanExt;
            }

            if (!ExcludedExtensions.Contains(cleanExt))
            {
                ExcludedExtensions.Add(cleanExt);
                GlobalFilterService.SaveFilters();
                NewExtensionInput = string.Empty;
            }
        }

        // Команда удаления расширения из списка
        [RelayCommand]
        private void DeleteExtension()
        {
            if (SelectedExtension == null) return;

            ExcludedExtensions.Remove(SelectedExtension);
            GlobalFilterService.SaveFilters();
            SelectedExtension = null;
        }
    }
}
