using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem
{
    /// <summary>
    /// Универсальный контракт управления деревом проекта для любых типов контекстов (код / проза).
    /// </summary>
    public interface IProjectTreeManager
    {
        /// <summary>
        /// Ссылка на полностью собранный в памяти корень дерева для отображения на UI.
        /// </summary>
        FileSystemNode RootNode { get; }

        /// <summary>
        /// Возвращает плоский список синтаксических записей, у которых на экране горит ComputedState == true.
        /// </summary>
        List<SyntaxEntry> GetSelectedEntries();

        /// <summary>
        /// Возвращает контракт на получение файлов, выбранных целиком (для кода).
        /// </summary>
        List<string> GetCheckedFiles();

        /// <summary>
        /// Асинхронный подсчет веса выбранных элементов структуры.
        /// </summary>
        Task<long> CalculateSelectedCharactersAsync(bool includeDirectoryStructure, CancellationToken token);

        // ================================================================= -->
        // ДОБАВЛЕНО: КОНТРАКТЫ НА МЕДИАТОРНЫЙ УПРАВЛЯЕМЫЙ ПЕРЕСЧЕТ TRI-STATE -->
        // ================================================================= -->

        /// <summary>
        /// Принудительно пробивает жесткий статус true/false по всей вертикали наследников.
        /// </summary>
        void RecalculateChildrenCascade(FileSystemNode parentNode, bool targetState);

        /// <summary>
        /// Агрегирует состояние родительских веток на основе строгого математического баланса детей.
        /// </summary>
        void RecalculateParentsBalance(FileSystemNode? parentNode);
    }
}
