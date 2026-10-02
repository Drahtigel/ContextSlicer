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
public class ProjectTreeManager
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
        // ИСПРАВЛЕНО: ЖЕСТКАЯ НОРМАЛИЗАЦИЯ СЛЭШЕЙ ДЛЯ БЕЗОШИБОЧНОГО TRI-STATE-->
        // ================================================================= -->
        // ШАГ 3: НАКАТЫВАЕМ ТОЧЕЧНЫЕ СИНТАКСИЧЕСКИЕ ВЫБОРКИ (CheckedEntries)
        // ================================================================= -->
        // ИСПРАВЛЕНО: КАСКАДНЫЙ НАКАТ ПОЛНЫХ ПАПОК И ФАЙЛОВ ИЗ CHECKEDFILES -->
        // ================================================================= -->
        // ШАГ 2: НАКАТЫВАЕМ ФАЙЛЫ И ПАПКИ, ВЫБРАННЫЕ ЦЕЛИКОМ (CheckedFiles)
        if (_module.CheckedFiles != null)
        {
            foreach (var relPath in _module.CheckedFiles)
            {
                if (string.IsNullOrEmpty(relPath)) continue;

                // Нормализуем слэши, удаляя возможные двойные разделители сериализации
                string normPath = relPath.Replace("/", "\\").Replace("\\\\", "\\");

                if (diskNodesMap.TryGetValue(normPath, out var node))
                {
                    // ИСПРАВЛЕНО: Если узел является папкой, мы вызываем SetChecked с флагом updateChildren: true!
                    // Это заставит каскад WPF честно пройтись вниз по дисковой структуре папки
                    // и проставить всем вложенным .cs файлам статус true, спасая их от зануления!
                    if (!node.IsFile)
                    {
                        node.SetChecked(true, updateChildren: true, updateParent: false);
                    }
                    else
                    {
                        node.SetChecked(true, updateChildren: false, updateParent: false);
                    }
                }
            }
        }

        // ================================================================= -->


        // ШАГ 4: Запускаем один сквозной Tri-State пересчет снизу вверх
        DeepVerifyCheckStates(localRoot);

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
        DeepVerifyCheckStates(RootNode);
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: КЛАССИЧЕСКИЙ ФАЙЛОВЫЙ TRI-STATE БЕЗ АНАЛИЗА СИНТАКСИСА-->
    // ================================================================= -->
    private void DeepVerifyCheckStates(FileSystemNode node)
    {
        if (node == null) return;

        // 1. Сначала рекурсивно спускаемся к самым глубоким папкам и файлам диска
        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
        {
            DeepVerifyCheckStates(child);
        }

        // 2. ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Рассчитываем состояние галочки ТОЛЬКО для папок диска!
        // Мы полностью игнорируем синтаксические внутренности файлов (IsSyntaxNode) и технические заглушки.
        // Папка собирает голые состояния IsChecked от своих непосредственных детей-файлов и подпапок.
        if (!node.IsFile && !node.IsSyntaxNode && node.Children.Count > 0)
        {
            bool hasChecked = false;
            bool hasUnchecked = false;
            bool hasIndeterminate = false;

            foreach (var child in node.Children)
            {
                // Пропускаем синтаксические ноды, если они случайно попали на этот уровень, и заглушки
                if (child.IsSyntaxNode || child.Name == "LoadingStub...") continue;

                if (child.IsChecked == true) hasChecked = true;
                else if (child.IsChecked == false) hasUnchecked = true;
                else hasIndeterminate = true; // Сюда попадает файл, у которого статус null (выбран частично)
            }

            bool? newState;

            // Математика Tri-State Проводника Windows:
            if (hasIndeterminate)
            {
                newState = null; // Если хоть один ребенок в квадратике — папка строго в квадратик!
            }
            else if (hasChecked && hasUnchecked)
            {
                newState = null; // Если есть и выбранные, и невыбранные файлы — папка в квадратик!
            }
            else if (hasChecked)
            {
                newState = true; // Если абсолютно ВСЕ файлы внутри папки выбраны — папка горит галочкой!
            }
            else
            {
                newState = false; // Если всё пусто — папка пустая
            }

            if (node.IsChecked != newState)
            {
                node.SetChecked(newState, updateChildren: false, updateParent: false);
            }
        }
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
}
