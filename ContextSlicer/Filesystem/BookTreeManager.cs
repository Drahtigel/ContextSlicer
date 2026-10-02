// ================================================================= -->
// ИЗОЛИРОВАННЫЙ КОНТРОЛЛЕР СТРУКТУРЫ ЛИТЕРАТУРНЫХ ПРОЕКТОВ GOOGLE  -->
// ================================================================= -->
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem
{
    public class BookTreeManager
    {
        private readonly ProjectConfig _project;
        private readonly ContextModule _module;
        private readonly string _cacheFilePath;

        public FileSystemNode RootNode { get; private set; } = null!;

        public BookTreeManager(ProjectConfig project, ContextModule module)
        {
            _project = project ?? throw new ArgumentNullException(nameof(project));
            _module = module ?? throw new ArgumentNullException(nameof(module));

            // Вычисляем физический путь к JSON-кэшу документа на диске
            string docId = Google.GoogleDownloader.ExtractDocumentId(project.RootPath);
            string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
            _cacheFilePath = Path.Combine(cacheDir, $"{docId}.json");

            InitializeBookTree();
        }

        /// <summary>
        /// Чистый алгоритм слияния литературного дерева по схеме автора
        /// </summary>
        private void InitializeBookTree()
        {
            // 1. Создаем изолированный корень книги
            var localRoot = new FileSystemNode
            {
                Name = _project.ProjectName,
                FullPath = _cacheFilePath,
                RelativePath = string.Empty,
                IsFile = false,
                IsChecked = null
            };

            // 2. ВЫЧИТЫВАЕМ ДОКУМЕНТ: Строим полное дерево из кэша. Все ветки изначально в null!
            if (File.Exists(_cacheFilePath))
            {
                try
                {
                    var parser = SyntaxParserFactory.GetParser(".gdoc");
                    if (parser != null)
                    {
                        var allBookEntries = Task.Run(() => parser.ParseFileAsync(_cacheFilePath, _project.RootPath)).GetAwaiter().GetResult();
                        foreach (var entry in allBookEntries)
                        {
                            if (entry != null) InjectBookNode(localRoot, entry);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Book Engine Error] {ex.Message}");
                }
            }

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

            // 3. СЛИЧАЕМ И СБРАСЫВАЕМ: Конечные ветки без отметок переводим в False, промежуточные - в null
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

            // 4. ПЕРЕСЧИТЫВАЕМ ВЕТКИ СО ВЛОЖЕННОСТЬЮ: Сквозной Tri-State снизу вверх
            DeepVerifyBookStates(localRoot);

            RootNode = localRoot;
        }

        private void InjectBookNode(FileSystemNode localRoot, SyntaxEntry entry)
        {
            var fileNodesMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
            BuildNodesMap(localRoot, fileNodesMap);

            if (fileNodesMap.ContainsKey(entry.EntryPath)) return;

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
                Parent = localRoot,
                IsExpanded = isContainer,
                IsChecked = null
            };
            newNode.SetChecked(null, updateChildren: false, updateParent: false);

            string parentEntryPath = string.Empty;
            int lastSep = entry.EntryPath.LastIndexOf('/');
            if (lastSep > 0) parentEntryPath = entry.EntryPath.Substring(0, lastSep);

            if (!string.IsNullOrEmpty(parentEntryPath) && fileNodesMap.TryGetValue(parentEntryPath, out var parentNode))
            {
                newNode.Parent = parentNode;
                parentNode.Children.Add(newNode);
            }
            else
            {
                localRoot.Children.Add(newNode);
            }
        }

        private void DeepVerifyBookStates(FileSystemNode node)
        {
            if (node == null) return;

            var childrenCopy = new List<FileSystemNode>(node.Children);
            foreach (var child in childrenCopy) DeepVerifyBookStates(child);

            if (node.Children.Count > 0)
            {
                bool hasChecked = false;
                bool hasUnchecked = false;
                bool hasIndeterminate = false;

                foreach (var child in node.Children)
                {
                    if (child.IsChecked == true) hasChecked = true;
                    else if (child.IsChecked == false) hasUnchecked = true;
                    else hasIndeterminate = true;
                }

                if (hasIndeterminate || (hasChecked && hasUnchecked)) node.IsChecked = null;
                else if (hasChecked) node.IsChecked = true;
                else node.IsChecked = false;
            }
        }

        private void BuildNodesMap(FileSystemNode node, Dictionary<string, FileSystemNode> map)
        {
            if (node == null) return;
            if (!string.IsNullOrEmpty(node.EntryPath)) map[node.EntryPath] = node;
            var childrenCopy = new List<FileSystemNode>(node.Children);
            foreach (var child in childrenCopy) BuildNodesMap(child, map);
        }
    }
}
