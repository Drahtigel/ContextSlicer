using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ContextSlicer.Filesystem;

namespace ContextSlicer.ContextBuilder;

/// <summary>
/// Стратегия сборки контекста ИИ для локальных документов MS Word (.docx).
/// Интегрирована с универсальной объектной моделью WordDocumentTree для сохранения заголовков, списков и таблиц.
/// </summary>
public class ContextBuilderWord : ContextBuilderBase
{
    public ContextBuilderWord(string promptRules, string moduleRules, bool includeDirectoryStructure, bool includeImages)
        : base(promptRules, moduleRules, includeDirectoryStructure, includeImages) { }

    public override async Task<StringBuilder> BuildTextContentAsync(
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        var sb = new StringBuilder();
        if (savedEntries == null || savedEntries.Count == 0) return sb;

        // 1. Извлекаем абсолютный путь к физическому локальному файлу .docx из корня дерева
        string docxPath = checkedFiles?.FirstOrDefault()?.FullPath
            ?? savedEntries.FirstOrDefault(e => e != null && !string.IsNullOrEmpty(e.FilePath))?.FilePath
            ?? string.Empty;

        if (string.IsNullOrEmpty(docxPath) || !File.Exists(docxPath)) return sb;

        // Выгружаем системные правила промпта и модуля из абстрактного класса
        AppendSystemRules(sb);

        // 1. Создаем папку wordcache в директории приложения
        string wordCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wordcache");
        if (!Directory.Exists(wordCacheDir)) Directory.CreateDirectory(wordCacheDir);

        // 2. ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Вычисляем уникальный хэш-ID от полного пути файла .docx
        // Это гарантирует стабильную, уникальную папку images/{docId} для каждого локального документа!
        uint docPathHash = 0;
        unchecked
        {
            foreach (char c in docxPath) docPathHash = (docPathHash * 31) + c;
        }
        string docId = "w_" + docPathHash.ToString("x8");

        // 3. Передаем вычисленные папки в конструктор WordDocumentTree
        var documentTree = new WordDocumentTree(wordCacheDir, docId);
        documentTree.IncludeImages = IncludeImages; // Пробрасываем флаг картинок с UI

        // Передаем задачу разбора структуры, Title, Subtitle, списков и таблиц в WordDocumentTree
        documentTree.ParseToTree(docxPath, savedEntries);

        // 3. СБОРКА ИТОГОВОГО ТЕКСТА НА ОСНОВЕ СФОРМИРОВАННОГО ДЕРЕВА
        // Наливаем оглавление со структурой вкладок <tab> и честными уровнями level
        if (IncludeDirectoryStructure)
        {
            sb.Append(documentTree.BuildStructureMarkdown());
        }

        // Наливаем структурированную прозу, списки <li> и маркеры таблиц/изображений
        sb.Append(documentTree.BuildContentText());
        await Task.CompletedTask;

        return sb;
    }

    public override async Task GeneratePdfAsync(
        string outputPath,
        string fileName,
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        var sb = await BuildTextContentAsync(checkedFiles, savedEntries, token);
        string metaDataText = sb.ToString();
        if (string.IsNullOrWhiteSpace(metaDataText)) return;

        var document = new MigraDoc.DocumentObjectModel.Document();
        var style = document.Styles["Normal"];
        if (style != null) { style.Font.Name = "Arial"; style.Font.Size = 10; }

        var currentSection = document.AddSection();
        SetSectionMargins(currentSection);

        // Построчно читаем наполненный буфер и рендерим в PDF
        using (var reader = new StringReader(metaDataText))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                token.ThrowIfCancellationRequested();
                var p = currentSection.AddParagraph();
                p.AddFormattedText(line);

                if (string.IsNullOrWhiteSpace(line))
                {
                    p.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(4);
                }
            }
        }

        SavePdfToDiskAndOpen(document, outputPath, fileName);
    }
}
