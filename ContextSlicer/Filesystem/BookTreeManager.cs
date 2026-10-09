using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem;

/// <summary>
/// Изолированный контроллер структуры литературных проектов Google Docs.
/// </summary>
public class BookTreeManager : BaseTreeManager
{
    private readonly string _cacheFilePath;

    public BookTreeManager(ProjectConfig project, ContextModule module) : base(project, module)
    {
        string docId = Google.GoogleDownloader.ExtractDocumentId(project.RootPath);
        string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
        _cacheFilePath = Path.Combine(cacheDir, $"{docId}.json");

        // ИНТЕЛЛЕКТУАЛЬНАЯ АВТОМАТИЧЕСКАЯ СВЕРКА КЭША С ОБЛАКОМ
        try
        {
            var downloader = new Google.GoogleDownloader();
            string mainVMKeyPath = string.Empty;

            var mainVM = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                System.Windows.Application.Current.MainWindow?.DataContext as MainViewModel);

            if (mainVM != null && !string.IsNullOrEmpty(project.GoogleApiKey))
            {
                mainVMKeyPath = mainVM.GetKeyPathByEmail(project.GoogleApiKey);
            }

            if (File.Exists(_cacheFilePath) && File.Exists(mainVMKeyPath))
            {
                DateTime? cloudModifiedTime = Task.Run(async () =>
                    await downloader.GetCloudModifiedTimeAsync(project.RootPath, mainVMKeyPath)
                ).GetAwaiter().GetResult();

                if (cloudModifiedTime.HasValue)
                {
                    DateTime localCacheTime = File.GetLastWriteTime(_cacheFilePath);

                    if (cloudModifiedTime.Value > localCacheTime)
                    {
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

    private void InitializeBookTree()
    {
        var localRoot = new FileSystemNode
        {
            Name = Project.ProjectName,
            FullPath = _cacheFilePath,
            RelativePath = string.Empty,
            IsFile = false,
            IsChecked = null
        };

        if (File.Exists(_cacheFilePath))
        {
            try
            {
                var parser = SyntaxParserFactory.GetParser(".gdoc");
                if (parser != null)
                {
                    var allBookEntries = Task.Run(async () =>
                        await parser.ParseFileAsync(_cacheFilePath, Project.RootPath).ConfigureAwait(false)
                    ).GetAwaiter().GetResult();

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

        // Вызываем монолитный метод восстановления Tri-State состояний из базового класса!
        RestoreCheckedStatesFromConfig(localRoot);
        RootNode = localRoot;
    }

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
            Parent = null
        };
        newNode.SetChecked(null, updateChildren: false, updateParent: false);

        string parentEntryPath = string.Empty;
        int lastSep = entry.EntryPath.LastIndexOf('/');
        if (lastSep > 0) parentEntryPath = entry.EntryPath.Substring(0, lastSep);

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

        buildingMap[entry.EntryPath] = newNode;
    }

    public override async Task<long> CalculateSelectedCharactersAsync(bool includeDirectoryStructure, CancellationToken token)
    {
        var activeEntries = GetSelectedEntries();
        if (activeEntries.Count == 0 || string.IsNullOrEmpty(_cacheFilePath) || !File.Exists(_cacheFilePath))
        {
            return 0;
        }

        string fullText = await GoogleDocBuilder.BuildContextTextAsync(
            activeEntries, _cacheFilePath, includeDirectoryStructure, false, token
        );

        return fullText?.Length ?? 0;
    }
}
