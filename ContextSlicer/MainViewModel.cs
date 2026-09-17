using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.WindowsAPICodePack.Dialogs;
using Newtonsoft.Json;
using PdfSharp.Fonts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using MigraDoc;

namespace ContextSlicer;

public partial class MainViewModel : ObservableObject
{
    private readonly string _configFilePath;
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private ObservableCollection<ProjectConfig> _projects = new();
    [ObservableProperty] private ProjectConfig? _selectedProject;
    [ObservableProperty] private bool _isAutoSaveEnabled;
    // Список модулей выбранного проекта
    [ObservableProperty] private bool _isPdfFormat; // Если true — рендерим в PDF, если false — в TXT
    [ObservableProperty] private bool _isProjectBlockExpanded;
    [ObservableProperty] private bool _isProjectNameInvalid;
    [ObservableProperty] private bool _isModuleNameInvalid;
    [ObservableProperty] private bool _includeDirectoryStructure = true;
    [ObservableProperty] private FileSystemNode? _rootNode;
    [ObservableProperty] private string _moduleRules = string.Empty;

    // Поля ввода UI
    [ObservableProperty] private string _projectNameInput = string.Empty;
    [ObservableProperty] private string _rootPath = string.Empty;
    [ObservableProperty] private string _outputPath = string.Empty;
    [ObservableProperty] private string _promptRules = string.Empty;

    [ObservableProperty] private string _moduleNameInput = string.Empty;
    [ObservableProperty] private string _contextFileName = string.Empty;
    [ObservableProperty]
    private ObservableCollection<ContextModule> _modules = new();

    // Внимательно проверьте написание этой переменной:
    [ObservableProperty]
    private ContextModule? _selectedModule;
    // Одна универсальная команда для контекстного меню TreeView
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void AddNodeToFilters(FileSystemNode? node)
    {
        if (node == null) return;

        if (node.IsFile)
        {
            // Если кликнули по файлу — отправляем его расширение в фильтр
            string ext = System.IO.Path.GetExtension(node.FullPath).ToLower();
            if (!string.IsNullOrEmpty(ext) && !GlobalFilterService.Current.ExcludedExtensions.Contains(ext))
            {
                GlobalFilterService.Current.ExcludedExtensions.Add(ext);
                GlobalFilterService.SaveFilters();
                TriggerTreeRefresh();
            }
        }
        else
        {
            // Если кликнули по папке — отправляем её имя в фильтр
            string folderName = node.Name;
            if (!GlobalFilterService.Current.ExcludedFolders.Contains(folderName))
            {
                GlobalFilterService.Current.ExcludedFolders.Add(folderName);
                GlobalFilterService.SaveFilters();
                TriggerTreeRefresh();
            }
        }
    }

    // Вспомогательный метод перезагрузки дерева файлов (оставляем старый)
    // Вспомогательный метод перезагрузки дерева файлов (ИСПРАВЛЕНО: Полный принудительный пересчет)
    private void TriggerTreeRefresh()
    {
        if (SelectedProject != null && !string.IsNullOrWhiteSpace(RootPath))
        {
            // Запускаем стандартный метод сборки дерева, который у вас вызывается при выборе папки.
            // Передаем текущий корневой путь и список уже сохраненных чекнутых файлов модуля
            var savedChecked = new List<string>();
            if (SelectedModule?.CheckedFiles != null)
            {
                savedChecked = new List<string>(SelectedModule.CheckedFiles);
            }

            // Перестраиваем структуру дерева с учетом НОВЫХ глобальных фильтров
            RootNode = ContextBuilderService.BuildTree(RootPath, savedChecked);

            // Уведомляем интерфейс WPF, что дерево файлов полностью обновилось
            OnPropertyChanged(nameof(RootNode));
        }
    }


