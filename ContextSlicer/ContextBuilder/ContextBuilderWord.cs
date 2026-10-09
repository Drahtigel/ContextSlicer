using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ContextSlicer.Filesystem;

namespace ContextSlicer.ContextBuilder;

/// <summary>
/// Изолированный сборщик контекста ИИ для проектов локальных документов MS Word (.docx).
/// Вычитывает структуру напрямую из файла и генерирует форматы TXT/PDF с контролем каскада родителей.
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

        // Жестко извлекаем абсолютный путь к локальному файлу .docx из корня дерева
        string docxPath = checkedFiles?.FirstOrDefault()?.FullPath
            ?? savedEntries.FirstOrDefault(e => e != null && !string.IsNullOrEmpty(e.FilePath))?.FilePath
            ?? string.Empty;

        if (string.IsNullOrEmpty(docxPath) || !File.Exists(docxPath)) return sb;

        // Выгружаем общие правила промпта из абстрактного класса
        AppendSystemRules(sb);

        var selectedPathsSet = new HashSet<string>(
            savedEntries.Where(e => e != null).Select(e => e.EntryPath),
            StringComparer.OrdinalIgnoreCase);

        // Генерация оглавления для Word с честными атрибутами id, level и type
        if (IncludeDirectoryStructure)
        {
            sb.AppendLine("<document_structure>");
            sb.AppendLine("  <ul id=\"toc_root\">");
            foreach (var entry in savedEntries.Where(e => e != null))
            {
                string elementId = "h_" + unchecked((uint)entry.EntryPath.GetHashCode()).ToString("x8");
                int currentLevel = entry.EntryPath.Count(f => f == '/');
                sb.AppendLine($"    <li level=\"{currentLevel}\" type=\"heading\" id=\"{elementId}\">{entry.DisplayName}</li>");
            }
            sb.AppendLine("  </ul id=\"toc_root\">");
            sb.AppendLine("</document_structure>\n");
        }

        // Запускаем асинхронный итератор тела документа Word
        await ProcessWordBodyElementsInternalAsync(sb, docxPath, selectedPathsSet, token);

        return sb;
    }
    /// <summary>
    /// Линейный обход XML-структур OpenXML: собирает главы и таблицы с жестким контролем каскада родителей.
    /// </summary>
    private async Task ProcessWordBodyElementsInternalAsync(
        StringBuilder sb, string docxPath, HashSet<string> selectedPathsSet, CancellationToken token)
    {
        await Task.Run(() =>
        {
            try
            {
                using (var wordDoc = WordprocessingDocument.Open(docxPath, false))
                {
                    var mainPart = wordDoc.MainDocumentPart;
                    if (mainPart?.Document?.Body == null) return;

                    string currentChapterPath = string.Empty;
                    var activePaths = new Dictionary<int, string> { { 0, string.Empty } };
                    var bodyElements = mainPart.Document.Body.ChildElements;

                    foreach (var element in bodyElements)
                    {
                        token.ThrowIfCancellationRequested();

                        if (element is Paragraph paragraph)
                        {
                            var textNodes = paragraph.Descendants<Text>().ToList();

                            string pText = string.Concat(textNodes.Select(t => t.Text ?? string.Empty))
                                             .Replace("\r", "")
                                             .Replace("\n", "")
                                             .Replace("\u00A0", " ");

                            string styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
                            string cleanStyleId = styleId.Replace(" ", "");

                            bool isHeading = !string.IsNullOrEmpty(cleanStyleId) &&
                                             (cleanStyleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ||
                                              cleanStyleId.StartsWith("Заголовок", StringComparison.OrdinalIgnoreCase));

                            if (isHeading)
                            {
                                string levelDigits = new string(cleanStyleId.Where(char.IsDigit).ToArray());
                                if (!int.TryParse(levelDigits, out int level)) level = 1;

                                string headingText = pText.Trim();
                                if (!string.IsNullOrEmpty(headingText))
                                {
                                    int targetParentLevel = level - 1;
                                    while (targetParentLevel > 0 && !activePaths.ContainsKey(targetParentLevel)) targetParentLevel--;

                                    string parentPath = activePaths[targetParentLevel];
                                    currentChapterPath = string.IsNullOrEmpty(parentPath) ? headingText : $"{parentPath}/{headingText}";

                                    activePaths[level] = currentChapterPath;
                                    var keysToRemove = activePaths.Keys.Where(k => k > level).ToList();
                                    foreach (var key in keysToRemove) activePaths.Remove(key);

                                    // Тег заголовка генерируется строго при валидности всей вертикали родителей
                                    if (IsPathChainValid(currentChapterPath, selectedPathsSet))
                                    {
                                        string elementId = "h_" + unchecked((uint)currentChapterPath.GetHashCode()).ToString("x8");
                                        sb.AppendLine($"<heading id=\"{elementId}\" path=\"{currentChapterPath}\">");
                                        sb.AppendLine($"  {headingText}");
                                    }
                                }
                            }
                            // Абзацы текста добавляются строго при валидности всей родительской цепочки
                            else if (!string.IsNullOrEmpty(currentChapterPath) &&
                                     IsPathChainValid(currentChapterPath, selectedPathsSet) &&
                                     !string.IsNullOrWhiteSpace(pText))
                            {
                                sb.AppendLine($"  {pText.Trim()}");
                            }
                        }
                        else if (element is Table table)
                        {
                            // Таблицы вычитываются строго при валидности всей родительской цепочки
                            if (!string.IsNullOrEmpty(currentChapterPath) && IsPathChainValid(currentChapterPath, selectedPathsSet))
                            {
                                sb.AppendLine("  <table>");
                                foreach (var row in table.Descendants<TableRow>())
                                {
                                    sb.AppendLine("    <tr>");
                                    foreach (var cell in row.Descendants<TableCell>())
                                    {
                                        string cellText = string.Concat(cell.Descendants<Text>().Select(t => t.Text))
                                                                .Replace("\r", "")
                                                                .Replace("\n", "")
                                                                .Trim();
                                        sb.AppendLine($"      <td>{cellText}</td>");
                                    }
                                    sb.AppendLine("    </tr>");
                                }
                                sb.AppendLine("  </table>");
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(currentChapterPath) && IsPathChainValid(currentChapterPath, selectedPathsSet))
                    {
                        sb.AppendLine("</heading>");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Word OOP Body Iterator Error] {ex.Message}");
            }
        }, token);
    }

    /// <summary>
    /// Шаблонная реализация генерации издательского PDF из локального Word-файла.
    /// </summary>
    public override async Task GeneratePdfAsync(string outputPath, string fileName, List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        var sb = await BuildTextContentAsync(checkedFiles, savedEntries, token);
        string metaDataText = sb.ToString();
        if (string.IsNullOrWhiteSpace(metaDataText)) return;

        var document = new MigraDoc.DocumentObjectModel.Document();
        var style = document.Styles["Normal"];
        if (style != null) { style.Font.Name = "Arial"; style.Font.Size = 10; }

        var currentSection = document.AddSection();
        SetSectionMargins(currentSection);

        // Вспомогательный вызов рендерера строк из базового абстрактного класса
        SavePdfToDiskAndOpen(document, outputPath, fileName);
    }

    /// <summary>
    /// Контракт на будущую сборку DocX.
    /// </summary>
    public override Task GenerateDocxAsync(string outputPath, string fileName, List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
