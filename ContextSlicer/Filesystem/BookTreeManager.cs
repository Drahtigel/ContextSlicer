// ================================================================= -->
// ИЗОЛИРОВАННЫЙ КОНТРОЛЛЕР СТРУКТУРЫ ЛИТЕРАТУРНЫХ ПРОЕКТОВ GOOGLE  -->
// ================================================================= -->
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem;

public class BookTreeManager :IProjectTreeManager
{
    private readonly ProjectConfig _project;
    private readonly ContextModule _module;
    private readonly string _cacheFilePath;

    public FileSystemNode RootNode { get; private set; } = null!;

    // ================================================================= -->
    // ИСПРАВЛЕНО: АВТОМАТИЧЕСКАЯ СВЕРКА КЭША ПРИ ДЕСЕРИАЛИЗАЦИИ ДЕРЕВА  -->
    // ================================================================= -->
    public BookTreeManager(ProjectConfig project, ContextModule module)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _module = module ?? throw new ArgumentNullException(nameof(module));

        string docId = Google.GoogleDownloader.ExtractDocumentId(project.RootPath);
        string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
        _cacheFilePath = Path.Combine(cacheDir, $"{docId}.json");

        // АВТОМАТИЧЕСКАЯ УМНАЯ СИНХРОНИЗАЦИЯ:
        // Перед тем как построить дерево, мы в синхронном блоке проверяем, 
        // не обновился ли документ в облаке, используя наш новый метод Drive API.
        try
        {
            var downloader = new Google.GoogleDownloader();

            // Извлекаем путь к файлу авторизации проекта
            string mainVMKeyPath = string.Empty;
            var mainVM = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                System.Windows.Application.Current.MainWindow?.DataContext as MainViewModel);

            if (mainVM != null && !string.IsNullOrEmpty(project.GoogleApiKey))
            {
                mainVMKeyPath = mainVM.GetKeyPathByEmail(project.GoogleApiKey);
            }

            if (File.Exists(_cacheFilePath) && File.Exists(mainVMKeyPath))
            {
                // Запрашиваем дату изменения из облака Google Drive
                DateTime? cloudModifiedTime = Task.Run(async () =>
                    await downloader.GetCloudModifiedTimeAsync(project.RootPath, mainVMKeyPath)
                ).GetAwaiter().GetResult();

                if (cloudModifiedTime.HasValue)
                {
                    // Получаем физическую дату изменения локального файла кэша на диске
                    DateTime localCacheTime = File.GetLastWriteTime(_cacheFilePath);

                    // Если документ в облаке новее нашего локального кэша — принудительно обновляем файл!
                    if (cloudModifiedTime.Value > localCacheTime)
                    {
                        System.Diagnostics.Debug.WriteLine("[Auto Cache] Обнаружены изменения в облаке. Скачиваем свежую структуру...");
                        Task.Run(async () =>
                            await downloader.DownloadToCacheAsync(project.RootPath, AppDomain.CurrentDomain.BaseDirectory, mainVMKeyPath)
                        ).GetAwaiter().GetResult();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Auto Cache Sync Error] {ex.Message}");
        }

        InitializeBookTree();
    }


    /// <summary>
    /// Чистый алгоритм слияния литературного дерева по схеме автора
    /// </summary>
    // ================================================================= -->
    // ИСПРАВЛЕНО: СТЕРИЛЬНОЕ ПЕРЕЧИТЫВАНИЕ КЭША ДОКУМЕНТА БЕЗ ЗАСТРЕВАНИЙ-->
    // ================================================================= -->
    private void InitializeBookTree()
    {
        // 1. Создаем абсолютно чистый, изолированный корень книги
        var localRoot = new FileSystemNode
        {
            Name = _project.ProjectName,
            FullPath = _cacheFilePath,
            RelativePath = string.Empty,
            IsFile = false,
            IsChecked = null
        };

        // ================================================================= -->
        // ИСПРАВЛЕНО: РАЗРЫВ ВЗАИМНОЙ БЛОКИРОВКИ ПОТОКОВ ЧЕРЕЗ CONFIGUREAWAIT-->
        // ================================================================= -->
        // 2. ВЫЧИТЫВАЕМ ДОКУМЕНТ: Строим полное дерево из свежего дискового кэша.
        if (File.Exists(_cacheFilePath))
        {
            try
            {
                var parser = SyntaxParserFactory.GetParser(".gdoc");
                if (parser != null)
                {
                    // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Используем Task.Run вместе с ConfigureAwait(false).
                    // Это принудительно уводит асинхронную задачу чтения файла из контекста WPF,
                    // полностью уничтожая дедлок на строке File.ReadAllTextAsync!
                    var allBookEntries = Task.Run(async () =>
                        await parser.ParseFileAsync(_cacheFilePath, _project.RootPath).ConfigureAwait(false)
                    ).GetAwaiter().GetResult();

                    // Строим изолированную локальную карту для сборки текущего шага
                    var currentBuildingMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);

                    foreach (var entry in allBookEntries)
                    {
                        if (entry != null)
                        {
                            InjectBookNodeSecure(localRoot, entry, currentBuildingMap);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Book Engine Error] {ex.Message}");
            }
        }
        // ================================================================= -->


        // Картуем построенное дерево для быстрого сличения O(1)
        var uniqueMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
        BuildNodesMap(localRoot, uniqueMap);

        // Хэш-сет сохраненных в модуле проекта веток
        var savedEntriesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_module.CheckedEntries != null)
        {
            foreach (var e in _module.CheckedEntries)
            {
                if (e != null && !string.IsNullOrEmpty(e.EntryPath)) savedEntriesSet.Add(e.EntryPath);
            }
        }

        // 3. СЛИЧАЕМ И СБРАСЫВАЕМ: Новая Глава 8 не найдена в CheckedEntries — она честно получит статус false [ ]!
        foreach (var nodePair in uniqueMap)
        {
            var node = nodePair.Value;
            if (node == null || !node.IsSyntaxNode) continue;

            bool isLeafNode = node.Children.Count == 0 || (node.Children.Count == 1 && node.Children[0].Name == "LoadingStub...");

            if (isLeafNode)
            {
                bool isSaved = savedEntriesSet.Contains(node.EntryPath);
                node.SetChecked(isSaved ? true : false, updateChildren: false, updateParent: false);
            }
            else
            {
                node.SetChecked(null, updateChildren: false, updateParent: false);
            }
        }

        // 4. Оповещаем WPF о том, что структура полностью обновлена
        localRoot.NotifyComputedStateChanged();

        RootNode = localRoot;
    }

