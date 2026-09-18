using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.WindowsAPICodePack.Dialogs;
using MigraDoc;
using Newtonsoft.Json;
using PdfSharp.Fonts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

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
    //Подсчёт токенов
    [ObservableProperty] private long _totalCharacters;
    [ObservableProperty] private long _estimatedTokens;
    [ObservableProperty] private bool _isCalculatingSize;
    // Внимательно проверьте написание этой переменной:
    [ObservableProperty]
    private ContextModule? _selectedModule;
    private bool _isUpdatingFields = false;
    [ObservableProperty] private string _newModuleNameInput = string.Empty;
    // Редактирование названия модуля
    [ObservableProperty] private bool _isEditOverlayVisible;
    [ObservableProperty] private string _editModuleNameInput = string.Empty;
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
    [RelayCommand]
    private void ShowEditOverlay()
    {
        if (SelectedModule == null) return;

        // Копируем текущее имя модуля в буферное поле ввода
        EditModuleNameInput = SelectedModule.ModuleName;
        IsEditOverlayVisible = true;
    }
    [RelayCommand]
    private void CancelEditOverlay()
    {
        IsEditOverlayVisible = false;
        IsModuleNameInvalid = false; // Сбрасываем красную рамку ошибки, если она была
    }
    [RelayCommand]
    private void ConfirmRenameModule()
    {
        if (SelectedProject == null || SelectedModule == null) return;

        string newName = EditModuleNameInput?.Trim() ?? string.Empty;

        // 1. Проверка на пустую строку
        if (string.IsNullOrWhiteSpace(newName))
        {
            System.Windows.MessageBox.Show("Имя модуля не может быть пустым!", "Ошибка валидации",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2. Проверка на дубликаты
        bool isDuplicate = SelectedProject.Modules.Any(m =>
            m != SelectedModule &&
            m.ModuleName.Equals(newName, StringComparison.OrdinalIgnoreCase));

        if (isDuplicate)
        {
            System.Windows.MessageBox.Show($"Модуль с названием \"{newName}\" уже существует в этом проекте!", "Ошибка валидации",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 3. Проверка на спецсимволы файловой системы Windows
        if (!IsValidName(newName, out string validationError))
        {
            System.Windows.MessageBox.Show(validationError, "Ошибка валидации", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ИСПРАВЛЕНО: Запоминаем ссылку на текущий переименовываемый модуль
        var currentModule = SelectedModule;

        _isUpdatingFields = true;
        try
        {
            currentModule.ModuleName = newName;
            currentModule.ContextFileName = $"{GetSafeFileName(newName)}.txt";
            ContextFileName = currentModule.ContextFileName;
            currentModule.ModuleRules = ModuleRules;

            // Синхронизируем строку в коллекции
            var index = Modules.IndexOf(currentModule);
            if (index >= 0)
            {
                Modules[index] = currentModule;
            }

            ModuleNameInput = newName;
        }
        finally
        {
            _isUpdatingFields = false;
        }

        // ИСПРАВЛЕНО: Откладываем восстановление фокуса ComboBox на следующий такт UI-потока.
        // Это гарантирует, что ComboBox выберет именно переименованный пункт, а не сбросится на индекс 0!
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            _isUpdatingFields = true;
            try
            {
                SelectedModule = currentModule;
            }
            finally
            {
                _isUpdatingFields = false;
            }

            // Принудительно заставляем селектор кнопок пересчитать состояние
            OnPropertyChanged(nameof(IsModuleSelectorEnabled));
        }), System.Windows.Threading.DispatcherPriority.Background);

        // Закрываем оверлей и сохраняем проект на диск
        IsEditOverlayVisible = false;
        SilentSave();
    }

    // Свойство доступности блока модулей (теперь со строгим уведомлением для интерфейса)
    // Настраиваемый черный список папок (дефолтные значения)
    [ObservableProperty]
    private string _excludedFoldersInput = "bin, obj, .vs, publish, .git, .idea, node_modules";

    // Настраиваемый черный список мусорных и бинарных расширений (дефолтные значения)
    [ObservableProperty]
    private string _excludedExtensionsInput = ".png, .jpg, .jpeg, .gif, .ico, .bmp, .webp, .mp3, .wav, .ogg, .flac, .aac, .mp4, .avi, .mkv, .mov, .zip, .rar, .7z, .tar, .gz, .dll, .exe, .pdb, .suo, .user, .fbx, .obj, .max, .blend, .3ds, .bak, .tmp, .temp, .log";

    [RelayCommand]
    private async Task RecalculateContextSizeAsync()
    {
        if (RootNode == null)
        {
            TotalCharacters = 0;
            EstimatedTokens = 0;
            return;
        }

        IsCalculatingSize = true;

        await Task.Run(() =>
        {
            var checkedFiles = new List<FileSystemNode>();
            ContextBuilderService.GetCheckedFiles(RootNode, checkedFiles);

            long charCount = 0;

            // 1. Считаем символы в выбранных файлах исходного кода
            foreach (var file in checkedFiles)
            {
                if (file != null && File.Exists(file.FullPath))
                {
                    try
                    {
                        var fi = new FileInfo(file.FullPath);
                        charCount += fi.Length;

                        // НОВОЕ: Если структура каталогов включена, добавляем вес строк разметки структуры папок
                        if (IncludeDirectoryStructure)
                        {
                            // Примерно 15 символов на строку вида " [Файл] Relative\Path\File.cs\n"
                            charCount += (file.RelativePath?.Length ?? 0) + 10;
                        }
                    }
                    catch { }
                }
            }

            // 2. Добавляем символы из полей правил и служебную разметку тегов
            charCount += (PromptRules?.Length ?? 0);
            charCount += (ModuleRules?.Length ?? 0);
            charCount += 150; // Запас на служебные теги

            // 3. Считаем токены. Код (английский) ~ 4 символа на токен. 
            // Русский текст в правилах ~ 2 символа на токен. Делаем взвешенную безопасную оценку:
            long rulesLength = (PromptRules?.Length ?? 0) + (ModuleRules?.Length ?? 0);
            long codeLength = charCount - rulesLength;
            if (codeLength < 0) codeLength = 0;

            long tokensFromCode = codeLength / 4;
            long tokensFromRules = rulesLength / 2; // Более тяжелые токены для кириллицы

            TotalCharacters = charCount;
            EstimatedTokens = tokensFromCode + tokensFromRules;
        });

        IsCalculatingSize = false;
    }
    partial void OnPromptRulesChanged(string value) => _ = RecalculateContextSizeAsync();
    partial void OnModuleRulesChanged(string value) => _ = RecalculateContextSizeAsync();
    // Автоматический пересчет токенов при клике на чекбокс структуры каталогов
    partial void OnIncludeDirectoryStructureChanged(bool value) => _ = RecalculateContextSizeAsync();


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

    public bool IsModuleSelectorEnabled => SelectedProject != null && Modules != null && Modules.Count > 0;


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
            ProjectNameInput = value.ProjectName;
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
        if (_isUpdatingFields) return;

        _isUpdatingFields = true;
        try
        {
            if (value != null)
            {
                ModuleNameInput = value.ModuleName;
                ContextFileName = value.ContextFileName;
                ModuleRules = value.ModuleRules;

                // 1. Строим базовую структуру файлов с диска
                RefreshTreeView(value.CheckedFiles);

                // 2. ИСПРАВЛЕНО: Мгновенно накатываем сохраненные структуры из JSON.
                // Папки покроются закрашенными квадратиками в ту же секунду!
                if (RootNode != null)
                {
                    FastPreloadSavedEntries(value, RootNode);
                }
            }
            else
            {
                ModuleNameInput = string.Empty;
                ContextFileName = string.Empty;
                ModuleRules = string.Empty;
                RefreshTreeView(new List<string>());
            }
        }
        finally
        {
            _isUpdatingFields = false;
        }
    }


    private void RefreshTreeView(List<string> checkedFiles)
    {
        if (SelectedProject == null || string.IsNullOrWhiteSpace(RootPath))
        {
            RootNode = null;
            _ = RecalculateContextSizeAsync();
            return;
        }

        switch (SelectedProject.Type)
        {
            case ProjectType.GoogleDoc:
                // Для Google Docs корнем будет виртуальный файл кэша
                string docId = GoogleDownloader.ExtractDocumentId(RootPath);
                string cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache", $"{docId}.json");
                RootNode = new FileSystemNode
                {
                    Name = $"{SelectedProject.ProjectName}.gdoc",
                    FullPath = cachePath,
                    RelativePath = $"{SelectedProject.ProjectName}.gdoc",
                    IsFile = true
                };
                RootNode.Children.Add(new FileSystemNode { Name = "LoadingStub...", Parent = RootNode });
                break;

            case ProjectType.WordDoc:
                // Для Word файлов корнем является сам файл .docx на диске
                RootNode = new FileSystemNode
                {
                    Name = Path.GetFileName(RootPath),
                    FullPath = RootPath,
                    RelativePath = Path.GetFileName(RootPath),
                    IsFile = true
                };
                RootNode.Children.Add(new FileSystemNode { Name = "LoadingStub...", Parent = RootNode });
                break;

            default:
                // Стандартный локальный проект-папка
                if (Directory.Exists(RootPath))
                {
                    RootNode = ContextBuilderService.BuildTree(RootPath, checkedFiles);
                    if (RootNode != null) DeepVerifyCheckStates(RootNode);
                }
                else
                {
                    RootNode = null;
                }
                break;
        }
        _ = RecalculateContextSizeAsync();
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

        // Читаем имя из ИЗОЛИРОВАННОГО поля ввода нового модуля
        string name = string.IsNullOrWhiteSpace(NewModuleNameInput)
            ? $"Модуль {Modules.Count + 1}"
            : NewModuleNameInput.Trim();

        bool isDuplicate = SelectedProject.Modules.Any(m =>
            m.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (isDuplicate)
        {
            System.Windows.MessageBox.Show($"Модуль с названием \"{name}\" уже существует в этом проекте!", "Внимание",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newModule = new ContextModule
        {
            ModuleName = name,
            ContextFileName = $"{GetSafeFileName(name)}.txt",
            CheckedFiles = new List<string>(),
            CheckedEntries = new List<SyntaxEntry>()
        };

        _isUpdatingFields = true;
        try
        {
            SelectedProject.Modules.Add(newModule);
            Modules.Add(newModule);

            // Полностью пересоздаем корень дерева файлов для изоляции памяти
            RootNode = new FileSystemNode
            {
                Name = System.IO.Path.GetFileName(SelectedProject.RootPath),
                FullPath = SelectedProject.RootPath,
                IsFile = false
            };
            OnPropertyChanged(nameof(RootNode));
            RefreshTreeView(new List<string>());

            // ОЧИЩАЕМ ИЗОЛИРОВАННОЕ ПОЛЕ. Теперь это никак не затронет ComboBox!
            NewModuleNameInput = string.Empty;
        }
        finally
        {
            _isUpdatingFields = false;
        }

        // Мягко переключаем фокус в UI-потоке
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            SelectedModule = newModule;
        }), System.Windows.Threading.DispatcherPriority.Background);

        SilentSave();
    }

    // public bool IsModuleSelectorEnabled => SelectedProject != null && Modules != null && Modules.Count > 0;


    [RelayCommand]
    private void UpdateCurrentModuleName()
    {
        // ЖЕСТКАЯ ЗАЩИТА: Если мы программно обновляем интерфейс, или проект/модуль не выбраны — выходим сразу
        if (_isUpdatingFields || SelectedProject == null || SelectedModule == null) return;

        string newName = ModuleNameInput?.Trim() ?? string.Empty;

        // ГАРАНТИЯ СТАБИЛЬНОСТИ: Если поле ввода пустое, содержит дефолтное "Модуль X" 
        // или полностью совпадает с текущим именем модуля — НЕМЕДЛЕННО прекращаем выполнение.
        // Это заблокирует ложные срабатывания при очистке полей и смене фокуса!
        if (string.IsNullOrWhiteSpace(newName) ||
            newName.StartsWith("Модуль ", StringComparison.OrdinalIgnoreCase) ||
            newName.Equals(SelectedModule.ModuleName, StringComparison.Ordinal))
        {
            return;
        }

        if (!IsValidName(newName, out string validationError))
        {
            IsModuleNameInvalid = true;
            System.Windows.MessageBox.Show(validationError, "Ошибка валидации модуля", MessageBoxButton.OK, MessageBoxImage.Warning);
            // Возвращаем старое имя обратно в поле, чтобы сбросить ошибку
            _isUpdatingFields = true;
            ModuleNameInput = SelectedModule.ModuleName;
            _isUpdatingFields = false;
            return;
        }

        IsModuleNameInvalid = false;

        // Создаем локальную копию, защищенную от сбросов UI
        var currentModule = SelectedModule;

        currentModule.ModuleName = newName;
        currentModule.ContextFileName = $"{GetSafeFileName(newName)}.txt";
        ContextFileName = currentModule.ContextFileName;
        currentModule.ModuleRules = ModuleRules;

        var index = Modules.IndexOf(currentModule);
        if (index >= 0)
        {
            _isUpdatingFields = true;
            try
            {
                Modules[index] = currentModule;
            }
            finally
            {
                _isUpdatingFields = false;
            }
        }

        SilentSave();
    }


    // Метод принудительной синхронизации текущего состояния дерева с моделью модуля
    public void SyncTreeWithModule()
    {
        if (SelectedModule == null || RootNode == null) return;

        // 1. В CheckedFiles сохраняем ТОЛЬКО файлы, выбранные на 100% целиком
        var checkedFilesList = new List<string>();
        var flatFilesList = new List<FileSystemNode>();
        ContextBuilderService.GetCheckedFiles(RootNode, flatFilesList); // Использует строго true
        foreach (var node in flatFilesList)
        {
            checkedFilesList.Add(node.RelativePath);
        }

        // 2. В CheckedEntries собираем точечные элементы синтаксиса
        var checkedEntriesList = new List<SyntaxEntry>();
        var flatSyntaxList = new List<FileSystemNode>();
        CollectAllCheckedSyntaxNodes(RootNode, flatSyntaxList);
        foreach (var node in flatSyntaxList)
        {
            checkedEntriesList.Add(new SyntaxEntry
            {
                FilePath = node.RelativePath,
                EntryPath = node.EntryPath,
                DisplayName = node.Name,
                Type = node.SyntaxType,
                SpanInfo = node.SyntaxSpanInfo
            });
        }

        SelectedModule.CheckedFiles = checkedFilesList;
        SelectedModule.CheckedEntries = checkedEntriesList;
    }




    // Вспомогательный метод для глубокой очистки синтаксических нод
    private void ClearSyntaxNodesRecursive(FileSystemNode node)
    {
        if (node == null) return;

        // Проходим по детям с конца, чтобы безопасно удалять элементы
        for (int i = node.Children.Count - 1; i >= 0; i--)
        {
            var child = node.Children[i];
            if (child.IsSyntaxNode)
            {
                node.Children.RemoveAt(i);
            }
            else
            {
                ClearSyntaxNodesRecursive(child);
            }
        }
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
            var flatFilesList = new List<FileSystemNode>();

            // Собираем вовлеченные файлы (полные и частичные)
            ContextBuilderService.GetCheckedFiles(RootNode, flatFilesList);
            foreach (var node in flatFilesList)
            {
                checkedFilesList.Add(node.RelativePath);
            }

            // НОВОЕ: Собираем детальный список выбранных элементов синтаксиса
            var checkedEntriesList = new List<SyntaxEntry>();
            var flatSyntaxList = new List<FileSystemNode>();

            // Рекурсивно собираем все синтаксические ноды с галочками из всего дерева
            CollectAllCheckedSyntaxNodes(RootNode, flatSyntaxList);

            foreach (var node in flatSyntaxList)
            {
                checkedEntriesList.Add(new SyntaxEntry
                {
                    FilePath = node.RelativePath,
                    EntryPath = node.EntryPath,
                    DisplayName = node.Name,
                    Type = node.SyntaxType,
                    SpanInfo = node.SyntaxSpanInfo
                });
            }

            SelectedModule.ModuleName = ModuleNameInput;
            SelectedModule.ContextFileName = GetSafeFileName(ModuleNameInput) + ".txt";
            SelectedModule.ModuleRules = ModuleRules;

            SelectedModule.CheckedFiles = checkedFilesList; // Сохраняем базовые ключи-файлы
            SelectedModule.CheckedEntries = checkedEntriesList; // Сохраняем детальный синтаксис!

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
    // Рекурсивный сбор всех выбранных элементов кода для сохранения в JSON
    // ИСПРАВЛЕНО: Рекурсивный сбор синтаксических нод БЕЗ мусора и дубликатов
    private void CollectAllCheckedSyntaxNodes(FileSystemNode node, List<FileSystemNode> result)
    {
        // Если мы наткнулись на ФАЙЛ, и он выбран ПОЛНОСТЬЮ (True) —
        // мы ОСТАНАВЛИВАЕМ рекурсию и не идем внутрь него! 
        // Его внутренние методы и таблицы НЕ должны попадать в CheckedEntries, 
        // так как файл идет в контекст целиком.
        if (node.IsFile && node.IsChecked == true)
        {
            return;
        }

        // Если это элемент синтаксиса (метод, класс, таблица) и на нем стоит галочка —
        // мы берем его ТОЛЬКО в том случае, если его родительский файл выбран частично.
        if (node.IsSyntaxNode && node.IsChecked == true)
        {
            result.Add(node);
        }

        // Идем глубже по дереву
        foreach (var child in node.Children)
        {
            CollectAllCheckedSyntaxNodes(child, result);
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
    // Пробегает по всему дереву до самых глубоких файлов и заставляет папки рассчитать свои квадратики снизу вверх
    private void DeepVerifyCheckStates(FileSystemNode node)
    {
        if (node == null) return;

        // Сначала уходим на самую глубину к файлам и синтаксическим элементам
        foreach (var child in node.Children)
        {
            DeepVerifyCheckStates(child);
        }

        // На обратном пути (снизу вверх) заставляем контейнеры обновить свое состояние
        if (!node.IsFile && node.Children.Count > 0)
        {
            node.VerifyCheckState();
        }
    }

    // Полностью заменяем старую команду GenerateContext на асинхронную
    [RelayCommand]
    private async Task GenerateContext()
    {
        if (SelectedModule == null || RootNode == null) return;

        // ЖЕЛЕЗНАЯ СИНХРОНИЗАЦИЯ: Считываем все галочки с экрана прямо в модель модуля перед сборкой
        SyncTreeWithModule();
        string extension = IsPdfFormat ? ".pdf" : ".txt";
        string safeFileName = GetSafeFileName(ModuleNameInput) + extension;

        if (string.IsNullOrWhiteSpace(OutputPath) || string.IsNullOrWhiteSpace(ModuleNameInput) || RootNode == null)
        {
            System.Windows.MessageBox.Show("Не заполнены критические данные: выходная папка, имя модуля или структура проекта.",
                            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var checkedFiles = new List<FileSystemNode>();
        // Было: ContextBuilderService.GetCheckedFiles(RootNode, checkedFiles);
        // Стало:
        ContextBuilderService.GetCheckedFilesExtended(RootNode, checkedFiles); // Собирает и полные, и частичные файлы для ИИ!

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
                    ModuleRules, IncludeDirectoryStructure, RootNode, SelectedModule.CheckedEntries, progressHandler, _cts.Token); // ДОБАВЛЕН СПИСОК
            }
            else
            {
                await ContextBuilderService.GenerateContextFileAsync(OutputPath, safeFileName, PromptRules,
                    ModuleRules, IncludeDirectoryStructure, RootNode, SelectedModule.CheckedEntries, progressHandler, _cts.Token); // ДОБАВЛЕН СПИСОК
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
    // Асинхронно восстанавливает структуру только для тех файлов, которые были частично выбраны в проекте
    private async Task RestoreSavedSyntaxStructureAsync(ContextModule module, FileSystemNode root)
    {
        if (module?.CheckedEntries == null || module.CheckedEntries.Count == 0 || root == null) return;

        // Собираем уникальные относительные пути файлов, для которых есть сохраненные записи элементов
        var filesToPreload = new HashSet<string>();
        foreach (var entry in module.CheckedEntries)
        {
            if (!string.IsNullOrEmpty(entry.FilePath))
            {
                filesToPreload.Add(entry.FilePath);
            }
        }

        // Запускаем фоновую задачу для каждого такого файла
        foreach (var relPath in filesToPreload)
        {
            // Ищем узел этого файла в нашем построенном дереве
            var fileNode = FindNodeByRelativePath(root, relPath);
            if (fileNode != null)
            {
                // Асинхронно загружаем синтаксическую структуру файла в фоновом потоке
                await PopulateSyntaxNodesAsync(fileNode);

                // Точечно восстанавливаем галочки для элементов этого файла на основе JSON
                RestoreEntriesCheckState(fileNode, module.CheckedEntries);
            }
        }
    }

    // Вспомогательный метод поиска узла файла в дереве по его относительному пути
    private FileSystemNode? FindNodeByRelativePath(FileSystemNode current, string relativePath)
    {
        if (current.IsFile && current.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        foreach (var child in current.Children)
        {
            var found = FindNodeByRelativePath(child, relativePath);
            if (found != null) return found;
        }

        return null;
    }

    // Рекурсивный метод простановки галочек сохраненным элементам
    private void RestoreEntriesCheckState(FileSystemNode node, List<SyntaxEntry> savedEntries)
    {
        if (node.IsSyntaxNode)
        {
            // Проверяем, есть ли текущий элемент кода в списке сохраненных в JSON
            var match = savedEntries.Find(e => e.FilePath.Equals(node.RelativePath, StringComparison.OrdinalIgnoreCase)
                                            && e.EntryPath.Equals(node.EntryPath, StringComparison.Ordinal));
            if (match != null)
            {
                // Восстанавливаем координаты и ставим галочку (без каскада вниз, чтобы не перетереть детей)
                node.SyntaxSpanInfo = match.SpanInfo;
                node.SetChecked(true, updateChildren: false, updateParent: true);
            }
        }

        // Проходим по детям (вглубь классов к методам)
        // Делаем копию коллекции, чтобы избежать ошибок изменения во время перебора
        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
        {
            RestoreEntriesCheckState(child, savedEntries);
        }
    }
    // Универсальный метод асинхронного парсинга файла и подселения синтаксических нод в дерево
    public async Task PopulateSyntaxNodesAsync(FileSystemNode fileNode)
    {
        // ИСПРАВЛЕНО: Проверяем, есть ли среди детей нода-заглушка "LoadingStub..."
        bool hasStub = false;
        for (int i = 0; i < fileNode.Children.Count; i++)
        {
            if (fileNode.Children[i].Name == "LoadingStub...")
            {
                hasStub = true;
                fileNode.Children.RemoveAt(i); // Удаляем техническую заглушку перед парсингом
                break;
            }
        }

        // Если заглушки нет И в списке уже есть элементы — значит, файл уже был полностью распарсен ранее, выходим
        if (!hasStub && fileNode.Children.Count > 0) return;

        string ext = System.IO.Path.GetExtension(fileNode.FullPath);
        if (!SyntaxParserFactory.IsSupported(ext)) return;

        var parser = SyntaxParserFactory.GetParser(ext);
        if (parser == null) return;

        List<SyntaxEntry> entries = await parser.ParseFileAsync(fileNode.FullPath, fileNode.RelativePath);

        // Строим словарь уже существующих в UI виртуальных нод, чтобы не дублировать их при парсинге
        var existingNodes = new Dictionary<string, FileSystemNode>(StringComparer.Ordinal);
        BuildExistingNodesMap(fileNode, existingNodes);

        foreach (var entry in entries)
        {
            // Если нода уже была создана быстрым прелоадером из JSON — пропускаем её добавление
            if (existingNodes.ContainsKey(entry.EntryPath)) continue;

            var newNode = new FileSystemNode
            {
                Name = entry.DisplayName,
                RelativePath = entry.FilePath,
                FullPath = fileNode.FullPath,
                IsFile = false,
                IsSyntaxNode = true,
                SyntaxType = entry.Type,
                SyntaxSpanInfo = entry.SpanInfo,
                EntryPath = entry.EntryPath,
                Parent = fileNode
            };

            string parentEntryPath = string.Empty;
            int lastDot = entry.EntryPath.LastIndexOf('.');
            if (lastDot > 0)
            {
                parentEntryPath = entry.EntryPath.Substring(0, lastDot);
            }

            // Пытаемся подселить к существующему родителю в UI
            if (!string.IsNullOrEmpty(parentEntryPath) && existingNodes.TryGetValue(parentEntryPath, out var parentUiNode))
            {
                newNode.Parent = parentUiNode;
                parentUiNode.Children.Add(newNode);
            }
            else
            {
                fileNode.Children.Add(newNode);
            }

            existingNodes[entry.EntryPath] = newNode;

            // Если файл или родительский класс выбран полностью — проставляем галочку новому методу
            if (fileNode.IsChecked == true || (newNode.Parent != null && newNode.Parent.IsChecked == true))
            {
                newNode.SetChecked(true, updateChildren: false, updateParent: false);
            }
        }

        fileNode.SetChecked(fileNode.IsChecked, updateChildren: false, updateParent: true);
    }

    // Вспомогательный метод для сбора карты уже существующих UI нод в файле
    private void BuildExistingNodesMap(FileSystemNode node, Dictionary<string, FileSystemNode> map)
    {
        if (node.IsSyntaxNode && !string.IsNullOrEmpty(node.EntryPath))
        {
            map[node.EntryPath] = node;
        }
        foreach (var child in node.Children)
        {
            BuildExistingNodesMap(child, map);
        }
    }


    private void FastPreloadSavedEntries(ContextModule module, FileSystemNode root)
    {
        if (module?.CheckedEntries == null || module.CheckedEntries.Count == 0 || root == null) return;

        // Группируем элементы из JSON по файлам
        var entriesByFile = new Dictionary<string, List<SyntaxEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in module.CheckedEntries)
        {
            if (string.IsNullOrEmpty(entry.FilePath)) continue;
            if (!entriesByFile.ContainsKey(entry.FilePath))
            {
                entriesByFile[entry.FilePath] = new List<SyntaxEntry>();
            }
            entriesByFile[entry.FilePath].Add(entry);
        }

        foreach (var kvp in entriesByFile)
        {
            var relPath = kvp.Key;
            var savedEntries = kvp.Value;

            var fileNode = FindNodeByRelativePath(root, relPath);
            if (fileNode != null)
            {
                // Очищаем коллекцию от базовых заглушек сканирования диска
                fileNode.Children.Clear();

                var nodeMap = new Dictionary<string, FileSystemNode>(StringComparer.Ordinal);

                // 1. Строим каркас выбранных элементов синтаксиса из JSON
                foreach (var entry in savedEntries)
                {
                    var newNode = new FileSystemNode
                    {
                        Name = entry.DisplayName,
                        RelativePath = entry.FilePath,
                        FullPath = fileNode.FullPath,
                        IsFile = false,
                        IsSyntaxNode = true,
                        SyntaxType = entry.Type,
                        SyntaxSpanInfo = entry.SpanInfo,
                        EntryPath = entry.EntryPath,
                        Parent = fileNode
                    };

                    string parentEntryPath = string.Empty;
                    int lastDot = entry.EntryPath.LastIndexOf('.');
                    if (lastDot > 0)
                    {
                        parentEntryPath = entry.EntryPath.Substring(0, lastDot);
                    }

                    if (!string.IsNullOrEmpty(parentEntryPath) && nodeMap.TryGetValue(parentEntryPath, out var parentUiNode))
                    {
                        newNode.Parent = parentUiNode;
                        parentUiNode.Children.Add(newNode);
                    }
                    else
                    {
                        fileNode.Children.Add(newNode);
                    }

                    nodeMap[entry.EntryPath] = newNode;

                    // Просто выставляем галочку элементу (без каскада, чтобы не трогать родительский файл раньше времени)
                    newNode.SetChecked(true, updateChildren: false, updateParent: false);
                }

                // 2. БЕЗОПАСНО подсаживаем техническую ноду полосы прогресса в самый конец списка детей
                fileNode.Children.Add(new FileSystemNode
                {
                    Name = "LoadingStub...",
                    Parent = fileNode,
                    IsFile = false,
                    IsSyntaxNode = false
                });

                // 3. И ТОЛЬКО ТЕПЕРЬ вызываем честный пересчет состояния файла.
                // Новая логика VerifyCheckState увидит заглушку и СТРОГО запретит файлу получить статус True!
                fileNode.VerifyCheckState();
            }
        }

        // Обновляем квадратики для родительских папок на диске снизу вверх
        DeepVerifyCheckStates(root);
    }

    // Флаги управления видимостью и состоянием оверлея проектов
    [ObservableProperty] private bool _isProjectOverlayVisible;
    [ObservableProperty] private bool _isLocalProjectSelected = true; // RadioButton: Локальная папка
    [ObservableProperty] private bool _isCloudProjectSelected;        // RadioButton: Google Doc

    // Буферные поля для ввода данных внутри оверлея
    [ObservableProperty] private string _editProjectNameInput = string.Empty;
    [ObservableProperty] private string _editProjectRootPathInput = string.Empty; // Папка или URL ссылки
    [ObservableProperty] private string _editProjectOutputPathInput = string.Empty;


    // Дополнительные свойства для трех типов проектов во ViewModel
    [ObservableProperty] private bool _isLocalFolderSelected = true;
    [ObservableProperty] private bool _isGoogleDocSelected;
    [ObservableProperty] private bool _isWordDocSelected;

    // Команда кнопки "Обзор" для источника данных (Папка или Word)
    [RelayCommand]
    private void BrowseProjectSource()
    {
        if (IsLocalFolderSelected)
        {
            // Используем ваш стандартный диалог выбора папки (например, CommonOpenFileDialog)
            var dialog = new Microsoft.WindowsAPICodePack.Dialogs.CommonOpenFileDialog { IsFolderPicker = true };
            if (dialog.ShowDialog() == Microsoft.WindowsAPICodePack.Dialogs.CommonFileDialogResult.Ok)
            {
                EditProjectRootPathInput = dialog.FileName;
            }
        }
        else if (IsWordDocSelected)
        {
            // Открываем диалог выбора файлов .doc / .docx
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Документы MS Word (*.doc;*.docx)|*.doc;*.docx|Все файлы (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                EditProjectRootPathInput = dialog.FileName;
            }
        }
    }

    // Команда кнопки "Вставить" (Иконка 📋 внутри текстового поля Google Doc)
    [RelayCommand]
    private void PasteUrlFromClipboard()
    {
        if (Clipboard.ContainsText())
        {
            EditProjectRootPathInput = Clipboard.GetText()?.Trim() ?? string.Empty;
        }
    }

    // КНОПКА ОТМЕНА: Полная зачистка буферных полей и возврат в интерфейс
    [RelayCommand]
    private void CancelProjectOverlay()
    {
        EditProjectNameInput = string.Empty;
        EditProjectRootPathInput = string.Empty;
        EditProjectOutputPathInput = string.Empty;
        IsProjectOverlayVisible = false;
    }

    // Дополнительный флаг для блокировки интерфейса оверлея во время скачивания кэша
    [ObservableProperty] private bool _isProjectLoading;

    // КНОПКА ОК: Комплексная валидация с учетом перечисления ProjectType
    [RelayCommand]
    private async Task ConfirmSaveProjectConfig()
    {
        string projName = EditProjectNameInput?.Trim() ?? string.Empty;
        string rootPath = EditProjectRootPathInput?.Trim() ?? string.Empty;
        string outPath = EditProjectOutputPathInput?.Trim() ?? string.Empty;

        // 1. Валидация на пустые строки
        if (string.IsNullOrWhiteSpace(projName) || string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(outPath))
        {
            MessageBox.Show("Все поля обязательны для заполнения!", "Ошибка валидации", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2. Проверка спецсимволы в имени проекта
        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (projName.Any(c => invalidChars.Contains(c)))
        {
            MessageBox.Show("Название проекта содержит недопустимые символы для имени файла!", "Ошибка валидации", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 3. Проверка на уникальность имени среди ДРУГИХ проектов
        bool isDuplicate = Projects.Any(p => p.ProjectName.Equals(projName, StringComparison.OrdinalIgnoreCase) && p != SelectedProject);
        if (isDuplicate)
        {
            MessageBox.Show($"Проект с названием \"{projName}\" уже существует!", "Ошибка валидации", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 4. Проверка и создание выходной папки результатов
        if (!Directory.Exists(outPath))
        {
            try { Directory.CreateDirectory(outPath); }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось создать выходную папку: {ex.Message}", "Ошибка диска", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        // Определяем выбранный тип на основе RadioButtons оверлея
        ProjectType selectedType = ProjectType.Folder;
        if (IsGoogleDocSelected) selectedType = ProjectType.GoogleDoc;
        else if (IsWordDocSelected) selectedType = ProjectType.WordDoc;

        // 5. Проверка доступности источников перед фиксацией
        if (selectedType == ProjectType.Folder && !Directory.Exists(rootPath))
        {
            MessageBox.Show("Указанная локальная папка источника данных не существует!", "Ошибка пути", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (selectedType == ProjectType.WordDoc && !File.Exists(rootPath))
        {
            MessageBox.Show("Указанный файл MS Word не найден на диске!", "Ошибка пути", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (selectedType == ProjectType.GoogleDoc)
        {
            if (string.IsNullOrEmpty(GoogleDownloader.ExtractDocumentId(rootPath)))
            {
                MessageBox.Show("Строка не содержит валидный ID Google Документа!", "Ошибка ссылки", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ВКЛЮЧАЕМ РЕЖИМ ЗАГРУЗКИ: Блокируем оверлей, показываем прогресс
            IsProjectLoading = true;
            try
            {
                // Запускаем наш класс-загрузчик. Путь к папке приложения берем из текущей среды выполнения
                var downloader = new GoogleDownloader();
                string projectBaseDir = AppDomain.CurrentDomain.BaseDirectory;

                bool success = await downloader.DownloadToCacheAsync(rootPath, projectBaseDir);
                if (!success)
                {
                    MessageBox.Show("Google Документ недоступен! Проверьте доступ по ссылке и сетевое подключение.", "Ошибка сети", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            finally
            {
                // Всегда выключаем режим загрузки при любом исходе
                IsProjectLoading = false;
            }
        }

        // 6. СОХРАНЕНИЕ: Переносим проверенные данные в модель
        ProjectConfig? targetProject = SelectedProject;
        // ИСПРАВЛЕНО: Заменили SelectedProject.ProjectName на targetProject.ProjectName
        if (targetProject == null || EditProjectNameInput != targetProject.ProjectName)
        {
            targetProject = new ProjectConfig();
            Projects.Add(targetProject);
        }


        targetProject.ProjectName = projName;
        targetProject.RootPath = rootPath;
        targetProject.OutputPath = outPath;
        targetProject.Type = selectedType; // Фиксируем новый ProjectType в конфигурации!

        // Прячем оверлей и фокусим ListBox на свежем проекте
        IsProjectOverlayVisible = false;
        SelectedProject = targetProject;

        SilentSave();
    }


}


