// ================================================================= -->
// ЧАСТЬ 1: СКАНИРОВАНИЕ ДИСКА И НАКАТ МАССИВА CHECKEDFILES          -->
// ================================================================= -->
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem;

/// <summary>
/// Автономная точка правды для технических проектов. Управляет структурой файлов диска и синтаксисом Roslyn.
/// </summary>
public class ProjectTreeManager:IProjectTreeManager
{
    private readonly ProjectConfig _project;
    private readonly ContextModule _module;
    private readonly string _rootPath;

    public FileSystemNode RootNode { get; private set; } = null!;

    public ProjectTreeManager(ProjectConfig project, ContextModule module)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _module = module ?? throw new ArgumentNullException(nameof(module));
        _rootPath = project.RootPath;

        InitializeCodeTree();
    }

    /// <summary>
    /// Монолитный конвейер кода: строит дерево диска, накатывает кэш JSON и рассчитывает Tri-State
    /// </summary>
    private void InitializeCodeTree()
    {
        if (!Directory.Exists(_rootPath)) return;

        // ШАГ 1: Строим полное, честное дерево репозитория на основе сканирования физического диска
        var localRoot = BuildBaseDiskTree(_rootPath, string.Empty);
        if (localRoot == null) return;

        localRoot.Name = _project.ProjectName;

        // Картируем все построенные дисковые узлы (файлы и папки) по их относительным путям для O(1) поиска
        var diskNodesMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
        BuildDiskNodesMapRecursive(localRoot, diskNodesMap);

        // ШАГ 2: НАКАТЫВАЕМ ФАЙЛЫ, ВЫБРАННЫЕ ЦЕЛИКОМ (CheckedFiles)
        if (_module.CheckedFiles != null)
        {
            foreach (var relPath in _module.CheckedFiles)
            {
                if (string.IsNullOrEmpty(relPath)) continue;
                string normPath = relPath.Replace('/', '\\');

                if (diskNodesMap.TryGetValue(normPath, out var fileNode))
                {
                    fileNode.SetChecked(true, updateChildren: false, updateParent: false);
                }
            }
        }

        // ШАГ 3: НАКАТЫВАЕМ ТОЧЕЧНЫЕ СИНТАКСИЧЕСКИЕ ВЫБОРКИ (CheckedEntries)
        // ================================================================= -->
        // ИСПРАВЛЕНО: УПРЕЖДАЮЩИЙ НАКАТ NULL-STATE ДЛЯ РОДИТЕЛЬСКИХ ПАПОК ДИСКА -->
        // ================================================================= -->
        // ШАГ 3: НАКАТЫВАЕМ ТОЧЕЧНЫЕ СИНТАКСИЧЕСКИЕ ВЫБОРКИ (CheckedEntries)
        if (_module.CheckedEntries != null)
        {
            var entriesByFile = _module.CheckedEntries
                .Where(e => e != null && !string.IsNullOrEmpty(e.FilePath))
                .ToLookup(e => e.FilePath.Replace("/", "\\").Replace("\\\\", "\\"), StringComparer.OrdinalIgnoreCase);

            foreach (var fileGroup in entriesByFile)
            {
                string fileRelPath = fileGroup.Key;

                if (diskNodesMap.TryGetValue(fileRelPath, out var fileNode))
                {
                    // 1. Ставим самому файлу статус тристейта (null), так как внутри него выбраны только методы
                    fileNode.SetChecked(null, updateChildren: false, updateParent: false);

                    // 2. ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Поднимаемся по цепочке вверх от файла к корню
                    // и принудительно выставляем абсолютно всем родительским папкам статус null!
                    // Это заставит папки Filesystem и Google гореть закрашенными квадратиками прямо при старте!
                    var currentParent = fileNode.Parent;
                    while (currentParent != null)
                    {
                        // Если папка не выбрана целиком (не true) — гарантированно переводим её в тристейт null
                        if (currentParent.IsChecked != true)
                        {
                            currentParent.SetChecked(null, updateChildren: false, updateParent: false);
                        }
                        currentParent = currentParent.Parent;
                    }

                    // Намертво вычищаем техническую заглушку ленивой загрузки для предзаданных из JSON нод
                    for (int i = fileNode.Children.Count - 1; i >= 0; i--)
                    {
                        if (fileNode.Children[i].Name == "LoadingStub...") fileNode.Children.RemoveAt(i);
                    }

                    // Накатываем структуру выбранных классов/методов из JSON внутрь файла
                    foreach (var entry in fileGroup)
                    {
                        if (entry == null) continue;
                        InjectSyntaxNodeFromConfig(fileNode, entry);
                    }
                }
            }
        }
        // ================================================================= -->

        // ШАГ 4: Запускаем один сквозной Tri-State пересчет снизу вверх
        localRoot.NotifyComputedStateChanged();

        RootNode = localRoot;
    }

    /// <summary>
    /// Сканирует диск и строит базовое плоское дерево репозитория со статусом False
    /// </summary>
    private FileSystemNode? BuildBaseDiskTree(string currentPath, string relPath)
    {
        var node = new FileSystemNode
        {
            Name = string.IsNullOrEmpty(relPath) ? "Root" : Path.GetFileName(currentPath),
            FullPath = currentPath,
            RelativePath = relPath,
            IsFile = false,
            IsChecked = false
        };
        node.SetChecked(false, updateChildren: false, updateParent: false);

        try
        {
            var dirInfo = new DirectoryInfo(currentPath);

            foreach (var subDir in dirInfo.GetDirectories())
            {
                if (IsFolderExcluded(subDir.Name)) continue;
                string nextRel = string.IsNullOrEmpty(relPath) ? subDir.Name : Path.Combine(relPath, subDir.Name);

                var childDir = BuildBaseDiskTree(subDir.FullName, nextRel);
                if (childDir != null)
                {
                    childDir.Parent = node;
                    node.Children.Add(childDir);
                }
            }

            foreach (var file in dirInfo.GetFiles())
            {
                if (IsExtensionExcluded(file.FullName)) continue;
                string nextRel = string.IsNullOrEmpty(relPath) ? file.Name : Path.Combine(relPath, file.Name);

                var childFile = new FileSystemNode
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    RelativePath = nextRel,
                    IsFile = true,
                    Parent = node,
                    IsChecked = false
                };
                childFile.SetChecked(false, updateChildren: false, updateParent: false);

                if (SyntaxParserFactory.IsSupported(file.Extension))
                {
                    childFile.Children.Add(new FileSystemNode { Name = "LoadingStub...", Parent = childFile, IsFile = true });
                }

                node.Children.Add(childFile);
            }
        }
        catch (UnauthorizedAccessException) { return null; }

        return node;
    }
    // ================================================================= -->
    // ЧАСТЬ 2: ИНЖЕКЦИЯ СИНТАКСИСА, ROSLYN-МЕРДЖЕР И TRI-STATE ПЕРЕСЧЕТ -->
    // ================================================================= -->
    private void InjectSyntaxNodeFromConfig(FileSystemNode fileNode, SyntaxEntry entry)
    {
        var fileNodesMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
        BuildNodesMapRecursive(fileNode, fileNodesMap);

        if (fileNodesMap.ContainsKey(entry.EntryPath)) return;

        bool isContainer = entry.Type == EntryType.Namespace || entry.Type == EntryType.Class || entry.Type == EntryType.Struct || entry.Type == EntryType.Interface;

        var newNode = new FileSystemNode
        {
            Name = entry.DisplayName.Trim(),
            RelativePath = fileNode.RelativePath,
            FullPath = fileNode.FullPath,
            IsFile = !isContainer,
            IsSyntaxNode = true,
            SyntaxType = entry.Type,
            SyntaxSpanInfo = entry.SpanInfo,
            EntryPath = entry.EntryPath,
            Parent = fileNode,
            IsExpanded = isContainer,
            IsChecked = !isContainer ? true : null
        };
        newNode.SetChecked(!isContainer ? true : null, updateChildren: false, updateParent: false);

        string parentEntryPath = string.Empty;
        int lastDot = entry.EntryPath.LastIndexOf('.');
        if (lastDot > 0) parentEntryPath = entry.EntryPath.Substring(0, lastDot);

        if (!string.IsNullOrEmpty(parentEntryPath) && fileNodesMap.TryGetValue(parentEntryPath, out var parentClassNode))
        {
            newNode.Parent = parentClassNode;
            parentClassNode.Children.Add(newNode);
        }
        else
        {
            fileNode.Children.Add(newNode);
        }
    }

    /// <summary>
    /// Ленивая догрузка невыбранных методов Roslyn при клике на стрелочку файла исходного кода
    /// </summary>
    public async Task PopulateSyntaxNodesAsync(FileSystemNode fileNode)
    {
        if (fileNode == null || !fileNode.IsFile || fileNode.IsSyntaxNode) return;

        string ext = Path.GetExtension(fileNode.FullPath);
        if (!SyntaxParserFactory.IsSupported(ext)) return;

        var parser = SyntaxParserFactory.GetParser(ext);
        if (parser == null) return;

        List<SyntaxEntry> entries = await parser.ParseFileAsync(fileNode.FullPath, fileNode.RelativePath);

        var existingNodesMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
        BuildNodesMapRecursive(fileNode, existingNodesMap);

        var savedCombinedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_module.CheckedEntries != null)
        {
            foreach (var entry in _module.CheckedEntries)
            {
                if (entry != null && !string.IsNullOrEmpty(entry.FilePath) && !string.IsNullOrEmpty(entry.EntryPath))
                {
                    string normFile = entry.FilePath.Replace('/', '\\').ToLower();
                    savedCombinedKeys.Add($"{normFile}||{entry.EntryPath}");
                }
            }
        }

        bool? originalFileCheckState = fileNode.IsChecked;

        foreach (var entry in entries)
        {
            if (entry == null || existingNodesMap.ContainsKey(entry.EntryPath)) continue;

            bool isContainer = entry.Type == EntryType.Namespace || entry.Type == EntryType.Class || entry.Type == EntryType.Struct || entry.Type == EntryType.Interface;
            string currentFileNorm = fileNode.RelativePath.Replace('/', '\\').ToLower();
            string currentCombinedKey = $"{currentFileNorm}||{entry.EntryPath}";

            bool isNodeSavedInConfig = savedCombinedKeys.Contains(currentCombinedKey);
            bool? nodeTargetState = (originalFileCheckState == true) || isNodeSavedInConfig;

            var newNode = new FileSystemNode
            {
                Name = entry.DisplayName.Trim(),
                RelativePath = fileNode.RelativePath,
                FullPath = fileNode.FullPath,
                IsFile = !isContainer,
                IsSyntaxNode = true,
                SyntaxType = entry.Type,
                SyntaxSpanInfo = entry.SpanInfo,
                EntryPath = entry.EntryPath,
                Parent = fileNode,
                IsExpanded = isContainer,
                IsChecked = nodeTargetState
            };
            newNode.SetChecked(nodeTargetState, updateChildren: false, updateParent: false);

            string parentEntryPath = string.Empty;
            int lastDot = entry.EntryPath.LastIndexOf('.');
            if (lastDot > 0) parentEntryPath = entry.EntryPath.Substring(0, lastDot);

            if (!string.IsNullOrEmpty(parentEntryPath) && existingNodesMap.TryGetValue(parentEntryPath, out var parentClassNode))
            {
                newNode.Parent = parentClassNode;
                parentClassNode.Children.Add(newNode);
            }
            else
            {
                newNode.Parent = fileNode;
                fileNode.Children.Add(newNode);
            }

            existingNodesMap[entry.EntryPath] = newNode;
        }

        fileNode.SetChecked(originalFileCheckState, updateChildren: false, updateParent: false);
        fileNode.NotifyComputedStateChanged();
    }

    
    // ================================================================= -->
    // ИСПРАВЛЕНО: СТРОГАЯ ОДНОРОДНОСТЬ СЛЭШЕЙ В ДИСКОВОЙ КАРТЕ MAP       -->
    // ================================================================= -->
    private void BuildDiskNodesMapRecursive(FileSystemNode node, Dictionary<string, FileSystemNode> map)
    {
        if (node == null || node.IsSyntaxNode) return;

        if (!string.IsNullOrEmpty(node.RelativePath))
        {
            // ИСПРАВЛЕНО: Принудительно приводим дисковые относительные пути к единому стандарту
            string canonicalPath = node.RelativePath.Replace("/", "\\").Replace("\\\\", "\\");
            map[canonicalPath] = node;
        }

        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
        {
            BuildDiskNodesMapRecursive(child, map);
        }
    }


    private void BuildNodesMapRecursive(FileSystemNode node, Dictionary<string, FileSystemNode> map)
    {
        if (node == null) return;
        if (!string.IsNullOrEmpty(node.EntryPath)) map[node.EntryPath] = node;
        else if (!string.IsNullOrEmpty(node.RelativePath)) map[node.RelativePath] = node;
        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy) BuildNodesMapRecursive(child, map);
    }

    private static bool IsFolderExcluded(string folderName) =>
        GlobalFilterService.Current.ExcludedFolders.Any(f => folderName.Equals(f, StringComparison.OrdinalIgnoreCase));

    private static bool IsExtensionExcluded(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && GlobalFilterService.Current.ExcludedExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================= -->
    // РЕАЛИЗАЦИЯ ИНТЕРФЕЙСА: СБОР ОТМЕЧЕННОГО КОДА ПО COMPUTEDSTATE     -->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО: ПОТОКОБЕЗОПАСНЫЙ СБОР КОДА НАПРЯМУЮ ИЗ КОНФИГУРАЦИИ  -->
    // ================================================================= -->
    public List<SyntaxEntry> GetSelectedEntries()
    {
        // Калькулятор токенов получит кристально чистый, независимый от UI потоков список
        return _module.CheckedEntries != null
            ? _module.CheckedEntries.Where(e => e != null).ToList()
            : new List<SyntaxEntry>();
    }

    /// <summary>
    /// Дополнительный потокобезопасный сборщик файлов, выбранных целиком
    /// </summary>
    public List<string> GetCheckedFiles()
    {
        return _module.CheckedFiles != null
            ? _module.CheckedFiles.Where(f => !string.IsNullOrEmpty(f)).ToList()
            : new List<string>();
    }


    private void CollectCodeEntriesRecursive(FileSystemNode node, List<SyntaxEntry> result)
    {
        if (node == null) return;

        // СЦЕНАРИЙ А: Пользователь выбрал физический файл целиком
        if (node.IsFile && !node.IsSyntaxNode && node.ComputedState == true)
        {
            result.Add(new SyntaxEntry
            {
                FilePath = node.RelativePath,
                EntryPath = string.Empty, // Маркер полной сборки файла
                DisplayName = node.Name,
                Type = EntryType.Section // Фолбэк-тип для файла целиком
            });
            return;
        }

        // СЦЕНАРИЙ Б: Пользователь выбрал конкретную точечную функцию внутри файла
        if (node.IsSyntaxNode && node.ComputedState == true)
        {
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
            CollectCodeEntriesRecursive(child, result);
        }
    }
    // ================================================================= -->
    // РЕАЛИЗАЦИЯ ИНТЕРФЕЙСА: АВТОНОМНЫЙ ПОДСЧЕТ СИМВОЛОВ ИСХОДНОГО КОДА-->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО: СТЕРИЛЬНЫЙ ПАССИВНЫЙ ПОДСЧЕТ КОДА БЕЗ ТРИГГЕРОВ WPF   -->
    // ================================================================= -->
    public async Task<long> CalculateSelectedCharactersAsync(bool includeDirectoryStructure, CancellationToken token)
    {
        // Берем списки выбранного контекста напрямую из стерильной конфигурации модуля,
        // полностью исключая обращение к UI-коллекциям и вызов бесконечных рекурсий!
        var savedFiles = _module.CheckedFiles != null ? _module.CheckedFiles.ToList() : new List<string>();
        var savedEntries = _module.CheckedEntries != null ? _module.CheckedEntries.ToList() : new List<SyntaxEntry>();

        if (savedFiles.Count == 0 && savedEntries.Count == 0)
        {
            return 0;
        }

        // Воссоздаем плоский список нод для оригинального сервиса верстки
        var checkedFilesList = new List<FileSystemNode>();
        foreach (var relPath in savedFiles)
        {
            checkedFilesList.Add(new FileSystemNode
            {
                RelativePath = relPath,
                FullPath = Path.Combine(_rootPath, relPath.Replace('/', '\\')),
                IsFile = true,
                IsChecked = true
            });
        }

        var emptyProgress = new Progress<ProgressReport>();

        // Вызываем оригинальный сервис, передавая ему изолированные списки данных
        var sb = await ContextBuilderService.BuildTextContentAsync(
            string.Empty, string.Empty, includeDirectoryStructure,
            checkedFilesList, savedEntries,
            emptyProgress, ProjectType.Folder, false, token
        );

        return sb?.Length ?? 0;
    }


    private void CollectFilesInternal(FileSystemNode node, List<FileSystemNode> result)
    {
        if (node == null) return;
        if (node.IsFile && !node.IsSyntaxNode && (node.ComputedState == true || node.ComputedState == null))
        {
            if (!result.Contains(node)) result.Add(node);
        }
        foreach (var child in node.Children) CollectFilesInternal(child, result);
    }

}
