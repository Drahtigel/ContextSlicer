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
using System.Diagnostics;
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
    private IProjectTreeManager? _activeTreeManager;
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
    [ObservableProperty]
    private long _totalTokens; // Генератор MVVM автоматически создаст публичное свойство TotalTokens!

    // Внимательно проверьте написание этой переменной:
    [ObservableProperty]
    private ContextModule? _selectedModule;
    private bool _isUpdatingFields = false;
    [ObservableProperty] private string _newModuleNameInput = string.Empty;
    // Редактирование названия модуля
    [ObservableProperty] private bool _isEditOverlayVisible;
    [ObservableProperty] private string _editModuleNameInput = string.Empty;
    [ObservableProperty] private bool _editProjectIncludeImagesInput;
    [ObservableProperty] private bool _includeImages;
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
    // ================================================================= -->
    // ИСПРАВЛЕНО: АБСОЛЮТНЫЙ ПОЛИМОРФИЗМ ПОДСЧЕТА РАЗМЕРА ЧЕРЕЗ МЕНЕДЖЕР -->
    // ================================================================= -->
    private async Task RecalculateContextSizeAsync()
    {
        // 1. Сразу отменяем предыдущую задачу при дребезге чекбокса
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsCalculatingSize = true;

        // Фиксируем ссылки на правила и менеджер в локальные переменные до ухода в поток
        string localPromptRules = PromptRules ?? string.Empty;
        string localModuleRules = ModuleRules ?? string.Empty;
        bool localIncludeStructure = IncludeDirectoryStructure;
        var manager = _activeTreeManager;

        try
        {
            await Task.Run(async () =>
            {
                long contentCharacters = 0;

                // Если менеджер инициализирован — запрашиваем у него чистый вес выбранного контекста
                if (manager != null && manager.RootNode != null)
                {
                    contentCharacters = await manager.CalculateSelectedCharactersAsync(localIncludeStructure, token);
                }

                // Математически точный расчет итоговых весов с учетом правил
                long rulesLength = localPromptRules.Length + localModuleRules.Length;
                long totalCharacters = contentCharacters + rulesLength;

                // Рассчитываем токены на основе итогового суммарного объема символов
                long totalTokens = totalCharacters / 2;

                // Безопасно публикуем результаты на нижней панели интерфейса WPF
                if (!token.IsCancellationRequested)
                {
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TotalCharacters = totalCharacters;
                        EstimatedTokens = totalTokens; // Синхронизировано с XAML-привязкой ComputedState!
                    }));
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Context Size Calc Error] {ex.Message}");
        }
        finally
        {
            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                IsCalculatingSize = false;
            }));
        }
    }


    partial void OnPromptRulesChanged(string value) => _ = RecalculateContextSizeAsync();
    partial void OnModuleRulesChanged(string value) => _ = RecalculateContextSizeAsync();
    // Автоматический пересчет токенов при клике на чекбокс структуры каталогов
    partial void OnIncludeDirectoryStructureChanged(bool value) => _ = RecalculateContextSizeAsync();
    // Автоматический пересчет токенов при включении/выключении картинок на форме
    partial void OnIncludeImagesChanged(bool value) => _ = RecalculateContextSizeAsync();

    [RelayCommand]
    private void DeleteCurrentModule()
    {
        if (SelectedModule == null) return;

        // ИСПРАВЛЕНО: Полная локализация диалога удаления модуля из ресурсов
        string confirmMsg = Application.Current.Resources["Str_Msg_ConfirmDeleteModule"] as string
            ?? "Вы уверены, что хотите полностью удалить этот модуль?";
        string titleMsg = Application.Current.Resources["Str_Title_DeleteModule"] as string
            ?? "Удаление модуля";

        var result = MessageBox.Show(confirmMsg, titleMsg, MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            var moduleToRemove = SelectedModule;
            SelectedModule = null;

            Modules.Remove(moduleToRemove);
            OnPropertyChanged(nameof(IsModuleSelectorEnabled));

            SaveProject();
        }
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
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDir = Path.Combine(appData, "ContextSlicer");
        string projectsDir = Path.Combine(appDir, "projects");

        // Создаем всю иерархию папок упреждающе
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(projectsDir);

        // Точный и правильный путь к настройке шрифтов Windows в PDFsharp/MigraDoc
        PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true;

        // Оставляем провайдер кодировок для кириллицы
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // ... остальной код конструктора без изменений ...
       // string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
       // string appDir = Path.Combine(appData, "ContextSlicer");
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
        IsPdfFormat = true;
    }
    public string GetKeyPathByEmail(string email)
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

    // ================================================================= -->
    // ИСПРАВЛЕНО: КАТЕГОРИЧЕСКАЯ ВЫЧИСТКА ДУБЛИКАТОВ И СИНХРОНИЗАЦИЯ КЭША-->
    // ================================================================= -->
    [RelayCommand]
    private async Task SyncProjectSource()
    {
        if (SelectedProject == null || string.IsNullOrWhiteSpace(RootPath)) return;

        // 1. Если текущий проект — Google Документ, выполняем жесткий перезапрос структуры из облака
        // ================================================================= -->
        // ИСПРАВЛЕНО: ПЕРЕДАЧА ВАЛИДНОГО ПУТИ К ФАЙЛУ КЛЮЧА ДЛЯ ОБНОВЛЕНИЯ КЭША-->
        // ================================================================= -->
        // 1. Если текущий проект — Google Документ, выполняем жесткий перезапрос структуры из облака
        if (SelectedProject.Type == ProjectType.GoogleDoc)
        {
            // ИСПРАВЛЕНО: Вытаскиваем Email сервисного аккаунта, сохраненный в конфигурации проекта
            string serviceAccountEmail = SelectedProject.GoogleApiKey;
            if (string.IsNullOrWhiteSpace(serviceAccountEmail))
            {
                // Если поле пустое — берем дефолтный аккаунт из настроек приложения
                serviceAccountEmail = Properties.Settings.Default.DefaultServiceAccountEmail;
                SelectedProject.GoogleApiKey = serviceAccountEmail;
            }

            // ИСПРАВЛЕНО: Превращаем Email в честный физический путь к JSON-файлу на диске!
            string keyPath = GetKeyPathByEmail(serviceAccountEmail);

            if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            {
                string authErr = Application.Current.Resources["Str_Err_GoogleAuthFailed"] as string
                                 ?? "Файл ключа авторизации Google Docs не найден на диске.";
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

                // ИСПРАВЛЕНО: Передаем ВАЛИДНЫЙ путь keyPath вместо сырой текстовой строки apiKey!
                // Теперь File.Exists внутри загрузчика отработает идеально, и кэш обновится!
                bool success = await downloader.DownloadToCacheAsync(RootPath, projectBaseDir, keyPath);
                if (!success)
                {
                    string syncErr = Application.Current.Resources["Str_Err_GoogleDocUnavailable"] as string
                                     ?? "Не удалось обновить кэш из облака. Проверьте сеть или доступ к документу.";
                    string syncErrTitle = Application.Current.Resources["Str_Err_NetworkTitle"] as string
                                       ?? "Ошибка синхронизации";
                    MessageBox.Show(syncErr, syncErrTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            finally
            {
                IsProjectLoading = false;
                IsUpdateOverlayVisible = false; // Гарантированно гасим оверлей обновления
            }
        }
        // ================================================================= -->


        // 2. ПОЛИМОРФНЫЙ ПЕРЕЗАПУСК МЕНЕДЖЕРОВ (ИСПРАВЛЕНО): 
        // Полностью удален весь дублирующий мусор, который слепо вызывал ProjectTreeManager для всех подряд!
        // Теперь после обновления диска или облачного кэша строго создается нужный менеджер дерева.
        if (SelectedProject != null && SelectedModule != null)
        {
            bool isText = SelectedProject.Type == ProjectType.GoogleDoc || SelectedProject.Type == ProjectType.WordDoc;

            if (isText)
            {
                // Вызываем изолированный менеджер прозы для перечитывания свежего JSON-кэша книг
                var bookManager = new BookTreeManager(SelectedProject, SelectedModule);
                FileSystemNode.ActiveManager = null; // Для книг Roslyn-мост не нужен
                RootNode = bookManager.RootNode;
                _activeTreeManager = bookManager; // Фиксируем мост для мгновенного пересчета токенов!
            }
            else
            {
                // Вызываем технический менеджер для пересбора исходного кода проекта C# / SQL
                var codeManager = new ProjectTreeManager(SelectedProject, SelectedModule);
                FileSystemNode.ActiveManager = codeManager; // Взводим Roslyn-мост ленивой загрузки
                RootNode = codeManager.RootNode;
                _activeTreeManager = codeManager; // Фиксируем мост кода
            }

            // Уведомляем интерфейс WPF о замене корня дерева и принудительно пинаем асинхронный расчет токенов
            OnPropertyChanged(nameof(RootNode));
            _ = RecalculateContextSizeAsync();
        }

        string successMsg = Application.Current.Resources["Str_Status_ProjectSaved"] as string
                            ?? "Синхронизация структуры успешно завершена!";

        string successTitle = Application.Current.Resources["Str_Msg_EmailCopiedTitle"] as string
                              ?? "Успех";
        MessageBox.Show(successMsg, successTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }



    // ================================================================= -->
    // ИСПРАВЛЕНО: БЕЗОПАСНЫЙ СИНХРОННЫЙ ЗАПУСК РАЗДЕЛЕННЫХ МЕНЕДЖЕРОВ   -->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО: ПОЛНОЕ ИСКЛЮЧЕНИЕ ГОНКИ И ЗАЦИКЛИВАНИЯ ПРИ СМЕНЕ ПРОЕКТОВ -->
    // ================================================================= -->
    partial void OnSelectedProjectChanged(ProjectConfig? value)
    {
        // ЖЕСТКАЯ ЗАЩИТА: Если мы уже находимся внутри процесса обновления полей — выходим сразу
        if (_isUpdatingFields) return;

        if (value != null)
        {
            // 1. Просто обновляем локальные текстовые поля ввода на UI
            ProjectNameInput = value.ProjectName;
            RootPath = value.RootPath;
            OutputPath = value.OutputPath;
            PromptRules = value.PromptRules;
            IncludeImages = value.IncludeImages;
            IncludeDirectoryStructure = value.IncludeDirectoryStructure;

            // 2. Наполняем выпадающий список модулей нового проекта
            Modules = new ObservableCollection<ContextModule>(value.Modules);

            var defaultModule = value.Modules.FirstOrDefault();
            if (defaultModule != null)
            {
                // КРИТИЧЕСКИЙ ШТРИХ (ТВОЙ АЛГОРИТМ): Мы ПОЛНОСТЬЮ УДАЛИЛИ отсюда 
                // создание ProjectTreeManager и BookTreeManager! Метод больше не трогает RootNode.
                // Мы просто выбираем первый модуль. Это автоматически, чисто и последовательно
                // передаст управление в метод OnSelectedModuleChanged, исключая наложение потоков!
                SelectedModule = defaultModule;
            }
            else
            {
                SelectedModule = null;
                RootNode = null;
                _activeTreeManager = null;
                FileSystemNode.ActiveManager = null;
            }
        }
        else
        {
            Modules.Clear();
            SelectedModule = null;
            RootNode = null;
            _activeTreeManager = null;
            FileSystemNode.ActiveManager = null;
        }

        OnPropertyChanged(nameof(IsModuleSelectorEnabled));
        OnPropertyChanged(nameof(DisplayProjectName));
        OnPropertyChanged(nameof(RootNode));
    }

    partial void OnSelectedModuleChanged(ContextModule? value)
    {
        if (_isUpdatingFields) return;
        _isUpdatingFields = true;

        try
        {
            // Защищаем триггер от Race Condition при быстрой смене проектов в ListBox
            var activeProject = SelectedProject ?? Projects.FirstOrDefault(p => p != null && p.Modules.Contains(value!));

            if (value != null && activeProject != null)
            {
                // 1. Обновляем текстовые поля модуля
                ModuleNameInput = value.ModuleName;
                ContextFileName = value.ContextFileName;
                ModuleRules = value.ModuleRules;

                bool isTextProject = activeProject.Type == ProjectType.GoogleDoc || activeProject.Type == ProjectType.WordDoc;

                // 2. ЕДИНСТВЕННЫЙ ИСТОЧНИК ИСТИНЫ: Строим дерево строго в один поток
                if (isTextProject)
                {
                    // Литературный конвейер (GoogleDoc)
                    var bookManager = new BookTreeManager(activeProject, value);
                    FileSystemNode.ActiveManager = null; // Для книг Roslyn-мост не нужен
                    RootNode = bookManager.RootNode;
                    _activeTreeManager = bookManager; // Фиксируем интерфейсный контракт
                }
                else
                {
                    // Технический конвейер (Код C# / SQL)
                    var codeManager = new ProjectTreeManager(activeProject, value);
                    FileSystemNode.ActiveManager = codeManager; // Взводим Roslyn-мост ленивой загрузки
                    RootNode = codeManager.RootNode;
                    _activeTreeManager = codeManager; // Фиксируем интерфейсный контракт
                }
            }
            else if (value == null)
            {
                ModuleNameInput = string.Empty;
                ContextFileName = string.Empty;
                ModuleRules = string.Empty;
                RootNode = null;
                _activeTreeManager = null;
                FileSystemNode.ActiveManager = null;
            }

            // Уведомляем интерфейс и запускаем фоновый подсчет токенов
            OnPropertyChanged(nameof(RootNode));
            _ = RecalculateContextSizeAsync();
        }
        finally
        {
            _isUpdatingFields = false;
        }
    }




    [RelayCommand]
    private void CreateNewProject()
    {
        string name = string.IsNullOrWhiteSpace(ProjectNameInput)
            ? $"Проект {Projects.Count + 1}"
            : ProjectNameInput.Trim();

        if (!IsValidName(name, out string validationError))
        {
            MessageBox.Show(validationError, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ИСПРАВЛЕНО: Локализация проверки на дубликаты проектов
        bool isDuplicate = Projects.Any(p => p.ProjectName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (isDuplicate)
        {
            string errTemplate = Application.Current.Resources["Str_Err_ProjectDuplicate"] as string
                ?? "Проект с названием \"{0}\" уже существует!";
            string titleMsg = Application.Current.Resources["Str_Title_Warning"] as string
                ?? "Внимание";

            MessageBox.Show(string.Format(errTemplate, name), titleMsg, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newProject = new ProjectConfig { ProjectName = name };
        Projects.Add(newProject);
        SelectedProject = newProject;

        ProjectNameInput = string.Empty;
        IsProjectNameInvalid = false;
        SilentSave();
    }


    // ================================================================= -->
    // ИСПРАВЛЕНО: БЕЗОПАСНОЕ СОЗДАНИЕ МОДУЛЯ БЕЗ СБРОСА ВЫДЕЛЕНИЯ UI    -->
    // ================================================================= -->
    [RelayCommand]
    private void CreateNewModule()
    {
        if (SelectedProject == null)
        {
            // Вытаскиваем мультиязычные ресурсы из Strings.ru.xaml / Strings.en.xaml
            string selectProjMsg = System.Windows.Application.Current.Resources["Str_ProjNotSelected"] as string
                ?? "Сначала выберите или создайте проект!";
            string warnTitle = System.Windows.Application.Current.Resources["Str_Title_Warning"] as string
                ?? "Внимание";

            System.Windows.MessageBox.Show(selectProjMsg, warnTitle, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        string name = string.IsNullOrWhiteSpace(NewModuleNameInput)
            ? $"Модуль {Modules.Count + 1}"
            : NewModuleNameInput.Trim();

        bool isDuplicate = SelectedProject.Modules.Any(m => m.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (isDuplicate)
        {
            // Вытаскиваем шаблон сообщения об ошибке дубликата из ресурсов
            string duplicateMsg = System.Windows.Application.Current.Resources["Str_Err_ModuleDuplicate"] as string
                ?? "Модуль с таким названием уже существует в этом проекте!";
            string warnTitle = System.Windows.Application.Current.Resources["Str_Title_Warning"] as string
                ?? "Внимание";

            System.Windows.MessageBox.Show(duplicateMsg, warnTitle, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var newModule = new ContextModule
        {
            ModuleName = name,
            ContextFileName = $"{GetSafeFileName(name)}.txt",
            CheckedFiles = new List<string>(),
            CheckedEntries = new List<SyntaxEntry>()
        };

        // Замораживаем UI триггеры, чтобы избежать циклической перезаписи полей
        _isUpdatingFields = true;
        try
        {
            // Добавляем строго в коллекцию текущего проекта
            SelectedProject.Modules.Add(newModule);
            Modules.Add(newModule);

            // Пересоздаем технический корень для изоляции дерева
            RootNode = new FileSystemNode
            {
                Name = System.IO.Path.GetFileName(SelectedProject.RootPath),
                FullPath = SelectedProject.RootPath,
                IsFile = false
            };

            OnPropertyChanged(nameof(RootNode));
            var manager = new ProjectTreeManager(SelectedProject, newModule);
            FileSystemNode.ActiveManager = manager;
            RootNode = manager.RootNode;

            NewModuleNameInput = string.Empty;
        }
        finally
        {
            _isUpdatingFields = false;
        }

        // ИСПРАВЛЕНО: Переключаем фокус на новый модуль на следующем такте UI
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            SelectedModule = newModule;
        }), System.Windows.Threading.DispatcherPriority.Background);

        // ИСПРАВЛЕНО: Вызываем АВТОНОМНЫЙ метод Save у нашего объекта конфигурации, 
        // не трогая коллекцию 'Projects' в MainViewModel и защищая ListBox от сброса!
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string targetDir = Path.Combine(appData, "ContextSlicer", "projects");
        SelectedProject.Save(targetDir);
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

    // ================================================================= -->
    // ИСПРАВЛЕНО: СТРОГОЕ РАЗДЕЛЕНИЕ ЦЕЛИКОВЫХ ФАЙЛОВ И ТОЧЕЧНЫХ ФУНКЦИЙ -->
    // ================================================================= -->
    public void SyncTreeWithModule()
    {
        if (SelectedModule == null || RootNode == null) return;

        SelectedModule.CheckedFiles.Clear();
        SelectedModule.CheckedEntries.Clear();

        bool isTextProject = SelectedProject?.Type == ProjectType.GoogleDoc || SelectedProject?.Type == ProjectType.WordDoc;

        // 1. СБОР ДАННЫХ ДЛЯ ЛИТЕРАТУРНЫХ ПРОЕКТОВ (GOOGLE DOCS)
        if (isTextProject)
        {
            var flatSyntaxList = new List<FileSystemNode>();
            CollectAllCheckedSyntaxNodes(RootNode, flatSyntaxList);

            foreach (var node in flatSyntaxList)
            {
                if (node == null) continue;
                SelectedModule.CheckedEntries.Add(new SyntaxEntry
                {
                    FilePath = string.Empty,
                    EntryPath = node.EntryPath,
                    DisplayName = node.Name,
                    Type = node.SyntaxType,
                    SpanInfo = node.SyntaxSpanInfo
                });
            }
            return;
        }

        // =================================================================
        // 2. СБОР ДАННЫХ ДЛЯ ТЕХНИЧЕСКИХ ПРОЕКТОВ (КОД C# / SQL)
        // Иерархически обходим дерево, четко разделяя файлы целиком и функции
        // =================================================================
        var allCheckedNodes = new List<FileSystemNode>();
        CollectAllCheckedNodesFlat(RootNode, allCheckedNodes);

        foreach (var node in allCheckedNodes)
        {
            if (node == null) continue;

            // Сценарий А: Это физический файл кода (.cs, .sql)
            if (node.IsFile && !node.IsSyntaxNode && !string.IsNullOrEmpty(node.RelativePath))
            {
                // Проверяем: если у файла нет детей, ИЛИ у него висит заглушка загрузки, 
                // ИЛИ у него стоит жесткая галочка True (выбран целиком) - сохраняем его в CheckedFiles!
                bool hasNoLoadedChildren = node.Children.Count == 0 || (node.Children.Count == 1 && node.Children[0].Name == "LoadingStub...");

                if (node.IsChecked == true || hasNoLoadedChildren)
                {
                    if (!SelectedModule.CheckedFiles.Contains(node.RelativePath))
                    {
                        SelectedModule.CheckedFiles.Add(node.RelativePath);
                    }
                }
            }
            // Сценарий Б: Это синтаксический элемент (метод, класс, импорты) внутри файла
            else if (node.IsSyntaxNode && (node.IsChecked == true || node.IsChecked == null))
            {
                // Нам нужно убедиться, что его родительский файл НЕ выбран целиком.
                // Если родительский файл выбран целиком, сохранять отдельные функции нет смысла - файл и так улетит в контекст.
                var parentFile = FindParentFileNode(node);
                if (parentFile != null && parentFile.IsChecked != true)
                {
                    SelectedModule.CheckedEntries.Add(new SyntaxEntry
                    {
                        FilePath = node.RelativePath,
                        EntryPath = node.EntryPath,
                        DisplayName = node.Name,
                        Type = node.SyntaxType,
                        SpanInfo = node.SyntaxSpanInfo
                    });
                }
            }
        }
    }

    /// <summary>
    /// Вспомогательный плоский сборщик вообще всех чекнутых узлов дерева UI
    /// </summary>
    private void CollectAllCheckedNodesFlat(FileSystemNode node, List<FileSystemNode> result)
    {
        if (node == null || result == null) return;

        if (node.IsChecked == true || node.IsChecked == null)
        {
            result.Add(node);
        }

        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
        {
            CollectAllCheckedNodesFlat(child, result);
        }
    }

    /// <summary>
    /// Вспомогательный метод поиска родительского узла-файла для синтаксической ноды
    /// </summary>
    private FileSystemNode? FindParentFileNode(FileSystemNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current.IsFile && !current.IsSyntaxNode) return current;
            current = current.Parent;
        }
        return null;
    }


    // ================================================================= -->
    // ИСПРАВЛЕНО: ИЗОЛИРОВАННОЕ СОХРАНЕНИЕ ТЕКУЩЕГО ПРОЕКТА В СВОЙ JSON -->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО: АТОМАРНОЕ СОХРАНЕНИЕ БЕЗ СБРОСА ВЫДЕЛЕНИЯ И ФОКУСА UI -->
    // ================================================================= -->
    private void ExecuteSave(bool showMessage)
    {
        // ================================================================= -->
        // ИСПРАВЛЕНО: АТОМАРНОЕ СОХРАНЕНИЕ СТРОГО ЧЕРЕЗ SYNCTREEWITHMODULE  -->
        // ================================================================= -->
        // 1. Фиксируем ссылку на текущий выбранный проект
        var currentProject = SelectedProject;
        if (currentProject == null)
        {
            if (!string.IsNullOrWhiteSpace(ProjectNameInput))
            {
                var autoProject = new ProjectConfig { ProjectName = ProjectNameInput.Trim() };
                Projects.Add(autoProject);
                SelectedProject = autoProject;
                currentProject = autoProject;
            }
            else
            {
                if (showMessage)
                {
                    string warnMsg = Application.Current.Resources["Str_Msg_EnterProjectName"] as string ?? "Введите имя проекта перед сохранением.";
                    string warnTitle = Application.Current.Resources["Str_Title_Warning"] as string ?? "Внимание";
                    System.Windows.MessageBox.Show(warnMsg, warnTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }
        }

        // 2. ИСПРАВЛЕНО: Вызываем централизованный метод синхронизации дерева.
        // Весь старый дублирующий код ручного пересбора коллекций с GetCheckedFilesExtended 
        // и затиранием массивов CheckedFiles/CheckedEntries ПОЛНОСТЬЮ УДАЛЕН.
        // Теперь данные сохраняются строго в том виде, в каком их подготовил SyncTreeWithModule.
        SyncTreeWithModule();

        // 3. Обновляем метаданные СТРОГО внутри локального объекта currentProject
        currentProject.ProjectName = ProjectNameInput.Trim();
        currentProject.RootPath = RootPath;
        currentProject.OutputPath = OutputPath;
        currentProject.PromptRules = PromptRules;
        currentProject.IncludeDirectoryStructure = IncludeDirectoryStructure;
        currentProject.IncludeImages = IncludeImages;
        currentProject.Modules = Modules.ToList();

        try
        {
            // Физическая запись на диск
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string targetDir = Path.Combine(appData, "ContextSlicer", "projects");
            currentProject.Save(targetDir);

            if (showMessage)
            {
                string successMsg = Application.Current.Resources["Str_Status_ProjectSaved"] as string ?? "Настройки проекта успешно сохранены.";
                string successTitle = Application.Current.Resources["Str_Msg_EmailCopiedTitle"] as string ?? "Успех";
                System.Windows.MessageBox.Show(successMsg, successTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Save Error] {ex.Message}");
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
    // ================================================================= -->
    // ИСПРАВЛЕНО: ТОТАЛЬНОЕ УДАЛЕНИЕ ПРОЕКТА И ЕГО ЛОКАЛЬНОГО КЭША GDOC  -->
    // ================================================================= -->
    [RelayCommand]
    private void DeleteProject(ProjectConfig? project)
    {
        var targetProject = project ?? SelectedProject;
        if (targetProject == null) return;

        // Считываем мультиязычные строки подтверждения
        string confirmTemplate = Application.Current.Resources["Str_Msg_ConfirmDeleteProj"] as string
            ?? "Вы уверены, что хотите полностью удалить проект \"{0}\" и все его модули?";
        string confirmMsg = string.Format(confirmTemplate, targetProject.ProjectName);
        string title = Application.Current.Resources["Str_Title_Confirmation"] as string ?? "Подтверждение";

        var result = System.Windows.MessageBox.Show(confirmMsg, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string targetDir = Path.Combine(appData, "ContextSlicer", "projects");

                // 1. ОЧИСТКА ГЛОБАЛЬНОГО КЭША GOOGLE DOCS (ЕСЛИ ЭТО ЛИТЕРАТУРНЫЙ ПРОЕКТ)
                if (targetProject.Type == ProjectType.GoogleDoc && !string.IsNullOrWhiteSpace(targetProject.RootPath))
                {
                    // Вытаскиваем уникальный ID документа из ссылки RootPath
                    string googleDocId = GoogleDownloader.ExtractDocumentId(targetProject.RootPath);
                    if (!string.IsNullOrEmpty(googleDocId))
                    {
                        string googleCachePath = Path.Combine(baseDir, "googlecache", $"{googleDocId}.json");

                        // Безопасно стираем файл кэша, если он физически существует на диске
                        if (File.Exists(googleCachePath))
                        {
                            File.Delete(googleCachePath);
                            System.Diagnostics.Debug.WriteLine($"[Кэш Очищен] Удален файл кэша Google Docs: {googleCachePath}");
                        }
                    }
                }

                // 2. ФИЗИЧЕСКОЕ УДАЛЕНИЕ ФАЙЛА КОНФИГУРАЦИИ ПРОЕКТА
                string safeName = string.Concat(targetProject.ProjectName.Split(Path.GetInvalidFileNameChars())).Trim();
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "UntitledProject";
                string projectFilePath = Path.Combine(targetDir, $"{safeName}.json");

                if (File.Exists(projectFilePath))
                {
                    File.Delete(projectFilePath);
                    System.Diagnostics.Debug.WriteLine($"[Успех] Файл проекта успешно удален: {projectFilePath}");
                }
                else
                {
                    // Фолбэк на случай расхождения имен файлов при переименовании в рантайме
                    var allFiles = Directory.GetFiles(targetDir, "*.json");
                    foreach (var file in allFiles)
                    {
                        var checkProj = ProjectConfig.Load(file);
                        if (checkProj != null && checkProj.ProjectName == targetProject.ProjectName)
                        {
                            File.Delete(file);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Delete Project Critical Error] {ex.Message}");
            }

            // Вычищаем проект из оперативной памяти и коллекции интерфейса ListBox
            if (targetProject == SelectedProject)
            {
                SelectedProject = null;
            }

            Projects.Remove(targetProject);

            // Автоматически переключаем фокус на оставшийся проект
            if (Projects.Count > 0)
            {
                SelectedProject = Projects.FirstOrDefault();
            }
            else
            {
                RootNode = null;
                Modules.Clear();
                OnPropertyChanged(nameof(DisplayProjectName));
            }
        }
    }


    // ================================================================= -->
    // ИСПРАВЛЕНО: ПЕРЕИМЕНОВАНИЕ ФАЙЛА ПРОЕКТА И СТИРАНИЕ СТАРОЙ ВЕРСИИ -->
    // ================================================================= -->
    [RelayCommand]
    private void UpdateCurrentProjectName()
    {
        if (SelectedProject == null) return;
        string newName = ProjectNameInput?.Trim() ?? string.Empty;
        if (newName.Equals(SelectedProject.ProjectName, StringComparison.Ordinal)) return;

        if (!IsValidName(newName, out string validationError))
        {
            IsProjectNameInvalid = true;
            System.Windows.MessageBox.Show(validationError, "Ошибка валидации проекта", MessageBoxButton.OK, MessageBoxImage.Warning);
            ProjectNameInput = SelectedProject.ProjectName;
            return;
        }

        IsProjectNameInvalid = false;

        try
        {
            // Вычисляем путь к старому файлу, чтобы стереть его перед записью нового
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string targetDir = Path.Combine(appData, "ContextSlicer", "projects");

            string oldSafeName = string.Concat(SelectedProject.ProjectName.Split(Path.GetInvalidFileNameChars())).Trim();
            string oldProjectFilePath = Path.Combine(targetDir, $"{oldSafeName}.json");

            // Удаляем старый файл-призрак с диска, так как имя проекта меняется
            if (File.Exists(oldProjectFilePath))
            {
                File.Delete(oldProjectFilePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Rename File Cleanup Error] {ex.Message}");
        }

        // Присваиваем новое имя и сохраняем по новому свободному пути
        SelectedProject.ProjectName = newName;

        var index = Projects.IndexOf(SelectedProject);
        if (index >= 0)
        {
            Projects[index] = SelectedProject;
            SelectedProject = Projects[index];
        }

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

    // ================================================================= -->
    // ИСПРАВЛЕНО: БЕЗОШИБОЧНЫЙ СБОР СИНТАКСИСА ДЛЯ СОХРАНЕНИЯ В JSON    -->
    // ================================================================= -->
    private void CollectAllCheckedSyntaxNodes(FileSystemNode node, List<FileSystemNode> result)
    {
        if (node == null || result == null) return;

        // Если узел является синтаксическим элементом и он выбран (True или Indeterminate квадратик)
        if (node.IsSyntaxNode && (node.IsChecked == true || node.IsChecked == null))
        {
            // ИСПРАВЛЕНО: Для проектов кода мы сохраняем абсолютно ВСЕ выбранные синтаксические ноды, 
            // независимо от флага IsFile, так как методы и свойства теперь являются листьями дерева!
            result.Add(node);
        }

        // Безопасный рекурсивный обход дочерних элементов дерева
        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
        {
            if (child != null)
            {
                CollectAllCheckedSyntaxNodes(child, result);
            }
        }
    }


    [RelayCommand]
    private void BrowseRootPath()
    {
        var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            RootPath = dialog.FileName;
            // ИСПРАВЛЕНО: Заменяем удаленный RefreshTreeView на монолитный ProjectTreeManager
            if (SelectedProject != null && SelectedModule != null)
            {
                var manager = new ProjectTreeManager(SelectedProject, SelectedModule);
                FileSystemNode.ActiveManager = manager;
                RootNode = manager.RootNode;
            }

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
    // ================================================================= -->
    // ИСПРАВЛЕНО: ДИНАМИЧЕСКАЯ ВЫЧИТКА ИЗОЛИРОВАННЫХ ФАЙЛОВ ПРОЕКТОВ   -->
    // ================================================================= -->
    private void LoadProjects()
    {
        // Запускаем одноразовый мигратор старой базы projects.json
        ProjectMigrationService.MigrateOldProjectsIfNeeded();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string targetDir = Path.Combine(appData, "ContextSlicer", "projects");

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            Projects = new ObservableCollection<ProjectConfig>();
            return;
        }

        var loadedProjects = new List<ProjectConfig>();

        try
        {
            var projectFiles = Directory.GetFiles(targetDir, "*.json");
            foreach (var file in projectFiles)
            {
                // Используем инкапсулированный статический метод загрузки
                var project = ProjectConfig.Load(file);
                if (project != null)
                {
                    loadedProjects.Add(project);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Directory Access Error] {ex.Message}");
        }

        var sortedList = loadedProjects.OrderBy(p => p.ProjectName).ToList();
        Projects = new ObservableCollection<ProjectConfig>(sortedList);

        if (Projects.Count > 0)
        {
            SelectedProject = Projects[0];
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

                // ================================================================= -->
                // ИСПРАВЛЕНО: СИНХРОНИЗАЦИЯ АРГУМЕНТОВ С РОДНЫМИ СВОЙСТВАМИ VIEWMODEL -->
                // ================================================================= -->
                // Вызываем расширенную версию метода, соблюдая строгую последовательность параметров из ContextBuilderService
                await ContextBuilderService.GeneratePdfContextFileAsync(
                    OutputPath,                         // 1. Папка вывода
                    safeFileName,                       // 2. Имя файла
                    PromptRules,                        // 3. Общие правила проекта
                    ModuleRules,                        // 4. Локальные правила модуля
                    IncludeDirectoryStructure,          // 5. Флаг структуры оглавления
                    IncludeImages,                      // 6. Флаг включения картинок с главной панели
                    RootNode,                           // 7. Корневой узел дерева UI
                    SelectedModule.CheckedEntries,      // 8. ИСПРАВЛЕНО CS0103: Твое родное свойство чекнутых синтаксических узлов!
                    progressHandler,                    // 9. ИСПРАВЛЕНО CS0103: Твоя родная локальная переменная прогресс-бара!
                    SelectedProject?.Type ?? ProjectType.Folder, // 10. Тип проекта (Книга/Код)
                    _cts.Token);                        // 11. Токен отмены операции


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
        targetProject.IncludeImages = EditProjectIncludeImagesInput;
        IncludeImages = EditProjectIncludeImagesInput;

        // ИСПРАВЛЕНО CS0103: Инициализируем структуру через ProjectTreeManager
        var currentModule = targetProject.Modules.FirstOrDefault();
        if (currentModule != null)
        {
            var manager = new ProjectTreeManager(targetProject, currentModule);
            FileSystemNode.ActiveManager = manager;
            RootNode = manager.RootNode;
        }

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
        EditProjectIncludeImagesInput = SelectedProject.IncludeImages;

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
            // ИСПРАВЛЕНО CS0103: Пересобираем дерево по новому пути через менеджер
            if (SelectedProject != null && SelectedModule != null)
            {
                var manager = new ProjectTreeManager(SelectedProject, SelectedModule);
                FileSystemNode.ActiveManager = manager;
                RootNode = manager.RootNode;
            }

        }
        catch (Exception ex)
        {
            string generalErrorTitle = Application.Current.Resources["Str_Err_GeneralErrorTitle"] as string ?? "Ошибка";
            MessageBox.Show($"{ex.Message}", generalErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }




}