    [RelayCommand]
    private void OpenFiltersWindow()
    {
        var filterWin = new FilterWindow();
        // Устанавливаем главное окно владельцем, чтобы новое окно красиво центрировалось поверх него
        filterWin.Owner = System.Windows.Application.Current.MainWindow;

        // ShowDialog() полностью блокирует поток выполнения до тех пор, пока пользователь не закроет окно фильтров.
        // Как только пользователь нажмет кнопку «ЗАКРЫТЬ НАСТРОЙКИ» или крестик — код пойдет дальше.
        filterWin.ShowDialog();

        // ИСПРАВЛЕНО: Прямо здесь вызываем наш железно работающий метод полной пересборки дерева файлов.
        // Это заставит утилиту мгновенно убрать с экрана все только что добавленные папки/расширения 
        // или вернуть обратно те, что пользователь вручную удалил из списков!
        TriggerTreeRefresh();
    }



    // Свойство доступности блока модулей (теперь со строгим уведомлением для интерфейса)
    // Настраиваемый черный список папок (дефолтные значения)
    [ObservableProperty]
    private string _excludedFoldersInput = "bin, obj, .vs, publish, .git, .idea, node_modules";

    // Настраиваемый черный список мусорных и бинарных расширений (дефолтные значения)
    [ObservableProperty]
    private string _excludedExtensionsInput = ".png, .jpg, .jpeg, .gif, .ico, .bmp, .webp, .mp3, .wav, .ogg, .flac, .aac, .mp4, .avi, .mkv, .mov, .zip, .rar, .7z, .tar, .gz, .dll, .exe, .pdb, .suo, .user, .fbx, .obj, .max, .blend, .3ds, .bak, .tmp, .temp, .log";

    [RelayCommand]
    private void DeleteCurrentModule()
    {
        if (SelectedModule == null) return;

        // Спрашиваем подтверждение удаления на языке системы
        var result = System.Windows.MessageBox.Show(
            "Вы уверены, что хотите полностью удалить этот модуль и все его сохраненные настройки файлов?",
            "Удаление модуля",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            var moduleToRemove = SelectedModule;
            SelectedModule = null; // Сбрасываем выбор

            Modules.Remove(moduleToRemove);
            OnPropertyChanged(nameof(IsModuleSelectorEnabled));

            // Автоматически сохраняем изменения на диск
            SaveProject();
        }
    }

    public bool IsModuleSelectorEnabled
    {
        get => SelectedProject != null;
    }

    // Свойство для динамического вывода имени проекта в заголовок Expander
    // Динамическое свойство для заголовка экспандера (Исключает наложение текста)
    public string DisplayProjectName
    {
        get
        {
            // Если проект выбран и у него есть имя — выводим его
            if (SelectedProject != null && !string.IsNullOrWhiteSpace(SelectedProject.ProjectName))
            {
                return SelectedProject.ProjectName;
            }

            // Если проект не выбран — безопасно вытаскиваем локализованную строку из ресурсов окна
            if (Application.Current?.MainWindow?.Resources != null &&
                Application.Current.MainWindow.Resources.Contains("Str_ProjNotSelected"))
            {
                return Application.Current.MainWindow.Resources["Str_ProjNotSelected"] as string ?? "---";
            }

            // Фолбэк на случай, если ресурсы еще не успели прогрузиться при самом первом старте
            return "---";
        }
    }


    public MainViewModel()
    {
        // Точный и правильный путь к настройке шрифтов Windows в PDFsharp/MigraDoc
        PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true;

        // Оставляем провайдер кодировок для кириллицы
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // ... остальной код конструктора без изменений ...
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDir = Path.Combine(appData, "ContextSlicer");
        Directory.CreateDirectory(appDir);
        _configFilePath = Path.Combine(appDir, "projects.json");
        IncludeDirectoryStructure = Properties.Settings.Default.IncludeDirectoryStructure;

        IsAutoSaveEnabled = Properties.Settings.Default.AutoSaveOnExit;
        IsDarkTheme = Properties.Settings.Default.IsDarkTheme;
        IsProjectBlockExpanded = Properties.Settings.Default.IsProjectBlockExpanded;
        OnIsDarkThemeChanged(IsDarkTheme);

        LoadProjects();
    }