    /// <summary>
    /// ИСПРАВЛЕНО: Безопасный инжектор узлов прозы. Использует внешнюю карту текущей сборки,
    /// полностью исключая ложное блокирование новых глав (таких как Глава 8) при обновлении кэша!
    /// </summary>
    // ================================================================= -->
    // ИСПРАВЛЕНО: СТРОГАЯ ИЗОЛЯЦИЯ РОДИТЕЛЬСКИХ ССЫЛОК ДЛЯ ИСКЛЮЧЕНИЯ ПЕТЛИ-->
    // ================================================================= -->
    private void InjectBookNodeSecure(FileSystemNode localRoot, SyntaxEntry entry, Dictionary<string, FileSystemNode> buildingMap)
    {
        if (buildingMap.ContainsKey(entry.EntryPath)) return;

        bool isContainer = entry.Type == EntryType.Tab || entry.Type == EntryType.Heading;

        var newNode = new FileSystemNode
        {
            Name = entry.DisplayName.Trim(),
            RelativePath = entry.FilePath,
            FullPath = localRoot.FullPath,
            IsFile = !isContainer,
            IsSyntaxNode = true,
            SyntaxType = entry.Type,
            SyntaxSpanInfo = entry.SpanInfo,
            EntryPath = entry.EntryPath,
            IsExpanded = isContainer,
            IsChecked = null,
            // ИСПРАВЛЕНО: Изначально родитель строго null! 
            // Никаких слепых дефолтных привязок к корню localRoot до проверки условий!
            Parent = null
        };
        newNode.SetChecked(null, updateChildren: false, updateParent: false);

        string parentEntryPath = string.Empty;
        int lastSep = entry.EntryPath.LastIndexOf('/');
        if (lastSep > 0) parentEntryPath = entry.EntryPath.Substring(0, lastSep);

        if (!string.IsNullOrEmpty(parentEntryPath) && buildingMap.TryGetValue(parentEntryPath, out var parentNode))
        {
            // Элемент вложенный — его родителем железно становится найденный узел папки/вкладки
            newNode.Parent = parentNode;
            parentNode.Children.Add(newNode);
        }
        else
        {
            // Элемент корневой — его родителем и контейнером становится главный корень проекта
            newNode.Parent = localRoot;
            localRoot.Children.Add(newNode);
        }

        // Регистрируем ноду в карте текущей сборки
        buildingMap[entry.EntryPath] = newNode;
    }

    private void BuildNodesMap(FileSystemNode node, Dictionary<string, FileSystemNode> map)
    {
        if (node == null) return;
        if (!string.IsNullOrEmpty(node.EntryPath)) map[node.EntryPath] = node;
        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy) BuildNodesMap(child, map);
    }
    public List<string> GetCheckedFiles()
    {
        return new List<string>();
    }
    public List<SyntaxEntry> GetSelectedEntries()
    {
        return _module.CheckedEntries != null
            ? _module.CheckedEntries.Where(e => e != null).ToList()
            : new List<SyntaxEntry>();
    }
    private void CollectBookEntriesRecursive(FileSystemNode node, List<SyntaxEntry> result)
    {
        if (node == null) return;

        // Если это элемент структуры книги и чекбокс горит галочкой [✓]
        if (node.IsSyntaxNode && node.ComputedState == true)
        {
            // Собираем только конечные главы (листья), чтобы не дублировать тексты
            bool isLeaf = node.Children.Count == 0 || (node.Children.Count == 1 && node.Children[0].Name == "LoadingStub...");
            if (isLeaf)
            {
                result.Add(new SyntaxEntry
                {
                    FilePath = node.RelativePath,
                    EntryPath = node.EntryPath,
                    DisplayName = node.Name,
                    Type = node.SyntaxType,
                    SpanInfo = node.SyntaxSpanInfo
                });
            }
        }

        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
        {
            CollectBookEntriesRecursive(child, result);
        }
    }
    public async Task<long> CalculateSelectedCharactersAsync(bool includeDirectoryStructure, CancellationToken token)
    {
        // Вытаскиваем сохраненные записи напрямую из конфигурации модуля
        var activeEntries = _module.CheckedEntries != null
            ? _module.CheckedEntries.Where(e => e != null).ToList()
            : new List<SyntaxEntry>();

        if (activeEntries.Count == 0 || string.IsNullOrEmpty(_cacheFilePath) || !File.Exists(_cacheFilePath))
        {
            return 0;
        }

        // Вызываем оригинальный GoogleDocBuilder для сборки прозы напрямую по файлу кэша
        string fullText = await GoogleDocBuilder.BuildContextTextAsync(
            activeEntries, _cacheFilePath, includeDirectoryStructure, false, token
        );

        return fullText?.Length ?? 0;
    }
}
