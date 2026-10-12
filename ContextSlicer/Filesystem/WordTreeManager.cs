using ContextSlicer.ContextBuilder;
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
    // ИСПРАВЛЕНО: ПЕРЕНАПРАВЛЕНИЕ ПОДСЧЕТА НА НАШ НОВЫЙ ООП-СБОРЩИК WORD  -->
    // ================================================================= -->
    public override async Task<long> CalculateSelectedCharactersAsync(bool includeDirectoryStructure, CancellationToken token)
    {
        var activeEntries = GetSelectedEntries();
        if (activeEntries.Count == 0 || string.IsNullOrEmpty(_docFilePath) || !File.Exists(_docFilePath))
        {
            return 0;
        }

        // Вызываем наш специализированный ContextBuilderWord!
        var wordBuilder = new ContextBuilderWord(string.Empty, string.Empty, includeDirectoryStructure, false);

        var dummyCheckedFiles = new List<FileSystemNode> { this.RootNode };
        var sb = await wordBuilder.BuildTextContentAsync(dummyCheckedFiles, activeEntries, token);

        return sb?.Length ?? 0;
    }

} // Финальное закрытие класса!
