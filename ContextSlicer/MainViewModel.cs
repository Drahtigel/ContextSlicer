using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextSlicer.Filesystem;
using ContextSlicer.Google;
using Microsoft.WindowsAPICodePack.Dialogs;
using MigraDoc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PdfSharp.Fonts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
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

  //  public bool IsModuleSelectorEnabled => SelectedProject != null && Modules != null && Modules.Count > 0;


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
        // Ваша существующая инициализация (InitializeLanguage и т.д.)
        LoadAvailableServiceAccounts();

        LoadProjects();
    }


    // Логика при выборе ПРОЕКТА
    // Модифицированная логика автоматической фоновой проверки кэша при смене проекта
    // 3. Модифицированная логика автоматической фоновой проверки кэша (OnSelectedProjectChanged)
    partial void OnSelectedProjectChanged(ProjectConfig? value)
    {
        if (value != null)
        {
            ProjectNameInput = value.ProjectName;
            RootPath = value.RootPath;
            OutputPath = value.OutputPath;
            PromptRules = value.PromptRules;
            IncludeDirectoryStructure = value.IncludeDirectoryStructure;

            Modules = new ObservableCollection<ContextModule>(value.Modules);

            if (value.Type == ProjectType.GoogleDoc)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Func<Task>(async () =>
                {
                    var downloader = new GoogleDownloader();
                    string projectBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                    if (downloader.IsCacheExpired(value.RootPath, projectBaseDir))
                    {
                        string keyPath = GetKeyPathByEmail(value.GoogleApiKey);
                        if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
                        {
                            await downloader.DownloadToCacheAsync(value.RootPath, projectBaseDir, keyPath);
                        }
                    }
                    RefreshTreeView(value.Modules.FirstOrDefault()?.CheckedFiles ?? new List<string>());
                    SelectedModule = Modules.FirstOrDefault();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }

            else
            {
                RefreshTreeView(new List<string>());
                SelectedModule = Modules.FirstOrDefault();
            }
        }
        else
        {
            Modules.Clear();
            RootNode = null;
        }
        OnPropertyChanged(nameof(IsModuleSelectorEnabled));
        OnPropertyChanged(nameof(DisplayProjectName));
    }

    private string GetKeyPathByEmail(string email)
    {
        if (string.IsNullOrEmpty(email)) return string.Empty;

        string systemStorageDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServiceAccounts");
        if (!Directory.Exists(systemStorageDir)) return string.Empty;

        var files = Directory.GetFiles(systemStorageDir, "*.json");
        foreach (var file in files)
        {
            try
            {
                string content = File.ReadAllText(file);
                var json = JObject.Parse(content);
                if (json["client_email"]?.ToString() == email)
                {
                    return file; // Нашли физический файл ключа
                }
            }
            catch { }
        }
        return string.Empty;
    }

    // ОБНОВЛЕННАЯ КОМАНДА СИНХРОНИЗАЦИИ (Кнопка V): Принудительный сброс кэша и перечитывание
    // Внутри MainViewModel.cs
    [RelayCommand]
    private async Task SyncProjectSource()
    {
        if (SelectedProject == null || string.IsNullOrWhiteSpace(RootPath)) return;

        // Если текущий проект — Google Документ, выполняем жесткий перезапрос структуры из облака
        if (SelectedProject.Type == ProjectType.GoogleDoc)
        {
            string encryptedKey = Properties.Settings.Default.EncryptedGoogleApiKey ?? string.Empty;
            string apiKey = SecureCredentialStorage.DecryptString(encryptedKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                string authErr = Application.Current.Resources["Str_Err_GoogleAuthFailed"] as string
                                 ?? "Google API Key отсутствует или поврежден!";
                string authTitle = Application.Current.Resources["Str_Err_ValidationTitle"] as string
                                   ?? "Ошибка авторизации";
                MessageBox.Show(authErr, authTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Активируем полноэкранный адаптивный оверлей обновления структуры проекта
            IsProjectLoading = true;
            try
            {
                // Интеграция с подсистемой отображения статусов
                await StartProjectUpdateAsync(UpdateType.GoogleApiLoad, RootPath);

                var downloader = new GoogleDownloader();
                string projectBaseDir = AppDomain.CurrentDomain.BaseDirectory;

                // Принудительно качаем свежую структуру, игнорируя проверку IsCacheExpired
                bool success = await downloader.DownloadToCacheAsync(RootPath, projectBaseDir, apiKey);
                if (!success)
                {
                    string syncErr = Application.Current.Resources["Str_Err_GoogleDocUnavailable"] as string
                                     ?? "Не удалось обновить кэш из облака.";
                    string syncTitle = Application.Current.Resources["Str_Err_NetworkTitle"] as string
                                       ?? "Ошибка синхронизации";
                    MessageBox.Show(syncErr, syncTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            finally
            {
                IsProjectLoading = false;
                IsUpdateOverlayVisible = false; // Гарантированно гасим оверлей обновления
            }
        }

        // Полностью перестраиваем синтаксическое дерево (WPF перерисует ноды на экране на лету)
        RefreshTreeView(SelectedModule?.CheckedFiles ?? new List<string>());
        if (RootNode != null && SelectedModule != null)
        {
            FastPreloadSavedEntries(SelectedModule, RootNode);
        }

        string successMsg = Application.Current.Resources["Str_Status_ProjectSaved"] as string
                            ?? "Синхронизация структуры успешно завершена!";
        string successTitle = Application.Current.Resources["Str_Msg_EmailCopiedTitle"] as string
                              ?? "Успех";
        MessageBox.Show(successMsg, successTitle, MessageBoxButton.OK, MessageBoxImage.Information);
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
    // Внутри MainViewModel.cs
    // Внутри MainViewModel.cs

    // ИСПРАВЛЕНО: Изменено на асинхронное выполнение для безопасного ожидания парсинга без ContinueWith
    private async void RefreshTreeView(List<string> checkedFiles)
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
                string googleDocId = GoogleDownloader.ExtractDocumentId(RootPath);
                string googleCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache", $"{googleDocId}.json");

                RootNode = new FileSystemNode
                {
                    Name = SelectedProject.ProjectName,
                    FullPath = googleCachePath,
                    RelativePath = googleCachePath,
                    IsFile = false
                };

                try
                {
                    RootNode.Children.Clear();
                    // Создаем временную техническую ноду-заглушку
                    RootNode.Children.Add(new FileSystemNode { Name = "LoadingStub...", Parent = RootNode, IsFile = true });

                    // ИСПРАВЛЕНО: Прямое и безопасное ожидание задачи парсинга вместо ContinueWith
                    await PopulateSyntaxNodesAsync(RootNode);

                    // Код ниже гарантированно выполнится в основном UI-потоке после успешного парсинга
                    if (RootNode != null)
                    {
                        RootNode.VerifyCheckState();
                        if (SelectedModule != null)
                        {
                            FastPreloadSavedEntries(SelectedModule, RootNode);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[Google Sync] Парсинг структуры был отменен.");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Google Sync Parser Break] {ex.Message}");
                }
                break;

            case ProjectType.WordDoc:
                RootNode = new FileSystemNode
                {
                    Name = Path.GetFileName(RootPath),
                    FullPath = RootPath,
                    RelativePath = Path.GetFileName(RootPath),
                    IsFile = false
                };
                RootNode.Children.Clear();
                RootNode.Children.Add(new FileSystemNode { Name = "LoadingStub...", Parent = RootNode });
                break;

            default:
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

        OnPropertyChanged(nameof(RootNode));
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
    // ================================================================= -->
    // ИСПРАВЛЕНО: ЖЕСТКАЯ СИНХРОНИЗАЦИЯ ПУТЕЙ БЕЗ ИСКАЖЕНИЯ И ДУБЛИРОВАНИЯ-->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО: ПОЛНАЯ АДАПТИВНАЯ ОЧИСТКА И СИНХРОНИЗАЦИЯ СТРУКТУРЫ    -->
    // ================================================================= -->
    public void SyncTreeWithModule()
    {
        if (SelectedModule == null || RootNode == null) return;

        var checkedFilesList = new List<string>();
        var checkedEntriesList = new List<SyntaxEntry>();

        // 1. ОПРЕДЕЛЯЕМ ТИП ПРОЕКТА ДЛЯ ИЗОЛЯЦИИ ЛОГИКИ ТЕКСТА И КОДА
        bool isTextProject = SelectedProject?.Type == ProjectType.GoogleDoc || SelectedProject?.Type == ProjectType.WordDoc;

        if (!isTextProject)
        {
            // ЛОКАЛЬНЫЙ ПРОЕКТ (КОД C#): Набиваем CheckedFiles списком путей к файлам на диске
            var flatFilesList = new List<FileSystemNode>();
            ContextBuilderService.GetCheckedFiles(RootNode, flatFilesList);
            foreach (var node in flatFilesList)
            {
                checkedFilesList.Add(node.RelativePath);
            }
        }
        else
        {
            // КНИЖНЫЙ ПРОЕКТ (GOOGLE DOCS): CheckedFiles остается чистым и пустым [], 
            // так как ссылка на документ уже лежит в RootPath на верхнем уровне JSON!
        }

        // 2. СБОР ОТМЕЧЕННЫХ ГАЛОЧКАМИ СИНТАКСИЧЕСКИХ НОД (ВКЛАДОК И ГЛАВ)
        var flatSyntaxList = new List<FileSystemNode>();
        CollectAllCheckedSyntaxNodes(RootNode, flatSyntaxList);

        foreach (var node in flatSyntaxList)
        {
            // Оптимизация: Для книг зануляем локальный жесткий FilePath, избавляя JSON от раздувания
            string effFilePath = isTextProject ? string.Empty : node.RelativePath;

            // Берем оригинальный EntryPath, который построил наш JSON-парсер
            string cleanEntryPath = node.EntryPath;

            checkedEntriesList.Add(new SyntaxEntry
            {
                FilePath = effFilePath,
                EntryPath = cleanEntryPath,
                DisplayName = node.Name,
                Type = node.SyntaxType,
                SpanInfo = node.SyntaxSpanInfo
            });
        }

        // 3. ФИКСИРУЕМ ОЧИЩЕННЫЕ ДАННЫЕ В ТЕКУЩЕМ МОДУЛЕ ПРОЕКТА
        SelectedModule.CheckedFiles = checkedFilesList;
        SelectedModule.CheckedEntries = checkedEntriesList;
    }


    // ИСПРАВЛЕНО: Точечная очистка путей внутри ExecuteSave перед записью JSON
    // ================================================================= -->
    // ВОССТАНОВЛЕНО: ЧИСТОЕ МОНОЛИТНОЕ СОХРАНЕНИЕ БЕЗ ХАРДКОДА СТРОК    -->
    // ================================================================= -->
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
                if (showMessage)
                {
                    // ИСПРАВЛЕНО: Мультиязычный вызов предупреждения об пустом имени проекта
                    string warnMsg = Application.Current.Resources["Str_Msg_EnterProjectName"] as string
                        ?? "Введите имя проекта перед сохранением.";
                    string warnTitle = Application.Current.Resources["Str_Title_Warning"] as string
                        ?? "Внимание";
                    System.Windows.MessageBox.Show(warnMsg, warnTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }
        }

        if (SelectedModule != null && RootNode != null)
        {
            var checkedFilesList = new List<string>();
            bool isTextProject = SelectedProject.Type == ProjectType.GoogleDoc || SelectedProject.Type == ProjectType.WordDoc;

            if (!isTextProject)
            {
                var flatFilesList = new List<FileSystemNode>();
                ContextBuilderService.GetCheckedFiles(RootNode, flatFilesList);
                foreach (var node in flatFilesList)
                {
                    checkedFilesList.Add(node.RelativePath);
                }
            }

            var checkedEntriesList = new List<SyntaxEntry>();
            var flatSyntaxList = new List<FileSystemNode>();
            CollectAllCheckedSyntaxNodes(RootNode, flatSyntaxList);

            foreach (var node in flatSyntaxList)
            {
                string effFilePath = isTextProject ? string.Empty : node.RelativePath;
                string cleanEntryPath = node.EntryPath;

                checkedEntriesList.Add(new SyntaxEntry
                {
                    FilePath = effFilePath,
                    EntryPath = cleanEntryPath,
                    DisplayName = node.Name,
                    Type = node.SyntaxType,
                    SpanInfo = node.SyntaxSpanInfo
                });
            }

            SelectedModule.ModuleName = ModuleNameInput;
            SelectedModule.ContextFileName = GetSafeFileName(ModuleNameInput) + ".txt";
            SelectedModule.ModuleRules = ModuleRules;
            SelectedModule.CheckedFiles = checkedFilesList;
            SelectedModule.CheckedEntries = checkedEntriesList;

            var mIdx = Modules.IndexOf(SelectedModule);
            if (mIdx >= 0) Modules[mIdx] = SelectedModule;
        }

        if (SelectedProject == null) return;

        SelectedProject.ProjectName = ProjectNameInput;
        SelectedProject.RootPath = RootPath;
        SelectedProject.OutputPath = OutputPath;
        SelectedProject.PromptRules = PromptRules;
        SelectedProject.IncludeDirectoryStructure = IncludeDirectoryStructure;
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

            if (showMessage)
            {
                // ИСПРАВЛЕНО: Локализованное окно успешного сохранения
                string successMsg = Application.Current.Resources["Str_Msg_SaveSuccess"] as string
                    ?? "Всё успешно сохранено!";
                string successTitle = Application.Current.Resources["Str_Title_Success"] as string
                    ?? "Успех";
                System.Windows.MessageBox.Show(successMsg, successTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            if (showMessage)
            {
                // ИСПРАВЛЕНО: Локализованное окно системной ошибки ввода-вывода
                string errorPrefix = Application.Current.Resources["Str_Err_SaveJson"] as string
                    ?? "Ошибка сохранения JSON";
                string errorTitle = Application.Current.Resources["Str_Err_GeneralErrorTitle"] as string
                    ?? "Ошибка";
                System.Windows.MessageBox.Show($"{errorPrefix}: {ex.Message}", errorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
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
            errorMessage = Application.Current.Resources["Str_Err_Update_ReadDir"] as string
                           ?? "Имя не может быть пустым или состоять только из пробелов.";
            return false;
        }

        char[] invalidChars = System.IO.Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars)
        {
            if (name.Contains(c))
            {
                string errTemplate = Application.Current.Resources["Str_Err_InvalidServiceAccount"] as string
                                     ?? "Имя содержит недопустимый символ '{0}'.";
                errorMessage = string.Format(errTemplate, c);
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

    // Рекурсивный сбор всех выбранных элементов кода для сохранения в JSON
    // ИСПРАВЛЕНО: Рекурсивный сбор синтаксических нод БЕЗ мусора и дубликатов
    // Внутри MainViewModel.cs
    // ================================================================= -->
    // ИСПРАВЛЕНО: АДАТИВНЫЙ СБОР СИНТАКСИСА ПО ТИПАМ ПРОЕКТОВ            -->
    // ================================================================= -->
    private void CollectAllCheckedSyntaxNodes(FileSystemNode node, List<FileSystemNode> result)
    {
        if (node == null) return;

        if (node.IsSyntaxNode && (node.IsChecked == true || node.IsChecked == null))
        {
            // Проверяем тип текущего проекта, чтобы не сломать сохранение исходного кода
            if (SelectedProject?.Type == ProjectType.GoogleDoc || SelectedProject?.Type == ProjectType.WordDoc)
            {
                // ДЛЯ КНИГ: Сохраняем любые отмеченные элементы (и вкладки, и главы-листья)
                result.Add(node);
            }
            else
            {
                // ДЛЯ КОДА С#: Сохраняем только промежуточные ноды-контейнеры (!node.IsFile)
                if (!node.IsFile)
                {
                    result.Add(node);
                }
            }
        }

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
    [RelayCommand]
    private void BrowseProjectOutputFolder()
    {
        // Открываем ваш стандартный диалог выбора папок
        var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            // ИСПРАВЛЕНО: Записываем путь в буферное поле оверлея, активируя валидацию!
            EditProjectOutputPathInput = dialog.FileName;
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
    // ================================================================= -->
    // ЧАСТЬ 1: ИСПРАВЛЕННАЯ КОМАНДА ГЕНЕРАЦИИ С ЗАПУСКОМ ОВЕРЛЕЯ PROGRESS-->
    // ================================================================= -->
    // Внутри MainViewModel.cs

    // ИСПРАВЛЕНО: Привязываем команду к методу проверки прав на выполнение
    [RelayCommand(CanExecute = nameof(CanGenerateContext))]
    private async Task GenerateContext()
    {
        if (SelectedModule == null || RootNode == null) return;

        // Считываем все галочки с экрана прямо в модель модуля перед сборкой
        SyncTreeWithModule();
        string extension = IsPdfFormat ? ".pdf" : ".txt";
        string safeFileName = GetSafeFileName(ModuleNameInput) + extension;

        if (string.IsNullOrWhiteSpace(OutputPath) || string.IsNullOrWhiteSpace(ModuleNameInput))
        {
            System.Windows.MessageBox.Show("Не заполнены критические данные: выходная папка или имя модуля.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var checkedFiles = new List<FileSystemNode>();
        ContextBuilderService.GetCheckedFilesExtended(RootNode, checkedFiles);

        if (checkedFiles.Count == 0)
        {
            System.Windows.MessageBox.Show("Не выбрано ни одного фрагмента структуры для нарезки контекста.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Взводим флаги оверлея
        IsProcessing = true;
        ProgressValue = 0;
        ProgressMax = checkedFiles.Count;
        ProgressText = $"Подготовка к обработке {checkedFiles.Count} элементов...";
        CurrentFileText = "";
        _cts = new CancellationTokenSource();

        var progressHandler = new Progress<ProgressReport>(report =>
        {
            ProgressValue = report.CurrentIndex;
            ProgressText = $"Обработано: {report.CurrentIndex} из {report.TotalCount}";
            CurrentFileText = $"Раздел: {report.CurrentFileName}";
        });

        try
        {
            string fullPath = Path.Combine(OutputPath, safeFileName);
            if (IsPdfFormat)
            {
                await ContextBuilderService.GeneratePdfContextFileAsync(OutputPath, safeFileName, PromptRules, ModuleRules, IncludeDirectoryStructure, RootNode, SelectedModule.CheckedEntries, progressHandler, _cts.Token);
            }
            else
            {
                await ContextBuilderService.GenerateContextFileAsync(OutputPath, safeFileName, PromptRules, ModuleRules, IncludeDirectoryStructure, RootNode, SelectedModule.CheckedEntries, progressHandler, _cts.Token);
            }

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
            IsProcessing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // Новый обязательный метод проверки для CanExecute
    private bool CanGenerateContext()
    {
        // Кнопка заблокирована, если идет загрузка проекта, обработка или пересчет размера токенов
        if (IsProjectLoading || IsProcessing || IsCalculatingSize) return false;

        // Кнопка активна только если выбран модуль и построено дерево
        return SelectedModule != null && RootNode != null;
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

        // ================================================================= -->
        // ИСПРАВЛЕНО: Интеллектуальное определение расширения для кэша Google Docs-->
        // ================================================================= -->
        // Если файл лежит в папке кэша googlecache или имеет виртуальное имя, 
        // принудительно выставляем ему расширение .gdoc, чтобы фабрика нашла нужный парсер
        string ext = System.IO.Path.GetExtension(fileNode.FullPath);
        if (fileNode.RelativePath.EndsWith(".gdoc", StringComparison.OrdinalIgnoreCase) ||
            fileNode.FullPath.Contains("googlecache"))
        {
            ext = ".gdoc";
        }

        // Теперь фабрика увидит легитимный .gdoc и не сбросит выполнение!
        if (!SyntaxParserFactory.IsSupported(ext)) return;

        var parser = SyntaxParserFactory.GetParser(ext);
        if (parser == null) return;

        // Сюда выполнение теперь гарантированно дойдет, и точка останова загорится!
        List<SyntaxEntry> entries = await parser.ParseFileAsync(fileNode.FullPath, fileNode.RelativePath);


        // Строим словарь уже существующих в UI виртуальных нод, чтобы не дублировать их при парсинге
        var existingNodes = new Dictionary<string, FileSystemNode>(StringComparer.Ordinal);
        BuildExistingNodesMap(fileNode, existingNodes);

        // ================================================================= -->
        // ИСПРАВЛЕНО: ПОДКЛЮЧЕНИЕ МУЛЬТИЯЗЫЧНЫХ РЕСУРСОВ ДЛЯ ПРОЗЫ         -->
        // ================================================================= -->
        // ================================================================= -->
        // ЧАСТЬ 2: ИЕРАРХИЧЕСКОЕ ПОДСЕЛЕНИЕ ГЛАВ ВНУТРЬ РОДИТЕЛЬСКИХ ВКЛАДОК -->
        // ================================================================= -->
        foreach (var entry in entries)
        {
            if (existingNodes.ContainsKey(entry.EntryPath)) continue;

            // СТРОГОЕ ПРАВИЛО: Вкладка (Tab) — это папка со стрелочкой (IsFile = false).
            // Глава рассказа (Heading) или текстовая секция — это конечный элемент (IsFile = true).
            bool isLeaf = (entry.Type == EntryType.Heading || entry.Type == EntryType.Section);

            var newNode = new FileSystemNode
            {
                Name = entry.DisplayName.Trim(), // Чистое название без запекания префиксов
                RelativePath = entry.FilePath,
                FullPath = fileNode.FullPath,
                IsFile = isLeaf, // Разделяем поведение папок и файлов в UI
                IsSyntaxNode = true,
                SyntaxType = entry.Type,
                SyntaxSpanInfo = entry.SpanInfo,
                EntryPath = entry.EntryPath,
                Parent = fileNode,
                // АВТОРАЗВОРАЧИВАНИЕ: Если узел является вкладкой GoogleDoc, заставляем UI раскрыть его
                IsExpanded = (entry.Type == EntryType.Tab)
            };

            // Ищем родителя по косой черте (/) литературных вкладок рассказов
            string parentEntryPath = string.Empty;
            int lastSeparator = entry.EntryPath.LastIndexOf('/');

            if (lastSeparator > 0)
            {
                parentEntryPath = entry.EntryPath.Substring(0, lastSeparator);
            }

            // Пытаемся подселить к существующей родительской вкладке в UI
            if (!string.IsNullOrEmpty(parentEntryPath) && existingNodes.TryGetValue(parentEntryPath, out var parentUiNode))
            {
                newNode.Parent = parentUiNode;
                parentUiNode.Children.Add(newNode);
            }
            else
            {
                // Если родительской вкладки нет, это корень проекта
                fileNode.Children.Add(newNode);
            }

            // Регистрируем ноду в карте, чтобы её могли найти её будущие дети (подглавы или главы)
            existingNodes[entry.EntryPath] = newNode;

            // Восстановление галочек
            if (fileNode.IsChecked == true || (newNode.Parent != null && newNode.Parent.IsChecked == true))
            {
                newNode.SetChecked(true, updateChildren: false, updateParent: false);
            }
        }

        fileNode.SetChecked(fileNode.IsChecked, updateChildren: false, updateParent: true);

    }

    // Вспомогательный метод для сбора карты уже существующих UI нод в файле
    // Внутри MainViewModel.cs
    private void BuildExistingNodesMap(FileSystemNode node, Dictionary<string, FileSystemNode> map)
    {
        if (node == null) return;

        // ИСПРАВЛЕНО: Регистрируем узел, если у него заполнен синтаксический путь, 
        // либо если это корневой узел виртуального файла GoogleDoc/WordDoc
        if (!string.IsNullOrEmpty(node.EntryPath))
        {
            map[node.EntryPath] = node;
        }
        else if (!node.IsFile && node.IsSyntaxNode == false && !string.IsNullOrEmpty(node.Name))
        {
            // Фоллбэк-маркер для корневого контейнера книги
            map[node.Name] = node;
        }

        // Делаем локальную копию коллекции детей для безопасного прохода без конфликтов потоков
        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
        {
            BuildExistingNodesMap(child, map);
        }
    }



    // Внутри MainViewModel.cs
    // Внутри MainViewModel.cs
    // ================================================================= -->
    // ИСПРАВЛЕНО: БЕЗОПАСНЫЙ НАКАТ ГАЛОЧЕК ДЛЯ ПРОЗЫ БЕЗ ДУБЛИРОВАНИЯ НОД-->
    // ================================================================= -->
    private void FastPreloadSavedEntries(ContextModule module, FileSystemNode fileNode)
    {
        if (module == null || fileNode == null) return;

        // СЦЕНАРИЙ А: Для текстовых проектов (GoogleDoc / WordDoc) работаем по эталонному дереву
        // СЦЕНАРИЙ А: Для текстовых проектов (GoogleDoc / WordDoc) работаем по эталонному дереву
        if (SelectedProject?.Type == ProjectType.GoogleDoc || SelectedProject?.Type == ProjectType.WordDoc)
        {
            if (module.CheckedEntries == null || module.CheckedEntries.Count == 0) return;

            var savedPaths = new HashSet<string>(
                module.CheckedEntries.Select(e => e.EntryPath.Replace('\\', '/')),
                StringComparer.OrdinalIgnoreCase
            );

            ApplyCheckedStatesFromConfigRecursive(fileNode, savedPaths);

            // ИСПРАВЛЕНО: Запускаем глубокий каскадный пересчет состояний снизу вверх 
            // для абсолютно всех уровней вложенности вкладок и подвкладок!
            DeepVerifyAllCheckStates(fileNode);
            return;
        }


        // СЦЕНАРИЙ Б: Старая исходная логика генерации нод для проектов с исходным кодом C#
        var savedEntries = module.CheckedEntries;
        if (savedEntries == null || savedEntries.Count == 0) return;

        fileNode.Children.Clear();
        var nodesMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in savedEntries)
        {
            bool isLeaf = (entry.Type == EntryType.Heading || entry.Type == EntryType.Section);

            var newNode = new FileSystemNode
            {
                Name = entry.DisplayName.Trim(),
                RelativePath = entry.FilePath,
                FullPath = fileNode.FullPath,
                IsFile = isLeaf,
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

            if (!string.IsNullOrEmpty(parentEntryPath) && nodesMap.TryGetValue(parentEntryPath, out var parentUiNode))
            {
                newNode.Parent = parentUiNode;
                parentUiNode.Children.Add(newNode);
            }
            else
            {
                fileNode.Children.Add(newNode);
            }
            nodesMap[entry.EntryPath] = newNode;

            newNode.SetChecked(true, updateChildren: false, updateParent: false);
        }

        fileNode.VerifyCheckState();
    }
    /// <summary>
    /// Рекурсивный каскадный пересчет состояний чекбоксов Tri-State снизу вверх
    /// </summary>
    private void DeepVerifyAllCheckStates(FileSystemNode node)
    {
        if (node == null) return;

        // Сначала уходим в самую глубь дерева к последним потомкам
        foreach (var child in node.Children)
        {
            DeepVerifyAllCheckStates(child);
        }

        // Когда вернулись снизу, принудительно заставляем текущий узел 
        // пересчитать свой статус на основе реального состояния его детей
        node.VerifyCheckState();
    }

    // Вспомогательный рекурсивный метод поиска нод по EntryPath и активации галочек
    // ================================================================= -->
    // ИСПРАВЛЕНО: ДВУХКАНАЛЬНАЯ ПРОВЕРКА ПУТЕЙ (С ТОЧКОЙ И СО СЛЭШЕМ)    -->
    // ================================================================= -->
    private void ApplyCheckedStatesFromConfigRecursive(FileSystemNode node, HashSet<string> savedPaths)
    {
        if (node == null) return;

        // 1. Нормализуем путь текущего узла из дерева (заменяем наклоны слэшей)
        string cleanNodePath = node.EntryPath.Replace('\\', '/');

        // 2. ИСПРАВЛЕНО: Создаем альтернативный вариант пути на случай, если в JSON 
        // точка на конце превратилась в слэш: "Среди людей - основной текст/Глава 1/"
        string alternativeNodePath = cleanNodePath;
        if (cleanNodePath.EndsWith("."))
        {
            // Отрезаем точку и принудительно дописываем слэш, имитируя баг сохранения
            alternativeNodePath = cleanNodePath.Substring(0, cleanNodePath.Length - 1) + "/";
        }

        // 3. Проверяем вхождение по любому из двух каналов адресации
        if (node.IsSyntaxNode && (savedPaths.Contains(cleanNodePath) || savedPaths.Contains(alternativeNodePath)))
        {
            node.SetChecked(true, updateChildren: false, updateParent: false);
        }

        // Рекурсивно уходим вглубь по коллекции вкладок и глав романа
        foreach (var child in node.Children)
        {
            ApplyCheckedStatesFromConfigRecursive(child, savedPaths);
        }
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
    // Новые свойства для отображения точного числового прогресса в оверлее
    [ObservableProperty] private int _currentProgressValue = 0;
    [ObservableProperty] private string _currentProgressStatus = string.Empty;
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

        // ИСПРАВЛЕНО: Строго определяем тип проекта по актуальным радиокнопкам оверлея
        ProjectType selectedType = ProjectType.Folder;
        if (IsGoogleDocSelected)
        {
            selectedType = ProjectType.GoogleDoc;
        }
        else if (IsWordDocSelected)
        {
            selectedType = ProjectType.WordDoc;
        }
        else if (IsLocalFolderSelected)
        {
            selectedType = ProjectType.Folder;
        }

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
                string idErr = Application.Current.Resources["Str_Err_InvalidServiceAccount"] as string ?? "Строка не содержит валидный ID!";
                MessageBox.Show(idErr, "ID Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsProjectLoading = true;
            try
            {
                // Подгружаем текст состояния из словаря локализации
                ProgressValue = 40;
                ProgressText = Application.Current.Resources["Str_Update_LoadingGoogleDoc"] as string ?? "Скачивание структуры из облака Google...";

                var downloader = new GoogleDownloader();
                string projectBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                bool success = await downloader.DownloadToCacheAsync(rootPath, projectBaseDir, EditGoogleApiKeyInput?.Trim() ?? string.Empty);

                if (!success)
                {
                    IsProjectLoading = false;
                    string errorMsg = Application.Current.Resources["Str_Err_GoogleDocUnavailable"] as string ?? "Google Документ недоступен!";
                    string errorTitle = Application.Current.Resources["Str_Err_NetworkTitle"] as string ?? "Ошибка сети";
                    MessageBox.Show(errorMsg, errorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                ProgressValue = 100;
                ProgressText = Application.Current.Resources["Str_Update_BuildingTree"] as string ?? "Структура успешно синхронизирована!";
            }
            finally
            {
                IsProjectLoading = false;
            }
        }

        // ... Завершение сохранения в модель конфигурации проекта и SilentSave ...
        ProjectConfig? targetProject = SelectedProject;
        if (targetProject == null || EditProjectNameInput != targetProject.ProjectName)
        {
            targetProject = new ProjectConfig();
            Projects.Add(targetProject);
        }
        targetProject.ProjectName = projName;
        targetProject.RootPath = rootPath;
        targetProject.OutputPath = outPath;
        targetProject.Type = selectedType;
        IsProjectOverlayVisible = false;
        SelectedProject = targetProject;

        RefreshTreeView(targetProject.Modules.FirstOrDefault()?.CheckedFiles ?? new List<string>());
        SilentSave();

    }
    // Изолированное буферное поле для безопасного ввода ключа в интерфейсе оверлея
    [ObservableProperty] private string _editGoogleApiKeyInput = string.Empty;

    // ================================================================= -->
    // ЧАСТЬ 1: РЕСТРУКТУРИЗАЦИЯ ОВЕРЛЕЕВ (ЧИСТАЯ ПРИВЯЗКА К CONFIG)      -->
    // ================================================================= -->

    // 1. Команда создания нового проекта: выставляет дефолтный аккаунт из настроек приложения
    [RelayCommand]
    private void CreateNewProjectConfig()
    {
        // Сканируем системную папку, наполняя AvailableServiceAccounts чистыми Email
        LoadAvailableServiceAccounts();

        EditProjectNameInput = string.Empty;
        EditProjectRootPathInput = string.Empty;
        EditProjectOutputPathInput = string.Empty;

        // Подтягиваем сохраненный по умолчанию Email из общих настроек приложения
        string defaultEmail = Properties.Settings.Default.DefaultServiceAccountEmail ?? string.Empty;

        // Если этот Email физически существует в нашей папке ключей — выбираем его
        if (!string.IsNullOrEmpty(defaultEmail) && AvailableServiceAccounts.Contains(defaultEmail))
        {
            SelectedServiceAccount = defaultEmail;
        }
        else
        {
            // Если настроек нет или ключ удален — берем самый первый доступный из списка
            SelectedServiceAccount = AvailableServiceAccounts.FirstOrDefault() ?? string.Empty;
        }

        IsLocalFolderSelected = true;
        IsGoogleDocSelected = false;
        IsWordDocSelected = false;
        IsProjectOverlayVisible = true;
    }

    // 2. Команда редактирования проекта (Ромбик ◊): строго считывает ключ из ProjectConfig
    [RelayCommand]
    private void ShowProjectSettings()
    {
        if (SelectedProject == null) return;

        LoadAvailableServiceAccounts();

        EditProjectNameInput = SelectedProject.ProjectName;
        EditProjectRootPathInput = SelectedProject.RootPath;
        EditProjectOutputPathInput = SelectedProject.OutputPath;

        IsLocalFolderSelected = (SelectedProject.Type == ProjectType.Folder);
        IsGoogleDocSelected = (SelectedProject.Type == ProjectType.GoogleDoc);
        IsWordDocSelected = (SelectedProject.Type == ProjectType.WordDoc);

        // СТРОГАЯ ПРИВЯЗКА: Вытаскиваем Email аккаунта напрямую из конфигурации выбранного проекта!
        if (SelectedProject.Type == ProjectType.GoogleDoc && !string.IsNullOrEmpty(SelectedProject.GoogleApiKey))
        {
            SelectedServiceAccount = SelectedProject.GoogleApiKey;
        }
        else
        {
            SelectedServiceAccount = AvailableServiceAccounts.FirstOrDefault() ?? string.Empty;
        }

        IsProjectOverlayVisible = true;
    }


    // НОВАЯ КОМАНДА: Безопасный запуск браузера на странице создания ключей Google Console
    [RelayCommand]
    private void OpenGoogleConsole()
    {
        try
        {
            string url = "https://console.cloud.google.com";

            // В .NET Core / .NET 9 для Process.Start требуется флаг UseShellExecute = true
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть браузер: {ex.Message}", "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }



    // ОБНОВЛЕННАЯ КОМАНДА ИМПОРТА: С контролем дубликатов и автовыбором
    [RelayCommand]
    private void BrowseGoogleAuthJson()
    {
        string filterStr = System.Windows.Application.Current.Resources["Str_Dialog_JsonFilter"] as string ?? "Ключ Google API (*.json)|*.json";
        string titleStr = System.Windows.Application.Current.Resources["Str_Dialog_JsonTitle"] as string ?? "Выберите JSON-ключ";

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = filterStr,
            Title = titleStr
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                string fileContent = File.ReadAllText(dialog.FileName);
                var json = JObject.Parse(fileContent);
                string clientEmail = json["client_email"]?.ToString() ?? string.Empty;
                string projectId = json["project_id"]?.ToString() ?? "project";

                if (string.IsNullOrEmpty(clientEmail))
                {
                    string msg = System.Windows.Application.Current.Resources["Str_Err_InvalidServiceAccount"] as string ?? "Ошибка валидации ключа.";
                    string title = System.Windows.Application.Current.Resources["Str_Err_ValidationTitle"] as string ?? "Ошибка";
                    MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string systemStorageDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServiceAccounts");
                if (!Directory.Exists(systemStorageDir)) Directory.CreateDirectory(systemStorageDir);

                // КОНТРОЛЬ ДУБЛИКАТОВ: Если такой Email уже импортирован, просто переключаемся на него
                if (AvailableServiceAccounts.Contains(clientEmail))
                {
                    SelectedServiceAccount = clientEmail; // Триггерит OnSelectedServiceAccountChanged
                    return;
                }

                string targetFileName = $"{projectId}_account.json";
                string targetPath = Path.Combine(systemStorageDir, targetFileName);

                File.Copy(dialog.FileName, targetPath, overwrite: true);

                // Перечитываем хранилище
                LoadAvailableServiceAccounts();

                // ФИНАЛЬНЫЙ ШТРИХ: Делаем импортированный Email выбранным в ComboBox на лету!
                SelectedServiceAccount = clientEmail;
            }
            catch (Exception ex)
            {
                string msg = System.Windows.Application.Current.Resources["Str_Err_ImportFailed"] as string ?? "Не удалось импортировать ключ:";
                string title = System.Windows.Application.Current.Resources["Str_Err_ImportTitle"] as string ?? "Ошибка";
                MessageBox.Show($"{msg} {ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }


    // ОБНОВЛЕННАЯ КОМАНДА КОПИРОВАНИЯ EMAIL ЧЕРЕЗ РЕСУРСЫ: Полностью локализована
    [RelayCommand]
    private void CopyServiceAccountEmailToClipboard()
    {
        if (string.IsNullOrEmpty(SelectedServiceAccount)) return;

        try
        {
            // Нам больше не нужно парсить JSON, Email уже выбран в ComboBox!
            Clipboard.SetText(SelectedServiceAccount);

            string rawTemplate = System.Windows.Application.Current.Resources["Str_Msg_EmailCopied"] as string ?? "Email скопирован: {0}";
            string title = System.Windows.Application.Current.Resources["Str_Msg_EmailCopiedTitle"] as string ?? "Успех";

            MessageBox.Show(string.Format(rawTemplate, SelectedServiceAccount), title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            string msg = System.Windows.Application.Current.Resources["Str_Err_ExtractEmailFailed"] as string ?? "Не удалось извлечь Email:";
            string title = System.Windows.Application.Current.Resources["Str_Err_GeneralErrorTitle"] as string ?? "Ошибка";
            MessageBox.Show($"{msg} {ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    // ИСПРАВЛЕНО: Явное объявление приватных полей для генератора MVVM свойств
    [ObservableProperty] private ObservableCollection<string> _availableServiceAccounts = new();
[ObservableProperty] private string _selectedServiceAccount = string.Empty;

    // ИСПРАВЛЕНО: Добавлен метод сканирования хранилища, который искал компилятор
    // ИСПРАВЛЕНО: Сканируем файлы и наполняем ComboBox чистыми Email-адресами
    private void LoadAvailableServiceAccounts()
    {
        AvailableServiceAccounts.Clear();
        string systemStorageDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServiceAccounts");

        if (!Directory.Exists(systemStorageDir))
        {
            Directory.CreateDirectory(systemStorageDir);
        }

        var files = Directory.GetFiles(systemStorageDir, "*.json");
        foreach (var file in files)
        {
            try
            {
                string content = File.ReadAllText(file);
                var json = JObject.Parse(content);
                string clientEmail = json["client_email"]?.ToString() ?? string.Empty;

                if (!string.IsNullOrEmpty(clientEmail) && !AvailableServiceAccounts.Contains(clientEmail))
                {
                    AvailableServiceAccounts.Add(clientEmail);
                }
            }
            catch { }
        }
    }

    // ИСПРАВЛЕНО: При выборе Email находим соответствующий ему файл на диске
    partial void OnSelectedServiceAccountChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            EditGoogleApiKeyInput = string.Empty;
            return;
        }

        string systemStorageDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServiceAccounts");
        var files = Directory.GetFiles(systemStorageDir, "*.json");
        string foundFullPath = string.Empty;

        // Бежим по всем файлам в поисках того, который содержит выбранный Email
        foreach (var file in files)
        {
            try
            {
                string content = File.ReadAllText(file);
                var json = JObject.Parse(content);
                if (json["client_email"]?.ToString() == value)
                {
                    foundFullPath = file;
                    break;
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(foundFullPath))
        {
            EditGoogleApiKeyInput = string.Empty;
            return;
        }

        // Записываем точный физический путь для сетевого Downloader
        EditGoogleApiKeyInput = foundFullPath;

        // Асинхронная проверка токена на лету
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Func<Task>(async () =>
        {
            try
            {
                string fileContent = await File.ReadAllTextAsync(foundFullPath);
                var json = JObject.Parse(fileContent);
                string clientEmail = json["client_email"]?.ToString() ?? string.Empty;
                string privateKeyRaw = json["private_key"]?.ToString() ?? string.Empty;

                if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKeyRaw)) return;

                // Здесь при необходимости можно оставить вызов GetGoogleAccessTokenAsync
            }
            catch { }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
    // ================================================================= -->
    // ЧАСТЬ 1: ЧИСТЫЙ СБОР ПУТЕЙ ДЛЯ СОХРАНЕНИЯ БЕЗ ИСКАЖЕНИЯ ТОЧКАМИ    -->
    // ================================================================= -->
    private void UpdateCheckedEntriesForCurrentModule()
    {
        if (SelectedModule == null || RootNode == null) return;

        // Очищаем старый список перед перезаписью
        SelectedModule.CheckedFiles.Clear();

        // Запускаем безопасный сбор путей по всему дереву нод
        CollectEntriesRecursive(RootNode, SelectedModule.CheckedFiles);
    }

    private void CollectEntriesRecursive(FileSystemNode node, List<string> checkedList)
    {
        if (node == null) return;

        // Если нода отмечена (или находится в неопределенном состоянии, но имеет синтаксис)
        if (node.IsSyntaxNode && (node.IsChecked == true || node.IsChecked == null))
        {
            // СТРОГОЕ ПРАВИЛО: Сохраняем оригинальный EntryPath, который построил парсер.
            // Никаких ручных склеек через точки! Парсер уже заложил туда "Вкладка/Глава 1."
            if (!checkedList.Contains(node.EntryPath))
            {
                checkedList.Add(node.EntryPath);
            }
        }

        // Идем глубже по дереву к дочерним элементам
        foreach (var child in node.Children)
        {
            CollectEntriesRecursive(child, checkedList);
        }
    }

    private CancellationTokenSource? _updateCts;
    private UpdateType _lastUpdateType;
    private string _lastTargetPath = string.Empty;

    // === СВОЙСТВА УПРАВЛЕНИЯ ОВЕРЛЕЕМ ===

    [ObservableProperty] private bool _isUpdateOverlayVisible;   // Видимость всего Grid-оверлея
    [ObservableProperty] private bool _isUpdateProcessing;       // Видимость полосы прогресса (ProgressBar)
    [ObservableProperty] private bool _isRetryButtonVisible;     // Видимость кнопки "Повторить"
    [ObservableProperty] private bool _isCancelButtonEnabled = true; // Активность кнопки "Отмена"
    [ObservableProperty] private string _updateOverlayMessage = string.Empty; // Текст внутри оверлея

    
    // === БИЗНЕС-ЛОГИКА ОБНОВЛЕНИЯ ===

    /// <summary>
    /// Публичный метод для инициализации процесса обновления проекта из любой точки приложения
    /// </summary>
  
    // Модифицированное свойство доступности селекторов и кнопок управления модулями
    public bool IsModuleSelectorEnabled
    {
        get
        {
            // Кнопки и комбобоксы блокируются, если проект находится в режиме загрузки/синхронизации (IsProjectLoading == true)
            if (IsProjectLoading) return false;

            return SelectedProject != null && Modules != null && Modules.Count > 0;
        }
    }

    // Дополнительное каноничное свойство для привязки к Command или IsEnabled кнопки генерации в XAML:
    public bool IsGenerateButtonEnabled
    {
        get
        {
            if (IsProjectLoading || IsProcessing || IsCalculatingSize) return false;
            return SelectedModule != null && RootNode != null;
        }
    }
    // Добавьте этот метод перехвата в MainViewModel.cs для мгновенного обновления кнопок в UI
    // Внутри MainViewModel.cs

    // Срабатывает при изменении статуса загрузки/синхронизации проекта Google Doc
    partial void OnIsProjectLoadingChanged(bool value)
    {
        GenerateContextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsModuleSelectorEnabled));
    }

    // Срабатывает при старте/окончании процесса сборки контекста
    partial void OnIsProcessingChanged(bool value)
    {
        GenerateContextCommand.NotifyCanExecuteChanged();
    }

    // Срабатывает при начале/окончании асинхронного подсчета токенов
    partial void OnIsCalculatingSizeChanged(bool value)
    {
        GenerateContextCommand.NotifyCanExecuteChanged();
    }
    // Внутри MainViewModel.cs

    [RelayCommand]
    private void DeleteCurrentProject(ProjectConfig? project)
    {
        var targetProject = project ?? SelectedProject;
        if (targetProject == null) return;

        string title = Application.Current.Resources["Str_Title_Confirmation"] as string ?? "Подтверждение";
        string rawMsg = Application.Current.Resources["Str_Msg_ConfirmDeleteProj"] as string ?? "Удалить проект \"{0}\"?";
        string message = string.Format(rawMsg, targetProject.ProjectName);

        var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            if (SelectedProject == targetProject)
            {
                SelectedProject = null;
            }
            Projects.Remove(targetProject);
            SilentSave();
        }
    }

    // === КОМАНДЫ (COMMUNITY TOOLKIT MVVM) ===

    // ИСПРАВЛЕНО: Явное публичное свойство для перехвата кликов backdrop
    [RelayCommand]
    public void Dummy()
    {
        // Пустой метод. Защищает центральное окноBorder от закрытия при клике на него.
    }

    [RelayCommand]
    public void CancelUpdate()
    {
        if (!IsCancelButtonEnabled) return;

        // Нажатие "Отмена" прерывает фоновую задачу (в т.ч. сетевой HttpClient)
        _updateCts?.Cancel();

        // Сброс видимости элементов
        IsUpdateOverlayVisible = false;
        IsUpdateProcessing = false;
        IsRetryButtonVisible = false;
    }

    [RelayCommand]
    public async Task RetryUpdate()
    {
        // ИСПРАВЛЕНО: Защита от дребезга кнопок и повторных параллельных запусков
        if (IsUpdateProcessing) return;
        await StartProjectUpdateAsync(_lastUpdateType, _lastTargetPath);
    }

    // === ИСПРАВЛЕННАЯ БИЗНЕС-ЛОГИКА ===
    // ================================================================= -->
    // ЧАСТЬ 2: ГАРАНТИРОВАННАЯ ИНИЦИАЛИЗАЦИЯ СТАТУСОВ И ВЫЗОВ GOOGLE API-->
    // ================================================================= -->
    public async Task StartProjectUpdateAsync(UpdateType type, string targetPath)
    {
        _lastUpdateType = type;
        _lastTargetPath = targetPath;
        _updateCts = new CancellationTokenSource();

        // 1. Инициализируем базовое состояние UI-оверлея
        IsUpdateOverlayVisible = true;
        IsUpdateProcessing = true;
        IsRetryButtonVisible = false;
        IsCancelButtonEnabled = true;

        // 2. ИСПРАВЛЕНО: Безопасное извлечение строк из словаря локализации ДО тяжелых задач
        string statusKey = type switch
        {
            UpdateType.ReadDirectory => "Str_Update_ReadDir",
            UpdateType.GoogleApiLoad => "Str_Update_GoogleApi",
            UpdateType.ConvertDocx => "Str_Update_ConvertDocx",
            _ => "Str_Calculating"
        };

        // Принудительно вытаскиваем и пингуем UI-поток текстом статуса
        UpdateOverlayMessage = System.Windows.Application.Current.Resources[statusKey] as string ?? "Обработка...";

        try
        {
            switch (type)
            {
                case UpdateType.ReadDirectory:
                    var fileSystem = new FileSystemService();
                    await fileSystem.ScanDirectoryAsync(targetPath);
                    break;

                case UpdateType.GoogleApiLoad:
                    IsCancelButtonEnabled = true;

                    if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                    {
                        throw new System.Net.WebException("No internet connection");
                    }

                    if (SelectedProject != null && SelectedProject.Type == ProjectType.GoogleDoc)
                    {
                        var downloader = new Google.GoogleDownloader();
                        string projectBaseDir = AppDomain.CurrentDomain.BaseDirectory;

                        // ИСПРАВЛЕНО: Защита от затирания Email. Если поле в проекте пустое — 
                        // принудительно восстанавливаем его из локального хранилища настроек приложения Settings!
                        string serviceAccountEmail = SelectedProject.GoogleApiKey;
                        if (string.IsNullOrWhiteSpace(serviceAccountEmail))
                        {
                            serviceAccountEmail = Properties.Settings.Default.DefaultServiceAccountEmail;
                            // Сразу восстанавливаем значение и в самом проекте, чтобы баг больше не повторялся
                            SelectedProject.GoogleApiKey = serviceAccountEmail;
                        }

                        // Теперь извлечение пути по Email сработает гарантированно!
                        string keyPath = GetKeyPathByEmail(serviceAccountEmail);

                        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
                        {
                            // Передаем keyPath в конструктор, чтобы в логах отладки (из прошлого шага) 
                            // вы точно видели, какой именно физический путь проверяет система
                            throw new FileNotFoundException("Файл ключа авторизации Google Docs не найден на диске.", keyPath ?? "ПОЛУЧЕНА_ПУСТАЯ_СТРОКА");
                        }

                        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_updateCts.Token))
                        {
                            linkedCts.CancelAfter(TimeSpan.FromSeconds(15));

                            bool success = await downloader.DownloadToCacheAsync(
                                SelectedProject.RootPath,
                                projectBaseDir,
                                keyPath);

                            if (!success)
                            {
                                throw new System.Net.WebException("Google Docs API returned failure.");
                            }
                        }
                    }
                    break;

                case UpdateType.ConvertDocx:
                    // Заглушка для будущего парсера Word
                    await Task.Delay(2000, _updateCts.Token);
                    break;
            }

            // Успешный исход — гасим оверлей
            IsUpdateOverlayVisible = false;
        }
        catch (OperationCanceledException)
        {
            IsUpdateOverlayVisible = false;
        }
        catch (FileNotFoundException fnfEx)
        {
            // ИСПРАВЛЕНО: Выводим в отладку точный путь, на котором споткнулась программа!
            System.Diagnostics.Debug.WriteLine($"[Google API Auth Error] Ключ не найден по пути: {fnfEx.FileName}");

            IsUpdateProcessing = false;
            IsCancelButtonEnabled = true;

            // Читаем из ресурсов строку об отсутствии доступа/ключа (или используем общую)
            string errKey = "Str_Err_GoogleDocUnavailable";
            UpdateOverlayMessage = System.Windows.Application.Current.Resources[errKey] as string
                ?? "Файл ключа авторизации Google Docs не найден на диске.";
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
        {
            System.Diagnostics.Debug.WriteLine($"[IO Error Detail] {ex.GetType().Name}: {ex.Message}");
            IsUpdateProcessing = false;
            IsCancelButtonEnabled = true;
            string errKey = (type == UpdateType.ConvertDocx) ? "Str_Err_Update_Convert" : "Str_Err_Update_ReadDir";
            UpdateOverlayMessage = System.Windows.Application.Current.Resources[errKey] as string ?? "Ошибка чтения/записи диска.";
        }
        catch (Exception ex) when (ex is System.Net.WebException || ex is TaskCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[Network Error Detail] {ex.Message}");
            IsUpdateProcessing = false;
            IsCancelButtonEnabled = true;
            IsRetryButtonVisible = true;
            UpdateOverlayMessage = System.Windows.Application.Current.Resources["Str_Err_Update_Network"] as string ?? "Ошибка сети.";
        }
    }


    // ================================================================= -->
    // ИСПРАВЛЕНО: ПОЛНАЯ МУЛЬТИЯЗЫЧНОСТЬ И ОЧИСТКА ХАРДКОДА СТРОК       -->
    // ================================================================= -->
    [RelayCommand]
    private async Task RecreateGoogleDoc()
    {
        if (SelectedProject == null || SelectedProject.Type != ProjectType.GoogleDoc) return;

        // 1. Извлекаем строго локализованные строки из XAML-ресурсов приложения
        string confirmMsg = Application.Current.Resources["Str_Msg_ConfirmRecreateGoogle"] as string
            ?? "Внимание! Это действие полностью удалит локальный кеш, очистит дерево и принудительно скачает документ заново. Продолжить?";

        // Используем ваш легитимный ключ заголовка из страницы 22/24 PDF кода
        string confirmTitle = Application.Current.Resources["Str_Title_Confirmation"] as string
            ?? "Подтверждение действия";

        // Показываем автору предупреждающее мультиязычное окно
        var dialogResult = MessageBox.Show(confirmMsg, confirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (dialogResult != MessageBoxResult.Yes) return;

        try
        {
            // 2. Уничтожаем локальный кэш
            string docId = Google.GoogleDownloader.ExtractDocumentId(SelectedProject.RootPath);
            if (!string.IsNullOrEmpty(docId))
            {
                string projectBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                string cacheDir = Path.Combine(projectBaseDir, "googlecache");

                string jsonCachePath = Path.Combine(cacheDir, $"{docId}.json");
                string metaCachePath = Path.Combine(cacheDir, $"{docId}.meta");

                if (File.Exists(jsonCachePath)) File.Delete(jsonCachePath);
                if (File.Exists(metaCachePath)) File.Delete(metaCachePath);
            }

            // 3. Обнуляем дерево и модули
            if (RootNode != null)
            {
                RootNode.Children.Clear();
                RootNode.Children.Add(new FileSystemNode { Name = "LoadingStub...", Parent = RootNode, IsFile = true });
            }

            if (SelectedModule != null)
            {
                SelectedModule.CheckedFiles?.Clear();
                SelectedModule.CheckedEntries?.Clear();
            }

            // ================================================================= -->
            // ЧАСТЬ 1: ИСПРАВЛЕННЫЙ ПОРЯДОК ОЖИДАНИЯ АСИНХРОННОГО СКАЧИВАНИЯ     -->
            // ================================================================= -->
            OnPropertyChanged(nameof(RootNode));

            // 4. ЗАПУСКАЕМ НАШ СЕТЕВОЙ GRID-ОВЕРЛЕЙ ДЛЯ СКАЧИВАНИЯ С НУЛЯ
            // ИСПРАВЛЕНО: Ждём (await) полного завершения скачивания и закрытия оверлея!
            await StartProjectUpdateAsync(UpdateType.GoogleApiLoad, SelectedProject.RootPath);

            // 5. ИСПРАВЛЕНО: Строим дерево ТЕПЕРЬ, когда файл гарантированно лежит в googlecache
            // Передаем пустой список выбранных файлов, так как мы полностью пересоздали проект
            RefreshTreeView(new List<string>());
        }
        catch (Exception ex)
        {
            string generalErrorTitle = Application.Current.Resources["Str_Err_GeneralErrorTitle"] as string ?? "Ошибка";
            MessageBox.Show($"{ex.Message}", generalErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }




}


