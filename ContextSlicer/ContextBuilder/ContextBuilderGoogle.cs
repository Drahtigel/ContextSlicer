using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ContextSlicer.Filesystem;

namespace ContextSlicer.ContextBuilder;

/// <summary>
/// Стратегия сборки контекста ИИ для Google Документов (Tabs API).
/// Интегрирована с универсальной объектной моделью GoogleDocumentTree для выгрузки вкладок, списков и таблиц.
/// </summary>
public class ContextBuilderGoogle : ContextBuilderBase
{
    public ContextBuilderGoogle(string promptRules, string moduleRules, bool includeDirectoryStructure, bool includeImages)
        : base(promptRules, moduleRules, includeDirectoryStructure, includeImages) { }

    public override async Task<StringBuilder> BuildTextContentAsync(
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        var sb = new StringBuilder();
        if (savedEntries == null || savedEntries.Count == 0) return sb;

        // 1. Извлекаем абсолютный путь к физическому файлу кэша JSON на диске
        string bookRootPath = checkedFiles?.FirstOrDefault()?.FullPath ?? string.Empty;
        string googleCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");

        if (string.IsNullOrEmpty(bookRootPath) || !File.Exists(bookRootPath))
        {
            var firstEntry = savedEntries.FirstOrDefault(e => e != null && !string.IsNullOrEmpty(e.FilePath));
            if (firstEntry != null)
            {
                bookRootPath = Path.Combine(googleCacheDir, Path.GetFileName(firstEntry.FilePath));
            }
        }

        // Если кэш отсутствует — ищем самый свежий json-файл в папке googlecache
        if (string.IsNullOrEmpty(bookRootPath) || !File.Exists(bookRootPath))
        {
            if (Directory.Exists(googleCacheDir))
            {
                var cacheFiles = Directory.GetFiles(googleCacheDir, "*.json");
                var latestCacheFile = cacheFiles
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();

                if (latestCacheFile != null) bookRootPath = latestCacheFile.FullName;
            }
        }

        if (string.IsNullOrEmpty(bookRootPath) || !File.Exists(bookRootPath)) return sb;

        // Выгружаем системные правила промпта и модуля из абстрактного класса
        AppendSystemRules(sb);

        // Извлекаем ID документа для создания правильной папки images/{id}
        string docId = Path.GetFileNameWithoutExtension(bookRootPath);

        // ИСПРАВЛЕНО: Передаем корень папки кэша и ID документа в конструктор DOM-дерева
        var documentTree = new GoogleDocumentTree(googleCacheDir, docId);
        documentTree.IncludeImages = IncludeImages;
        documentTree.ParseToTree(bookRootPath, savedEntries);

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