    // Логика при выборе ПРОЕКТА
    partial void OnSelectedProjectChanged(ProjectConfig? value)
    {
        if (value != null)
        {
            ProjectNameInput = value?.ProjectName ?? string.Empty;
            RootPath = value.RootPath;
            OutputPath = value.OutputPath;
           
            PromptRules = value.PromptRules;
            IncludeDirectoryStructure = value.IncludeDirectoryStructure; // ЧИТАЕМ НАСТРОЙКУ


            // Загружаем список модулей этого проекта
            Modules = new ObservableCollection<ContextModule>(value.Modules);

            // Перестраиваем дерево папок базово (без выбранных файлов)
            RefreshTreeView(new List<string>());

            // Выбираем первый модуль, если он есть
            SelectedModule = Modules.FirstOrDefault();
        }
        else
        {
            Modules.Clear();
            RootNode = null;
        }
        OnPropertyChanged(nameof(IsModuleSelectorEnabled));
        OnPropertyChanged(nameof(DisplayProjectName));

    }

    // Логика при выборе конкретного МОДУЛЯ (контекста) внутри проекта
    // Имя должно быть ОДИН В ОДИН как имя свойства после On...
    partial void OnSelectedModuleChanged(ContextModule? value)
    {
        if (value != null)
        {
            ModuleNameInput = value.ModuleName;
            ContextFileName = value.ContextFileName;
            ModuleRules = value.ModuleRules; // ЧИТАЕМ ПРАВИЛА МОДУЛЯ
            RefreshTreeView(value.CheckedFiles);
        }
        else
        {
            ModuleNameInput = string.Empty;
            ContextFileName = string.Empty;
            ModuleRules = string.Empty;
            RefreshTreeView(new List<string>());
        }
    }



    private void RefreshTreeView(List<string> checkedFiles)
    {
        if (Directory.Exists(RootPath))
        {
            RootNode = ContextBuilderService.BuildTree(RootPath, checkedFiles);
        }
        else
        {
            RootNode = null;
        }
    }

