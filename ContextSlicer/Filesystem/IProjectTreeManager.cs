using System.Collections.Generic;

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

        // ИСПРАВЛЕНО: Добавляем контракт на получение файлов выбранных целиком
        List<string> GetCheckedFiles();
        Task<long> CalculateSelectedCharactersAsync(bool includeDirectoryStructure, CancellationToken token);
    }
}
