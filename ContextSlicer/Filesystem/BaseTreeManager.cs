using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem;

/// <summary>
/// Базовый монолитный класс для всех менеджеров структур проектов.
/// Обеспечивает сквозную унификацию построения карт, синхронизацию кэша и расчет Tri-State.
/// </summary>
public abstract class BaseTreeManager : IProjectTreeManager
{
    protected readonly ProjectConfig Project;
    protected readonly ContextModule Module;

    public FileSystemNode RootNode { get; protected set; } = null!;

    protected BaseTreeManager(ProjectConfig project, ContextModule module)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Module = module ?? throw new ArgumentNullException(nameof(module));
    }

    /// <summary>
    /// Контракт на асинхронный подсчет символов контента, уникальный для каждого типа проекта.
    /// </summary>
    public abstract Task<long> CalculateSelectedCharactersAsync(bool includeDirectoryStructure, CancellationToken token);

    /// <summary>
    /// Возвращает список файлов, выбранных целиком (перегружается в менеджере кода).
    /// </summary>
    public virtual List<string> GetCheckedFiles() => new();

    /// <summary>
    /// Возвращает стерильный список точечно выбранных элементов конфигурации модуля.
    /// </summary>
    public List<SyntaxEntry> GetSelectedEntries()
    {
        return Module.CheckedEntries != null
            ? Module.CheckedEntries.Where(e => e != null).ToList()
            : new List<SyntaxEntry>();
    }

    /// <summary>
    /// Унифицированный рекурсивный построитель карты нод O(1) для быстрого сличения состояний.
    /// Исключает дублирование и рассинхронизацию логики обхода дочерних коллекций.
    /// </summary>
    protected void BuildNodesMapInternal(FileSystemNode node, Dictionary<string, FileSystemNode> map)
    {
        if (node == null) return;

        if (!string.IsNullOrEmpty(node.EntryPath))
            map[node.EntryPath] = node;
        else if (!string.IsNullOrEmpty(node.RelativePath))
            map[node.RelativePath.Replace('/', '\\')] = node;

        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
            BuildNodesMapInternal(child, map);
    }

    /// <summary>
    /// Единый шаблонный метод накатки сохраненных состояний Tri-State из JSON-конфига модуля.
    /// Гарантирует абсолютно одинаковое поведение чекбоксов для кода, книг и документов Word!
    /// </summary>
    protected void RestoreCheckedStatesFromConfig(FileSystemNode localRoot)
    {
        var uniqueMap = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
        BuildNodesMapInternal(localRoot, uniqueMap);

        // Хэш-сет сохраненных точечных элементов
        var savedEntriesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Module.CheckedEntries != null)
        {
            foreach (var e in Module.CheckedEntries)
            {
                if (e != null && !string.IsNullOrEmpty(e.EntryPath))
                    savedEntriesSet.Add(e.EntryPath);
            }
        }

        // Выполняем точечную накатку только на конечные листья структуры
        foreach (var nodePair in uniqueMap)
        {
            var node = nodePair.Value;
            if (node == null) continue;

            // Если это литературный синтаксический узел (заголовок или вкладка)
            if (node.IsSyntaxNode)
            {
                bool isLeafNode = node.Children.Count == 0 || (node.Children.Count == 1 && node.Children[0].Name == "LoadingStub...");
                if (isLeafNode)
                {
                    bool isSaved = savedEntriesSet.Contains(node.EntryPath);
                    node.SetChecked(isSaved, updateChildren: false, updateParent: false);
                }
                else
                {
                    node.SetChecked(null, updateChildren: false, updateParent: false);
                }
            }
        }

        // Запускаем один сквозной, безопасный Tri-State пересчет снизу вверх для всего дерева на UI
        localRoot.NotifyComputedStateChanged();
    }
}