    [RelayCommand]
    private void CreateNewProject()
    {
        string name = string.IsNullOrWhiteSpace(ProjectNameInput)
            ? $"Проект {Projects.Count + 1}"
            : ProjectNameInput.Trim();

        // 1. ВАЛИДАЦИЯ НА СПЕЦСИМВОЛЫ
        if (!IsValidName(name, out string validationError))
        {
            System.Windows.MessageBox.Show(validationError, "Ошибка валидации",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2. ПРОВЕРКА НА ДУБЛИКАТЫ
        bool isDuplicate = Projects.Any(p => p.ProjectName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (isDuplicate)
        {
            System.Windows.MessageBox.Show($"Проект с названием \"{name}\" уже существует!", "Внимание",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newProject = new ProjectConfig { ProjectName = name };
        Projects.Add(newProject);
        SelectedProject = newProject;

        // 3. ОЧИСТКА И СОХРАНЕНИЕ
        ProjectNameInput = string.Empty;
        IsProjectNameInvalid = false;
        SilentSave();
    }

    [RelayCommand]
    private void CreateNewModule()
    {
        if (SelectedProject == null)
        {
            System.Windows.MessageBox.Show("Сначала выберите или создайте проект!", "Внимание",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string name = string.IsNullOrWhiteSpace(ModuleNameInput)
            ? $"Модуль {Modules.Count + 1}"
            : ModuleNameInput.Trim();

        // 1. ВАЛИДАЦИЯ НА СПЕЦСИМВОЛЫ
        if (!IsValidName(name, out string validationError))
        {
            System.Windows.MessageBox.Show(validationError, "Ошибка валидации",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2. ПРОВЕРКА НА ДУБЛИКАТЫ
        bool isDuplicate = Modules.Any(m => m.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (isDuplicate)
        {
            System.Windows.MessageBox.Show($"Модуль с названием \"{name}\" уже существует в этом проекте!", "Внимание",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newModule = new ContextModule
        {
            ModuleName = name,
            ContextFileName = $"{GetSafeFileName(name)}.txt"
        };

        Modules.Add(newModule);
        SelectedModule = newModule;
        RefreshTreeView(new List<string>());

        // 3. ОЧИСТКА И СОХРАНЕНИЕ
        ModuleNameInput = string.Empty;
        IsModuleNameInvalid = false;
        SilentSave();
        
    }

    // Универсальный метод валидации имени проекта или модуля
    private bool IsValidName(string name, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            errorMessage = "Имя не может быть пустым или состоять только из пробелов.";
            return false;
        }

        // Получаем массив символов, запрещенных в именах файлов/папок Windows
        char[] invalidChars = System.IO.Path.GetInvalidFileNameChars();

        // Дополнительно можно явно дописать проверку на популярные проблемные символы, 
        // если GetInvalidFileNameChars их не перекрывает в некоторых контекстах
        foreach (char c in invalidChars)
        {
            if (name.Contains(c))
            {
                errorMessage = $"Имя содержит недопустимый символ '{c}'.\nЗапрещено использовать: \\ / : * ? \" < > |";
                return false;
            }
        }

        return true;
    }
    [RelayCommand]
    private void UpdateCurrentProjectName()
    {
        if (SelectedProject == null) return;
        string newName = ProjectNameInput?.Trim() ?? string.Empty;
        if (newName.Equals(SelectedProject.ProjectName, StringComparison.Ordinal)) return;

        if (!IsValidName(newName, out string validationError))
        {
            IsProjectNameInvalid = true; // ВКЛЮЧАЕМ ПОДСВЕТКУ
            System.Windows.MessageBox.Show(validationError, "Ошибка валидации проекта", MessageBoxButton.OK, MessageBoxImage.Warning);
            ProjectNameInput = SelectedProject.ProjectName;
            return;
        }

        IsProjectNameInvalid = false; // СБРАСЫВАЕМ ПОДСВЕТКУ
        SelectedProject.ProjectName = newName;

        var index = Projects.IndexOf(SelectedProject);
        if (index >= 0) { Projects[index] = SelectedProject; SelectedProject = Projects[index]; }
        OnPropertyChanged(nameof(DisplayProjectName));
        SilentSave();
    }

    [RelayCommand]
    private void UpdateCurrentModuleName()
    {
        if (SelectedProject == null || SelectedModule == null) return;
        string newName = ModuleNameInput?.Trim() ?? string.Empty;
        if (newName.Equals(SelectedModule.ModuleName, StringComparison.Ordinal)) return;

        if (!IsValidName(newName, out string validationError))
        {
            IsModuleNameInvalid = true; // ВКЛЮЧАЕМ ПОДСВЕТКУ
            System.Windows.MessageBox.Show(validationError, "Ошибка валидации модуля", MessageBoxButton.OK, MessageBoxImage.Warning);
            ModuleNameInput = SelectedModule.ModuleName;
            return;
        }

        IsModuleNameInvalid = false; // СБРАСЫВАЕМ ПОДСВЕТКУ
        SelectedModule.ModuleName = newName;
        SelectedModule.ContextFileName = $"{GetSafeFileName(newName)}.txt";
        ContextFileName = SelectedModule.ContextFileName;

        var index = Modules.IndexOf(SelectedModule);
        if (index >= 0) { Modules[index] = SelectedModule; SelectedModule = Modules[index]; }
        SelectedModule.ModuleRules = ModuleRules;
        SilentSave();
    }

    partial void OnProjectNameInputChanged(string value) => IsProjectNameInvalid = false;
    partial void OnModuleNameInputChanged(string value) => IsModuleNameInvalid = false;

    // Изменим сигнатуру метода, добавив необязательный параметр showMessage

    [RelayCommand]
    public void SaveProject()
    {
        ExecuteSave(showMessage: true);
    }

    // Этот метод мы вызываем при закрытии, он работает без всплывающих окон
    public void SilentSave()
    {
        ExecuteSave(showMessage: false);
    }

    // Основная логика сохранения
    private void ExecuteSave(bool showMessage)
    {
        if (SelectedProject == null)
        {
            if (!string.IsNullOrWhiteSpace(ProjectNameInput))
            {
                var autoProject = new ProjectConfig { ProjectName = ProjectNameInput };
                Projects.Add(autoProject);
                SelectedProject = autoProject;
            }
            else
            {
                if (showMessage) System.Windows.MessageBox.Show("Введите имя проекта перед сохранением.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (SelectedModule != null && RootNode != null)
        {
            var checkedFilesList = new List<string>();
            var flatList = new List<FileSystemNode>();
            ContextBuilderService.GetCheckedFiles(RootNode, flatList);
            foreach (var node in flatList)
            {
                checkedFilesList.Add(node.RelativePath);
            }

            SelectedModule.ModuleName = ModuleNameInput;
            SelectedModule.ContextFileName = GetSafeFileName(ModuleNameInput) + ".txt";
            SelectedModule.ModuleRules = ModuleRules; // СОХРАНЯЕМ ПРАВИЛА МОДУЛЯ
            SelectedModule.CheckedFiles = checkedFilesList;

            var mIdx = Modules.IndexOf(SelectedModule);
            if (mIdx >= 0) Modules[mIdx] = SelectedModule;
        }

        SelectedProject.ProjectName = ProjectNameInput;
        SelectedProject.RootPath = RootPath;
        SelectedProject.OutputPath = OutputPath;
        SelectedProject.PromptRules = PromptRules;
        SelectedProject.IncludeDirectoryStructure = IncludeDirectoryStructure; // СОХРАНЯЕМ НАСТРОЙКУ
        
        SelectedProject.Modules = Modules.ToList();

        var pIdx = Projects.IndexOf(SelectedProject);
        if (pIdx >= 0)
        {
            Projects[pIdx] = SelectedProject;
            SelectedProject = Projects[pIdx];
        }

        try
        {
            string json = JsonConvert.SerializeObject(Projects, Formatting.Indented);
            File.WriteAllText(_configFilePath, json);
            if (showMessage) System.Windows.MessageBox.Show("Всё успешно сохранено!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (showMessage) System.Windows.MessageBox.Show($"Ошибка сохранения JSON: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

 
    [RelayCommand]
    private void BrowseRootPath()
    {
        var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            RootPath = dialog.FileName;
            RefreshTreeView(SelectedModule?.CheckedFiles ?? new List<string>());
        }
    }

    [RelayCommand]
    private void BrowseOutputPath()
    {
        var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            OutputPath = dialog.FileName;
        }
    }

    private void LoadProjects()
    {
        if (File.Exists(_configFilePath))
        {
            try
            {
                string json = File.ReadAllText(_configFilePath);
                var list = JsonConvert.DeserializeObject<List<ProjectConfig>>(json);
                if (list != null)
                {
                    Projects = new ObservableCollection<ProjectConfig>(list);
                    if (Projects.Count > 0) SelectedProject = Projects[0];
                }
            }
            catch { }
        }
    }

    // Метод автоматической генерации безопасного имени файла
    private string GetSafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "context";

        string safeName = name.Replace(" ", "-");
        char[] invalidChars = Path.GetInvalidFileNameChars();

        foreach (char c in invalidChars)
        {
            safeName = safeName.Replace(c.ToString(), "");
        }
        return string.IsNullOrWhiteSpace(safeName) ? "context" : safeName.ToLower();
    }


    // Метод, вызываемый при закрытии приложения
    public void OnWindowClosing()
    {
        // Сохраняем состояние темы в настройки по умолчанию
        Properties.Settings.Default.IsDarkTheme = IsDarkTheme;
        // Сохраняем состояние чекбокса в Settings приложения
        Properties.Settings.Default.AutoSaveOnExit = IsAutoSaveEnabled;
        Properties.Settings.Default.IsProjectBlockExpanded = IsProjectBlockExpanded;
        Properties.Settings.Default.IncludeDirectoryStructure = IncludeDirectoryStructure; // СОХРАНЯЕМ ГЛОБАЛЬНО
        Properties.Settings.Default.Save();

        // Если чекбокс активен — сохраняем проект без вывода MessageBox
        if (IsAutoSaveEnabled)
        {
            SilentSave();
        }
    }

    // Этот метод срабатывает автоматически каждый раз, когда меняется галочка Тёмной темы
    partial void OnIsDarkThemeChanged(bool value)
    {
        // Очищаем старую палитру приложения
        Application.Current.Resources.MergedDictionaries.Clear();

        var themeDict = new ResourceDictionary();

        if (value)
        {
            // Накатываем каноничные цвета глубокой тёмной темы VS Code
            themeDict.Add("WindowBackgroundBrush", new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
            themeDict.Add("ControlBackgroundBrush", new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)));
            themeDict.Add("TextBoxBackgroundBrush", new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)));
            themeDict.Add("TextBrush", new SolidColorBrush(Color.FromRgb(0xF1, 0xF1, 0xF1)));
            themeDict.Add("SplitterBrush", new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)));
            themeDict.Add("BorderBrush", new SolidColorBrush(Color.FromRgb(0x43, 0x43, 0x46)));
        }
        else
        {
            // Возвращаем чистые светлые цвета Windows
            themeDict.Add("WindowBackgroundBrush", new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)));
            themeDict.Add("ControlBackgroundBrush", new SolidColorBrush(Color.FromRgb(0xFF,0xFF, 0xFF)));
            themeDict.Add("TextBoxBackgroundBrush", new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
            themeDict.Add("TextBrush", new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)));
            themeDict.Add("SplitterBrush", new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)));
            themeDict.Add("BorderBrush", new SolidColorBrush(Color.FromRgb(0xCC, 0x2C, 0xCC)));
        }

        // Внедряем новую палитру в глобальный контекст WPF
        Application.Current.Resources.MergedDictionaries.Add(themeDict);

        // Красим заголовок главного окна на лету
        if (Application.Current.MainWindow is MainWindow mainWin)
        {
            InverseBooleanConverter.ApplyTitleBarColor(mainWin, value);
        }


    }


    // Маленький служебный класс-заглушка для связки MigraDoc с системными шрифтами
    private CancellationTokenSource? _cts;

    // Свойства для отображения оверлея прогресса
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMax;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _currentFileText = string.Empty;

    // Команда "Отмена"
    [RelayCommand]
    private void CancelGeneration()
    {
        _cts?.Cancel();
        ProgressText = "Отмена операции...";
    }

    // Полностью заменяем старую команду GenerateContext на асинхронную
    [RelayCommand]
    private async Task GenerateContext()
    {
        string extension = IsPdfFormat ? ".pdf" : ".txt";
        string safeFileName = GetSafeFileName(ModuleNameInput) + extension;

        if (string.IsNullOrWhiteSpace(OutputPath) || string.IsNullOrWhiteSpace(ModuleNameInput) || RootNode == null)
        {
            System.Windows.MessageBox.Show("Не заполнены критические данные: выходная папка, имя модуля или структура проекта.",
                            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var checkedFiles = new List<FileSystemNode>();
        ContextBuilderService.GetCheckedFiles(RootNode, checkedFiles);
        if (checkedFiles.Count == 0)
        {
            System.Windows.MessageBox.Show("Не выбрано ни одного файла для нарезки контекста.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Включаем оверлей прогресса в UI
        IsProcessing = true;
        ProgressValue = 0;
        ProgressMax = checkedFiles.Count;
        ProgressText = $"Подготовка к обработке {checkedFiles.Count} файлов...";
        CurrentFileText = "";

        _cts = new CancellationTokenSource();

        // Настраиваем отправку прогресса в поток UI
        var progressHandler = new Progress<ProgressReport>(report =>
        {
            ProgressValue = report.CurrentIndex;
            ProgressText = $"Обработано файлов: {report.CurrentIndex} из {report.TotalCount}";
            CurrentFileText = $"Текущий файл: {report.CurrentFileName}";
        });

        try
        {
            string fullPath = Path.Combine(OutputPath, safeFileName);

            // Найдите блок генерации (IsPdfFormat) и замените передачу параметров:
            if (IsPdfFormat)
            {
                await ContextBuilderService.GeneratePdfContextFileAsync(OutputPath, safeFileName, PromptRules,
                    ModuleRules, IncludeDirectoryStructure, RootNode, progressHandler, _cts.Token); // ДОБАВЛЕН ФЛАГ
            }
            else
            {
                await ContextBuilderService.GenerateContextFileAsync(OutputPath, safeFileName, PromptRules,
                    ModuleRules, IncludeDirectoryStructure, RootNode, progressHandler, _cts.Token); // ДОБАВЛЕН ФЛАГ
            }



            // Автооткрытие Проводника Windows (оставляем без изменений)
            if (File.Exists(fullPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
            }
        }
        catch (OperationCanceledException)
        {
            System.Windows.MessageBox.Show("Операция сборки контекста была отменена пользователем.", "Отмена", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Ошибка сборки файла: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // Выключаем оверлей в любом случае
            IsProcessing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

}
