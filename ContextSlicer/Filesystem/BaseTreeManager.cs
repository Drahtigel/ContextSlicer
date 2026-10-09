using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem;

public abstract class BaseTreeManager : IProjectTreeManager
{
    protected readonly ProjectConfig Project;
    protected readonly ContextModule Module;

    // Централизованная оперативная карта всех нод дерева для O(1) поиска
    protected readonly Dictionary<string, FileSystemNode> UniqueMap = new(StringComparer.OrdinalIgnoreCase);

    public FileSystemNode RootNode { get; protected set; } = null!;

    protected BaseTreeManager(ProjectConfig project, ContextModule module)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public abstract Task<long> CalculateSelectedCharactersAsync(bool includeDirectoryStructure, CancellationToken token);
    public virtual List<string> GetCheckedFiles() => new();

    public List<SyntaxEntry> GetSelectedEntries()
    {
        return Module.CheckedEntries != null
            ? Module.CheckedEntries.Where(e => e != null).ToList()
            : new List<SyntaxEntry>();
    }

    // ================================================================= -->
    // ЦЕНТРАЛИЗОВАННЫЙ СУПЕР-КАЛЬКУЛЯТОР СВЕРХУ ВНИЗ И СНИЗУ ВВЕРХ      -->
    // ================================================================= -->

    // ================================================================= -->
    // ИСПРАВЛЕНО: ЖЕСТКАЯ ПУБЛИКАЦИЯ СИГНАЛОВ ДЛЯ СИНХРОНИЗАЦИИ WPF UI  -->
    // ================================================================= -->
    /// <summary>
    /// Пробивает статус true/false вниз и принудительно уведомляет биндинги WPF!
    /// </summary>
    public void RecalculateChildrenCascade(FileSystemNode parentNode, bool targetState)
    {
        if (parentNode == null || parentNode.Children == null) return;

        foreach (var child in parentNode.Children)
        {
            if (child == null || child.Name == "LoadingStub...") continue;

            // Жестко выставляем внутренний флаг
            child.IsChecked = targetState;

            // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Принудительно пинаем UI-уведомления для каждого ребенка!
            child.NotifyComputedStateChanged();

            // Рекурсивно шагаем до самого низа структуры
            RecalculateChildrenCascade(child, targetState);
        }
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: МАТЕМАТИЧЕСКИ СТРОГИЙ КАЛЬКУЛЯТОР БАЛАНСА РОДИТЕЛЕЙ   -->
    // ================================================================= -->
    public void RecalculateParentsBalance(FileSystemNode? parentNode)
    {
        if (parentNode == null || parentNode.Children == null || parentNode.Children.Count == 0) return;

        bool hasChecked = false;
        bool hasUnchecked = false;
        bool hasIndeterminate = false;

        // Обходим только прямых детей текущего узла
        for (int i = 0; i < parentNode.Children.Count; i++)
        {
            var child = parentNode.Children[i];

            // Пропускаем заглушку ленивой загрузки, она не участвует в балансе веса
            if (child == null || child.Name == "LoadingStub...") continue;

            // Опрашиваем живое вычисленное состояние ребенка
            bool? childState = child.IsChecked;

            if (childState == true) hasChecked = true;
            else if (childState == false) hasUnchecked = true;
            else hasIndeterminate = true;
        }

        bool? newParentState;

        // Строгий логический конъюнкт Tri-State структуры
        if (hasIndeterminate || (hasChecked && hasUnchecked))
            newParentState = null; // Каша или квадратик у детей -> папка горит квадратиком
        else if (hasChecked && !hasUnchecked)
            newParentState = true; // Все дети выбраны -> папка горит галочкой
        else
            newParentState = false; // Все дети пустые -> папка гаснет

        if (parentNode.IsChecked != newParentState)
        {
            parentNode.IsChecked = newParentState;

            // Принудительно заставляем WPF мгновенно перерисовать этот родительский узел
            parentNode.NotifyComputedStateChanged();
        }

        // Рекурсивно поднимаем волну пересчета по вертикали власти до самого верха репозитория
        if (parentNode.Parent != null)
        {
            RecalculateParentsBalance(parentNode.Parent);
        }
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
        // Очищаем и заново строим глобальную карту нод текущего менеджера
        UniqueMap.Clear();
        BuildNodesMapInternal(localRoot, UniqueMap);

        // Хэш-сет сохраненных в JSON точечных элементов
        var savedEntriesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Module.CheckedEntries != null)
        {
            foreach (var e in Module.CheckedEntries)
            {
                if (e != null && !string.IsNullOrEmpty(e.EntryPath))
                    savedEntriesSet.Add(e.EntryPath);
            }
        }

        // 1. Сначала накатываем жесткие состояния true/false строго на конечные листья-главы
        foreach (var nodePair in UniqueMap)
        {
            var node = nodePair.Value;
            if (node == null || !node.IsSyntaxNode) continue;

            bool isLeafNode = node.Children.Count == 0 || (node.Children.Count == 1 && node.Children[0].Name == "LoadingStub...");
            if (isLeafNode)
            {
                bool isSaved = savedEntriesSet.Contains(node.EntryPath);
                node.IsChecked = isSaved;
            }
        }

        // 2. ТВОЙ АЛГОРИТМ (СНИЗУ ВВЕРХ): Один раз рекурсивно пересчитываем баланс родителей для всего дерева!
        // Проходим по листьям и пинаем расчет их родителей — папки пассивно примут идеальные квадратики или галочки.
        foreach (var nodePair in UniqueMap)
        {
            var node = nodePair.Value;
            if (node == null || !node.IsSyntaxNode) continue;

            bool isLeafNode = node.Children.Count == 0 || (node.Children.Count == 1 && node.Children[0].Name == "LoadingStub...");
            if (isLeafNode && node.Parent != null)
            {
                RecalculateParentsBalance(node.Parent);
            }
        }
    }
}
