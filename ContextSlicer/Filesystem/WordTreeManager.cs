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
public class WordTreeManager : BaseTreeManager
{
    private readonly string _docFilePath;

    public WordTreeManager(ProjectConfig project, ContextModule module) : base(project, module)
    {
        _docFilePath = project.RootPath;
        InitializeWordTree();
    }

    private void InitializeWordTree()
    {
        var localRoot = new FileSystemNode
        {
            Name = Project.ProjectName,
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

        // Вызываем монолитный метод восстановления Tri-State состояний из базового класса!
        RestoreCheckedStatesFromConfig(localRoot);
        RootNode = localRoot;
    }
    // ================================================================= -->
    // ИСПРАВЛЕНО CS8604: ФИКСАЦИЯ КЛЮЧА ЧЕРЕЗ СТРОГУЮ ЛОКАЛЬНУЮ ПЕРЕМЕННУЮ -->
    // ================================================================= -->
    private void InjectWordNodeSecure(FileSystemNode localRoot, SyntaxEntry entry, Dictionary<string, FileSystemNode> buildingMap)
    {
        if (entry == null || string.IsNullOrEmpty(entry.EntryPath)) return;

        // ИСПРАВЛЕНО: Кэшируем путь в локальную non-nullable переменную.
        // Это раз и навсегда отрежет варнинг CS8604 на строке 118!
        string entryPathKey = entry.EntryPath;

        if (buildingMap.ContainsKey(entryPathKey)) return;

        // В дерево идут только заголовки и вкладки структуры, исключая кашу из плоского текста!
        if (entry.Type != EntryType.Heading && entry.Type != EntryType.Tab) return;

        uint rawHash = unchecked((uint)entryPathKey.GetHashCode());
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
            EntryPath = entryPathKey,
            IsExpanded = true,
            Parent = null
        };

        newNode.SetChecked(null, updateChildren: false, updateParent: false);

        string parentEntryPath = string.Empty;
        int lastSep = entryPathKey.LastIndexOf('/');

        if (lastSep > 0)
        {
            parentEntryPath = entryPathKey.Substring(0, lastSep).Trim();
        }

        if (!string.IsNullOrEmpty(parentEntryPath) && buildingMap.TryGetValue(parentEntryPath, out var parentNode))
        {
            newNode.Parent = parentNode;
            parentNode.Children.Add(newNode);
        }
        else
        {
            newNode.Parent = localRoot;
            localRoot.Children.Add(newNode);
        }

        // ИСПРАВЛЕНО: Передаем локальную переменную entryPathKey вместо entry.EntryPath!
        buildingMap[entryPathKey] = newNode;
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: АВТОНОМНЫЙ ПАССИВНЫЙ ПОДСЧЕТ СИМВОЛОВ И ТАБЛИЦ WORD   -->
    // ================================================================= -->
    public override async Task<long> CalculateSelectedCharactersAsync(bool includeDirectoryStructure, CancellationToken token)
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
                    var selectedPathsSet = new HashSet<string>(activeEntries.Select(e => e.EntryPath), StringComparer.OrdinalIgnoreCase);

                    if (includeDirectoryStructure)
                    {
                        foreach (string path in selectedPathsSet) totalLength += path.Length + 5;
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

                            if (!string.IsNullOrEmpty(styleId) && styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                                && int.TryParse(styleId.Substring(7), out int level))
                            {
                                string headingText = string.Concat(textNodes.Select(t => t.Text ?? string.Empty)).Trim();
                                if (!string.IsNullOrEmpty(headingText))
                                {
                                    int targetParentLevel = level - 1;
                                    while (targetParentLevel > 0 && !activePaths.ContainsKey(targetParentLevel)) targetParentLevel--;

                                    string parentPath = activePaths[targetParentLevel];
                                    currentChapterPath = string.IsNullOrEmpty(parentPath) ? headingText : $"{parentPath}/{headingText}";

                                    activePaths[level] = currentChapterPath;
                                    var keysToRemove = activePaths.Keys.Where(k => k > level).ToList();
                                    foreach (var key in keysToRemove) activePaths.Remove(key);
                                }
                            }

                            if (!string.IsNullOrEmpty(currentChapterPath) && selectedPathsSet.Contains(currentChapterPath))
                            {
                                totalLength += elementLength;
                            }
                        }
                        else if (element is DocumentFormat.OpenXml.Wordprocessing.Table table)
                        {
                            if (!string.IsNullOrEmpty(currentChapterPath) && selectedPathsSet.Contains(currentChapterPath))
                            {
                                totalLength += table.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableCell>()
                                    .Sum(c => c.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Sum(t => (t.Text ?? string.Empty).Length));
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
} // Финальное закрытие класса!
