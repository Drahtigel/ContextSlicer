using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem;

/// <summary>
/// Автономная точка правды для технических проектов. Управляет структурой файлов диска и синтаксисом Roslyn.
/// </summary>
public class ProjectTreeManager : BaseTreeManager
{
    private readonly string _rootPath;

    public ProjectTreeManager(ProjectConfig project, ContextModule module) : base(project, module)
    {
        _rootPath = project.RootPath;
        InitializeCodeTree();
    }

    public override List<string> GetCheckedFiles()
    {
        return Module.CheckedFiles != null
            ? Module.CheckedFiles.Where(f => !string.IsNullOrEmpty(f)).ToList()
            : new List<string>();
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: ИЕРАРХИЧЕСКИЙ СТАРТ КОДА БЕЗ ИСКАЖЕНИЯ ВЛОЖЕННОСТИ    -->
    // ================================================================= -->
    private void InitializeCodeTree()
    {
        if (!Directory.Exists(_rootPath)) return;

        // ШАГ 1: Строим чистое дерево папок и файлов на основе диска
        var localRoot = BuildBaseDiskTree(_rootPath, string.Empty);
        if (localRoot == null) return;

        localRoot.Name = Project.ProjectName;

        var diskNodesMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
        BuildDiskNodesMapRecursive(localRoot, diskNodesMap);

        // ШАГ 2: НАКАТЫВАЕМ ГАЛОЧКИ НА ФАЙЛЫ, ВЫБРАННЫЕ ЦЕЛИКОМ (CheckedFiles)
        if (Module.CheckedFiles != null)
        {
            foreach (var relPath in Module.CheckedFiles)
            {
                if (string.IsNullOrEmpty(relPath)) continue;
                string normPath = relPath.Replace('/', '\\');

                if (diskNodesMap.TryGetValue(normPath, out var fileNode))
                {
                    fileNode.IsChecked = true;
                }
            }
        }

        // ШАГ 3: ИНТЕЛЛЕКТУАЛЬНЫЙ СТАРТ ТОЧЕЧНЫХ ВЫБОРОК ИЗ JSON С УЧЕТОМ РОДИТЕЛЕЙ
        if (Module.CheckedEntries != null && Module.CheckedEntries.Count > 0)
        {
            var entriesByFile = Module.CheckedEntries
                .Where(e => e != null && !string.IsNullOrEmpty(e.FilePath))
                .ToLookup(e => e.FilePath.Replace("/", "\\"), StringComparer.OrdinalIgnoreCase);

            var parser = SyntaxParserFactory.GetParser(".cs");

            foreach (var fileGroup in entriesByFile)
            {
                string fileRelPath = fileGroup.Key;

                if (diskNodesMap.TryGetValue(fileRelPath, out var fileNode) && parser != null)
                {
                    // Удаляем заглушку, так как мы сейчас загрузим честную Roslyn-иерархию при старте
                    for (int i = fileNode.Children.Count - 1; i >= 0; i--)
                    {
                        if (fileNode.Children[i].Name == "LoadingStub...") fileNode.Children.RemoveAt(i);
                    }

                    // Даем файлу статус null (точечный выбор)
                    fileNode.IsChecked = null;

                    // Вызываем парсер напрямую! Он построит ИДЕАЛЬНОЕ дерево пространства имен и классов
                    var entries = Task.Run(async () => await parser.ParseFileAsync(fileNode.FullPath, fileNode.RelativePath)).GetAwaiter().GetResult();
                    var fileNodesMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);

                    // Переиспользуем наш отлаженный двухступенчатый инжектор
                    foreach (var entry in entries)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.EntryPath)) continue;

                        // Метод InjectCodeSyntaxNodeFromParser разложит всё по честным полочкам
                        InjectCodeSyntaxNodeFromParser(fileNode, entry, fileNodesMap);
                    }
                }
            }
        }

        // ШАГ 4: Сквозной накат баланса Tri-State из базового класса (он расставит галочки на листья)
        RestoreCheckedStatesFromConfig(localRoot);
        RootNode = localRoot;
    }
    // ================================================================= -->
    // ДОБАВЛЕНО: ЭТАЛОННЫЙ ИНЖЕКТОР СИНТАКСИСА ИЗ ПАРСЕРА ROSLYN        -->
    // ================================================================= -->
    private void InjectCodeSyntaxNodeFromParser(FileSystemNode fileNode, SyntaxEntry entry, Dictionary<string, FileSystemNode> fileNodesMap)
    {
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
            Parent = null,
            IsExpanded = isContainer,
            IsChecked = false // Статусы проставит метод RestoreCheckedStatesFromConfig на Шаге 4
        };

        string parentEntryPath = string.Empty;
        int lastDot = entry.EntryPath.LastIndexOf('.');
        if (lastDot > 0)
        {
            parentEntryPath = entry.EntryPath.Substring(0, lastDot).Trim();
        }

        // Ступень 1: Ищем точного родителя в карте
        if (!string.IsNullOrEmpty(parentEntryPath) && fileNodesMap.TryGetValue(parentEntryPath, out var parentClassNode))
        {
            newNode.Parent = parentClassNode;
            parentClassNode.Children.Add(newNode);
        }
        // Ступень 2 (Фолбэк): Если это класс, а namespace не найден — крепим к первому namespace файла
        else if (entry.Type == EntryType.Class && fileNode.Children.FirstOrDefault(c => c.SyntaxType == EntryType.Namespace) is FileSystemNode namespaceNode)
        {
            newNode.Parent = namespaceNode;
            namespaceNode.Children.Add(newNode);
        }
        else
        {
            newNode.Parent = fileNode;
            fileNode.Children.Add(newNode);
        }

        fileNodesMap[entry.EntryPath] = newNode;
    }

    // ================================================================= -->
    // ДОБАВЛЕНО: БЕЗОПАСНЫЙ ИЕРАРХИЧЕСКИЙ ИНЖЕКТОР СИНТАКСИСА КОДА      -->
    // ================================================================= -->
    private void InjectCodeSyntaxNodeSecure(FileSystemNode fileNode, SyntaxEntry entry, Dictionary<string, FileSystemNode> fileNodesMap)
    {
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
            Parent = null,
            IsExpanded = isContainer,
            IsChecked = !isContainer ? true : null
        };

        string parentEntryPath = string.Empty;
        int lastSep = entry.EntryPath.LastIndexOf('/'); // ИСПРАВЛЕНО: Используем слэш вместо точки!

        if (lastSep > 0)
        {
            parentEntryPath = entry.EntryPath.Substring(0, lastSep).Trim();
        }

        if (!string.IsNullOrEmpty(parentEntryPath) && fileNodesMap.TryGetValue(parentEntryPath, out var parentClassNode))
        {
            newNode.Parent = parentClassNode;
            parentClassNode.Children.Add(newNode);
        }
        else
        {
            newNode.Parent = fileNode;
            fileNode.Children.Add(newNode);
        }

        fileNodesMap[entry.EntryPath] = newNode;
    }

    /// <summary>
    /// Рекурсивно сканирует жесткий диск и строит базовый каркас папок и файлов
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

    // ================================================================= -->
    // ИСПРАВЛЕНО: ПОЛНАЯ МОНОЛИТНАЯ РЕАЛИЗАЦИЯ ЛЕНИВОЙ ДОГРУЗКИ ROSLYN  -->
    // ================================================================= -->
    /// <summary>
    /// Асинхронная динамическая догрузка Roslyn-методов при клике на стрелочку TreeView.
    /// Полностью очищена от каши, дубликатов путей и ложных падений баланса в null!
    /// </summary>
    public async Task PopulateSyntaxNodesAsync(FileSystemNode fileNode)
    {
        if (fileNode == null || !fileNode.IsFile || fileNode.IsSyntaxNode) return;

        string ext = Path.GetExtension(fileNode.FullPath);
        if (!SyntaxParserFactory.IsSupported(ext)) return;

        var parser = SyntaxParserFactory.GetParser(ext);
        if (parser == null) return;

        // 1. Извлекаем живые синтаксические элементы файла через Roslyn-парсер
        List<SyntaxEntry> entries = await parser.ParseFileAsync(fileNode.FullPath, fileNode.RelativePath);

        // Строим оперативную карту уже существующих нод внутри этого файла
        var existingNodesMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
        BuildNodesMapRecursive(fileNode, existingNodesMap);

        // Извлекаем хэш-сет сохраненных точечных элементов из JSON модуля
        var savedCombinedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Module.CheckedEntries != null)
        {
            foreach (var entry in Module.CheckedEntries)
            {
                if (entry != null && !string.IsNullOrEmpty(entry.FilePath) && !string.IsNullOrEmpty(entry.EntryPath))
                {
                    string normFile = entry.FilePath.Replace('/', '\\').ToLower();
                    savedCombinedKeys.Add($"{normFile}||{entry.EntryPath}");
                }
            }
        }

        bool? originalFileCheckState = fileNode.IsChecked;

        // 2. ЦИКЛ ИЕРАРХИЧЕСКОЙ СБОРКИ И ИНЖЕКЦИИ СИНТАКСИСА ПО ТОЧКАМ
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
                Parent = null, // Будет назначен ниже
                IsExpanded = isContainer,
                IsChecked = nodeTargetState
            };

            // ================================================================= -->
            // ИСПРАВЛЕНО: ДВУХСТУПЕНЧАТЫЙ ПОИСК РОДИТЕЛЬСКОГО КОНТЕЙНЕРА С#    -->
            // ================================================================= -->
            string parentEntryPath = string.Empty;
            int lastDot = entry.EntryPath.LastIndexOf('.');
            if (lastDot > 0)
            {
                parentEntryPath = entry.EntryPath.Substring(0, lastDot).Trim();
            }

            // 1. ПЕРВАЯ СТУПЕНЬ: Пытаемся найти точный родительский контейнер по карте
            if (!string.IsNullOrEmpty(parentEntryPath) && existingNodesMap.TryGetValue(parentEntryPath, out var parentClassNode))
            {
                newNode.Parent = parentClassNode;
                parentClassNode.Children.Add(newNode);
            }
            // 2. ВТОРАЯ СТУПЕНЬ (ФОЛБЭК): Если это класс, и точный namespace не найден в карте,
            // но в файле в принципе есть узел пространства имен — крепим класс внутрь него!
            else if (entry.Type == EntryType.Class && fileNode.Children.FirstOrDefault(c => c.SyntaxType == EntryType.Namespace) is FileSystemNode namespaceNode)
            {
                newNode.Parent = namespaceNode;
                namespaceNode.Children.Add(newNode);
            }
            else
            {
                // Если родительских контейнеров нет (например, это сам корневой namespace)
                newNode.Parent = fileNode;
                fileNode.Children.Add(newNode);
            }

            existingNodesMap[entry.EntryPath] = newNode;
        }


        // 3. ЗАКЛЮЧИТЕЛЬНЫЙ МАТЕМАТИЧЕСКИЙ ПЕРЕСЧЕТ БАЛАНСА РОДИТЕЛЕЙ
        // Вместо слепого затирания fileNode.IsChecked = originalFileCheckState,
        // мы заставляем менеджер рассчитать честное состояние файла на основе живых детей!
        RecalculateParentsBalance(fileNode);

        // Форсируем финальную перерисовку элемента на экране
        fileNode.NotifyComputedStateChanged();
    }

    private void BuildDiskNodesMapRecursive(FileSystemNode node, Dictionary<string, FileSystemNode> map)
    {
        if (node == null || node.IsSyntaxNode) return;

        if (!string.IsNullOrEmpty(node.RelativePath))
        {
            string canonicalPath = node.RelativePath.Replace("/", "\\").Replace("\\\\", "\\");
            map[canonicalPath] = node;
        }

        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy) BuildDiskNodesMapRecursive(child, map);
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

    public override async Task<long> CalculateSelectedCharactersAsync(bool includeDirectoryStructure, CancellationToken token)
    {
        var savedFiles = GetCheckedFiles();
        var savedEntries = GetSelectedEntries();

        if (savedFiles.Count == 0 && savedEntries.Count == 0) return 0;

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
        var sb = await ContextBuilderService.BuildTextContentAsync(
            string.Empty, string.Empty, includeDirectoryStructure,
            checkedFilesList, savedEntries,
            emptyProgress, ProjectType.Folder, false, token
        );

        return sb?.Length ?? 0;
    }
    // ================================================================= -->
    // OVERRIDE: СПУСК КАСКАДА СВЕРХУ ВНИЗ ДЛЯ ТЕХНИЧЕСКИХ ПРОЕКТОВ КОДА -->
    // ================================================================= -->
    /// <summary>
    /// Переопределенный каскад для кода. Бескомпромиссно пробивает true/false 
    /// сверху вниз сквозь папки диска, файлы и лениво загруженные методы Roslyn!
    /// </summary>
    public override void RecalculateChildrenCascade(FileSystemNode parentNode, bool targetState)
    {
        if (parentNode == null || parentNode.Children == null) return;

        foreach (var child in parentNode.Children)
        {
            if (child == null || child.Name == "LoadingStub...") continue;

            // Выставляем жесткое состояние дочернему элементу (файлу, папке или методу)
            child.IsChecked = targetState;
            child.NotifyComputedStateChanged();

            // Если это файл и у него есть живые догруженные методы, 
            // рекурсивно пробиваем команду внутрь синтаксического дерева
            if (child.Children != null && child.Children.Count > 0)
            {
                RecalculateChildrenCascade(child, targetState);
            }
        }
    }
    // ================================================================= -->
    // OVERRIDE: АГРЕГАЦИЯ БАЛАНСА СНИЗУ ВВЕРХ ДЛЯ ПРОЕКТОВ КОДА (C# / SQL) -->
    // ================================================================= -->
    /// <summary>
    /// Переопределенный баланс для кода. Корректно рассчитывает состояние папок 
    /// и файлов, учитывая ленивую догрузку синтаксических методов Roslyn.
    /// </summary>
    public override void RecalculateParentsBalance(FileSystemNode? parentNode)
    {
        if (parentNode == null || parentNode.Children == null || parentNode.Children.Count == 0) return;

        bool hasChecked = false;
        bool hasUnchecked = false;
        bool hasIndeterminate = false;

        // Обходим всех детей текущего узла (это могут быть файлы внутри папки или методы внутри файла)
        for (int i = 0; i < parentNode.Children.Count; i++)
        {
            var child = parentNode.Children[i];

            // Заглушка ленивой загрузки игнорируется, она не ломает математику баланса веса
            if (child == null || child.Name == "LoadingStub...") continue;

            bool? childState = child.IsChecked;
            if (childState == true) hasChecked = true;
            else if (childState == false) hasUnchecked = true;
            else hasIndeterminate = true;
        }

        bool? newParentState;

        // Если мы считаем баланс внутри файла, содержащего живые Roslyn-методы
        if (parentNode.IsFile && !parentNode.IsSyntaxNode && hasChecked && hasUnchecked)
        {
            // ТВОЙ АЛГОРИТМ: Если внутри файла часть методов выбрана, а часть нет — 
            // файл ОБЯЗАН перейти в состояние Indeterminate (null), сигнализируя об этом на UI!
            newParentState = null;
        }
        else
        {
            // Стандартный логический конъюнкт Tri-State для физических папок диска
            if (hasIndeterminate || (hasChecked && hasUnchecked))
                newParentState = null; // Есть каша у детей -> папка горит квадратиком
            else if (hasChecked && !hasUnchecked)
                newParentState = true; // Абсолютно все дети выбраны -> галочка
            else
                newParentState = false; // Абсолютно все дети пустые -> пусто
        }

        // Если состояние изменилось — фиксируем и пробрасываем сигнал для WPF
        if (parentNode.IsChecked != newParentState)
        {
            parentNode.IsChecked = newParentState;
            parentNode.NotifyComputedStateChanged();
        }

        // Рекурсивно шагаем вверх по иерархии папок до корня репозитория
        if (parentNode.Parent != null)
        {
            RecalculateParentsBalance(parentNode.Parent);
        }
    }
    // ================================================================= -->
    // OVERRIDE: МАТЕМАТИЧЕСКИ ТОЧНЫЙ НАКАТ И БАЛАНС СТАРТА ДЛЯ КОДА     -->
    // ================================================================= -->
    /// <summary>
    /// Переопределенный метод накатки состояний для кода. Гарантирует, что после 
    /// загрузки файлов из JSON все папки диска пассивно примут идеальные галочки!
    /// </summary>
    // ================================================================= -->
    // OVERRIDE: МАТЕМАТИЧЕСКИ ТОЧНЫЙ НАКАТ И БАЛАНС СТАРТА ДЛЯ КОДА     -->
    // ================================================================= -->
    /// <summary>
    /// Переопределенный метод накатки состояний для кода. Гарантирует, что после 
    /// загрузки файлов из JSON все папки диска пассивно примут идеальные галочки!
    /// </summary>
    // ================================================================= -->
    // ИСПРАВЛЕНО: ГЛУБОКИЙ СТАРТ ВОЛНЫ БАЛАНСА TRI-STATE С ЛИСТЬЕВ-МЕТОДОВ -->
    // ================================================================= -->
    protected override void RestoreCheckedStatesFromConfig(FileSystemNode localRoot)
    {
        UniqueMap.Clear();
        BuildNodesMapInternal(localRoot, UniqueMap);

        var savedEntriesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Module.CheckedEntries != null)
        {
            foreach (var e in Module.CheckedEntries)
            {
                if (e != null && !string.IsNullOrEmpty(e.EntryPath))
                {
                    savedEntriesSet.Add(e.EntryPath.Replace('/', '.'));
                }
            }
        }

        // 1. Накатываем жесткие состояния true/false строго на конечные листья-функции Roslyn
        foreach (var nodePair in UniqueMap)
        {
            var node = nodePair.Value;
            if (node == null || !node.IsSyntaxNode) continue;

            bool isLeafNode = node.Children.Count == 0 || (node.Children.Count == 1 && node.Children[0].Name == "LoadingStub...");
            if (isLeafNode)
            {
                string nodeNormKey = node.EntryPath.Replace('/', '.');
                bool isSaved = savedEntriesSet.Contains(nodeNormKey);
                node.IsChecked = isSaved;
            }
        }

        // 2. ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Запускаем волну пересчета СНИЗУ ВВЕРХ строго с листьев-методов!
        // Проходим по синтаксическим нодам. Если это конечный метод — пинаем расчет его родительского класса.
        // Класс пересчитается в null, поднимет волну к namespace, тот пересчитается в null и поднимет волну к файлу!
        foreach (var nodePair in UniqueMap)
        {
            var node = nodePair.Value;
            if (node == null || !node.IsSyntaxNode) continue;

            bool isLeafNode = node.Children.Count == 0 || (node.Children.Count == 1 && node.Children[0].Name == "LoadingStub...");
            if (isLeafNode && node.Parent != null)
            {
                // Запускаем каскадный подъем баланса с уровня методов!
                RecalculateParentsBalance(node.Parent);
            }
        }

        // 3. Дополнительно страхуем чистые файлы диска, выбранные целиком (без синтаксических детей)
        var diskMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
        BuildDiskNodesMapRecursive(localRoot, diskMap);

        foreach (var pair in diskMap)
        {
            var node = pair.Value;
            if (node == null || !node.IsFile || node.IsSyntaxNode) continue;

            if ((node.Children.Count == 0 || node.Children.Any(c => c.Name == "LoadingStub...")) && node.IsChecked == true && node.Parent != null)
            {
                RecalculateParentsBalance(node.Parent);
            }
        }

        localRoot.NotifyComputedStateChanged();
    }

} // Финальное закрытие класса ProjectTreeManager!
