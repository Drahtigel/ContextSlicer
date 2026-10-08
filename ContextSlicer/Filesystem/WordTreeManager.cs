using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem;

/// <summary>
/// Автономный контроллер структуры локальных документов MS Word (.docx).
/// </summary>
public class WordTreeManager : IProjectTreeManager
{
    private readonly ProjectConfig _project;
    private readonly ContextModule _module;
    private readonly string _docFilePath;

    public FileSystemNode RootNode { get; private set; } = null!;

    public WordTreeManager(ProjectConfig project, ContextModule module)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _module = module ?? throw new ArgumentNullException(nameof(module));
        _docFilePath = project.RootPath; // Для Word в RootPath хранится физический путь к файлу .docx

        InitializeWordTree();
    }

    private void InitializeWordTree()
    {
        var localRoot = new FileSystemNode
        {
            Name = _project.ProjectName,
            FullPath = _docFilePath,
            RelativePath = string.Empty,
            IsFile = false,
            IsChecked = null
        };

        if (File.Exists(_docFilePath))
        {
            try
            {
                var parser = SyntaxParserFactory.GetParser(".docx");
                if (parser != null)
                {
                    // Безопасно вычитываем структуру заголовков docx в фоновом потоке пула
                    var allEntries = Task.Run(async () =>
                        await parser.ParseFileAsync(_docFilePath, Path.GetFileName(_docFilePath)).ConfigureAwait(false)
                    ).GetAwaiter().GetResult();

                    var currentBuildingMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);

                    foreach (var entry in allEntries)
                    {
                        if (entry != null)
                        {
                            InjectWordNodeSecure(localRoot, entry, currentBuildingMap);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Word Engine Tree Error] {ex.Message}");
            }
        }

        var uniqueMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
        BuildNodesMap(localRoot, uniqueMap);

        var savedEntriesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_module.CheckedEntries != null)
        {
            foreach (var e in _module.CheckedEntries)
            {
                if (e != null && !string.IsNullOrEmpty(e.EntryPath)) savedEntriesSet.Add(e.EntryPath);
            }
        }

        foreach (var nodePair in uniqueMap)
        {
            var node = nodePair.Value;
            if (node == null || !node.IsSyntaxNode) continue;

            // ИСПРАВЛЕНО: Безопасный подсчет листьев структуры
            bool isLeafNode = node.Children.Count == 0 || (node.Children.Count == 1 && node.Children[0].Name == "LoadingStub...");

            if (isLeafNode)
            {
                bool isSaved = savedEntriesSet.Contains(node.EntryPath);
                node.SetChecked(isSaved ? true : false, updateChildren: false, updateParent: false);
            }
            // ИСПРАВЛЕНО: Ветка else { node.SetChecked(null); } ПОЛНОСТЬЮ УДАЛЕНА!
            // Родителю больше не навязывается сырой null извне. Его ComputedState 
            // автоматически и чисто посчитается силами WPF-привязки, не плодя дедлоков!
        }

        // Одиночный, безопасный финишный сигнал для перерисовки дерева на экране
        localRoot.NotifyComputedStateChanged();
        RootNode = localRoot;
    }
    // ================================================================= -->
    // ИСПРАВЛЕНО: СТРОГАЯ СТРУКТУРНАЯ ФИЛЬТРАЦИЯ И СБОРКА ИЕРАРХИИ WORD -->
    // ================================================================= -->
    private void InjectWordNodeSecure(FileSystemNode localRoot, SyntaxEntry entry, Dictionary<string, FileSystemNode> buildingMap)
    {
        if (entry == null || string.IsNullOrEmpty(entry.EntryPath)) return;

        // Жесткая защита от дублирования узлов в карте сборки
        if (buildingMap.ContainsKey(entry.EntryPath)) return;

        // ТВОЙ АЛГОРИТМ: В дерево TreeView должны идти ТОЛЬКО структурные заголовки (Heading)!
        // Обычные текстовые абзацы и скрытые маркеры не должны засорять UI и ломать компоновку WPF!
        if (entry.Type != EntryType.Heading && entry.Type != EntryType.Tab) return;

        uint rawHash = unchecked((uint)entry.EntryPath.GetHashCode());
        string elementId = "h_" + rawHash.ToString("x8");

        var newNode = new FileSystemNode
        {
            Name = entry.DisplayName != null ? entry.DisplayName.Trim() : string.Empty,
            RelativePath = entry.FilePath ?? string.Empty,
            FullPath = localRoot.FullPath ?? string.Empty,
            IsFile = false,
            IsSyntaxNode = true,
            SyntaxType = entry.Type,
            SyntaxSpanInfo = entry.SpanInfo ?? string.Empty,
            EntryPath = entry.EntryPath,
            IsExpanded = true,
            Parent = null
        };

        newNode.SetChecked(null, updateChildren: false, updateParent: false);

        string parentEntryPath = string.Empty;
        int lastSep = entry.EntryPath.LastIndexOf('/');

        if (lastSep > 0)
        {
            parentEntryPath = entry.EntryPath.Substring(0, lastSep).Trim();
        }

        // Выполняем точечный поиск родительского заголовка в карте сборки текущего шага
        if (!string.IsNullOrEmpty(parentEntryPath) && buildingMap.TryGetValue(parentEntryPath, out var parentNode))
        {
            newNode.Parent = parentNode;
            parentNode.Children.Add(newNode);
        }
        else
        {
            // Если это корневой заголовок (например, "Глава 1.") — его родителем становится корень проекта
            newNode.Parent = localRoot;
            localRoot.Children.Add(newNode);
        }

        // Фиксируем заголовок в карте, чтобы его вложенные подзаголовки могли его найти!
        buildingMap[entry.EntryPath] = newNode;
    }
    private void BuildNodesMap(FileSystemNode node, Dictionary<string, FileSystemNode> map)
    {
        if (node == null) return;
        if (!string.IsNullOrEmpty(node.EntryPath)) map[node.EntryPath] = node;
        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy) BuildNodesMap(child, map);
    }

    public List<string> GetCheckedFiles() => new();

    public List<SyntaxEntry> GetSelectedEntries()
    {
        return _module.CheckedEntries != null
            ? _module.CheckedEntries.Where(e => e != null).ToList()
            : new List<SyntaxEntry>();
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: АВТОНОМНЫЙ ПАССИВНЫЙ ПОДСЧЕТ СИМВОЛОВ И ТАБЛИЦ WORD   -->
    // ================================================================= -->
    public async Task<long> CalculateSelectedCharactersAsync(bool includeDirectoryStructure, CancellationToken token)
    {
        var activeEntries = GetSelectedEntries();
        if (activeEntries.Count == 0 || string.IsNullOrEmpty(_docFilePath) || !File.Exists(_docFilePath))
        {
            return 0;
        }

        return await Task.Run(() =>
        {
            try
            {
                using (var wordDoc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(_docFilePath, false))
                {
                    var mainPart = wordDoc.MainDocumentPart;
                    if (mainPart == null || mainPart.Document == null || mainPart.Document.Body == null) return 0L;

                    long totalLength = 0;
                    var bodyElements = mainPart.Document.Body.ChildElements;

                    // Сбор хэш-сета выбранных путей для O(1) поиска
                    var selectedPathsSet = new HashSet<string>(activeEntries.Select(e => e.EntryPath), StringComparer.OrdinalIgnoreCase);

                    // Если флаг оглавления активен — добавляем вес разметки структуры каталогов
                    if (includeDirectoryStructure)
                    {
                        foreach (string path in selectedPathsSet)
                        {
                            totalLength += path.Length + 5;
                        }
                    }

                    string currentChapterPath = string.Empty;
                    var activePaths = new Dictionary<int, string> { { 0, string.Empty } };

                    foreach (var element in bodyElements)
                    {
                        token.ThrowIfCancellationRequested();

                        if (element is DocumentFormat.OpenXml.Wordprocessing.Paragraph paragraph)
                        {
                            var textNodes = paragraph.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().ToList();
                            int elementLength = textNodes.Sum(t => (t.Text ?? string.Empty).Length);

                            string styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;

                            // Если встретили заголовок — пересчитываем текущую координату пути
                            if (!string.IsNullOrEmpty(styleId) && styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                                && int.TryParse(styleId.Substring(7), out int level))
                            {
                                string headingText = string.Concat(textNodes.Select(t => t.Text ?? string.Empty)).Trim();

                                if (!string.IsNullOrEmpty(headingText))
                                {
                                    int targetParentLevel = level - 1;
                                    while (targetParentLevel > 0 && !activePaths.ContainsKey(targetParentLevel))
                                    {
                                        targetParentLevel--;
                                    }

                                    string parentPath = activePaths[targetParentLevel];
                                    currentChapterPath = string.IsNullOrEmpty(parentPath) ? headingText : $"{parentPath}/{headingText}";

                                    activePaths[level] = currentChapterPath;
                                    var keysToRemove = activePaths.Keys.Where(k => k > level).ToList();
                                    foreach (var key in keysToRemove) activePaths.Remove(key);
                                }
                            }

                            // ТВОЙ АЛГОРИТМ: Если текущий абзац принадлежит выбранной главе — суммируем его вес!
                            if (!string.IsNullOrEmpty(currentChapterPath) && selectedPathsSet.Contains(currentChapterPath))
                            {
                                totalLength += elementLength;
                            }
                        }
                        else if (element is DocumentFormat.OpenXml.Wordprocessing.Table table)
                        {
                            // Если сложная таблица находится внутри выбранной главы — вычитываем её текст
                            if (!string.IsNullOrEmpty(currentChapterPath) && selectedPathsSet.Contains(currentChapterPath))
                            {
                                int tableLength = table.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableCell>()
                                    .Sum(cell => cell.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>()
                                    .Sum(t => (t.Text ?? string.Empty).Length));

                                totalLength += tableLength;
                            }
                        }
                    }

                    return totalLength;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Word Size Calculation Error] {ex.Message}");
                return 0L;
            }
        }, token);
    }

}
