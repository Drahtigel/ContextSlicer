using Newtonsoft.Json.Linq;
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
/// Изолированный сборщик контекста ИИ для проектов Google Docs.
/// Вычитывает структуру из дискового JSON-кэша и генерирует форматы TXT/PDF.
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

        // Определяем абсолютный путь к локальному JSON-файлу кэша Google Doc
        string bookRootPath = checkedFiles?.FirstOrDefault()?.FullPath ?? string.Empty;
        if (string.IsNullOrEmpty(bookRootPath))
        {
            string googleCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
            var firstEntry = savedEntries.FirstOrDefault(e => e != null && !string.IsNullOrEmpty(e.FilePath));
            if (firstEntry != null)
                bookRootPath = Path.Combine(googleCacheDir, Path.GetFileName(firstEntry.FilePath));
        }

        if (string.IsNullOrEmpty(bookRootPath) || !File.Exists(bookRootPath)) return sb;

        // Выгружаем общие правила промпта из абстрактного класса
        AppendSystemRules(sb);

        var selectedPathsSet = new HashSet<string>(
            savedEntries.Where(e => e != null).Select(e => e.EntryPath),
            StringComparer.OrdinalIgnoreCase);

        // Генерация оглавления для Google Docs с честными атрибутами id, level и type
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

        // Запускаем асинхронную читку и парсинг JSON-документа
        await Task.Run(async () =>
        {
            try
            {
                string jsonContent = await File.ReadAllTextAsync(bookRootPath, token);
                if (string.IsNullOrWhiteSpace(jsonContent)) return;

                var docObj = JObject.Parse(jsonContent);
                var bodyContent = docObj["body"]?["content"] as JArray;
                if (bodyContent == null) return;

                int currentAbsoluteIndex = 0;
                string docIdentifier = Path.GetFileNameWithoutExtension(bookRootPath);
                string imagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache", "images", docIdentifier);

                // Вызываем внутреннее ядро разбора элементов контента
                ProcessGoogleBodyContent(bodyContent, string.Empty, selectedPathsSet, sb, ref currentAbsoluteIndex, token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Google OOP Context Text Error] {ex.Message}");
            }
        }, token);

        return sb;
    }
    /// <summary>
    /// Линейный разбор элементов JSON: собирает иерархию глав и фильтрует контент по всей вертикали родителей.
    /// </summary>
    private void ProcessGoogleBodyContent(JArray contentArray, string tabPath, HashSet<string> selectedPathsSet, StringBuilder sb, ref int currentAbsoluteIndex, CancellationToken token)
    {
        if (contentArray == null) return;

        string currentChapterPath = tabPath;
        var activePaths = new Dictionary<int, string> { { 0, tabPath } };

        foreach (var element in contentArray)
        {
            token.ThrowIfCancellationRequested();

            var paragraph = element["paragraph"];
            if (paragraph != null)
            {
                string namedStyle = paragraph["paragraphStyle"]?["namedStyleType"]?.ToString() ?? string.Empty;
                int elementLength = 0;

                if (paragraph["elements"] is JArray elements)
                {
                    foreach (var el in elements)
                    {
                        elementLength += (el["textRun"]?["content"]?.ToString() ?? string.Empty).Length;
                    }

                    if (namedStyle.StartsWith("HEADING_") && int.TryParse(namedStyle.Substring(8), out int level))
                    {
                        string headingText = "";
                        foreach (var el in elements) headingText += el["textRun"]?["content"]?.ToString() ?? string.Empty;
                        headingText = headingText.Trim();

                        if (!string.IsNullOrEmpty(headingText))
                        {
                            int targetParentLevel = level - 1;
                            while (targetParentLevel > 0 && !activePaths.ContainsKey(targetParentLevel)) targetParentLevel--;

                            string parentPath = activePaths[targetParentLevel];
                            currentChapterPath = string.IsNullOrEmpty(parentPath) ? headingText : $"{parentPath}/{headingText}";

                            activePaths[level] = currentChapterPath;
                            foreach (var key in activePaths.Keys.Where(k => k > level).ToList()) activePaths.Remove(key);

                            // Контроль вертикального каскада: заголовок добавляется только если цепочка валидна!
                            if (IsPathChainValid(currentChapterPath, selectedPathsSet))
                            {
                                string elementId = "h_" + unchecked((uint)currentChapterPath.GetHashCode()).ToString("x8");
                                sb.AppendLine($"<heading id=\"{elementId}\" path=\"{currentChapterPath}\">");
                                sb.AppendLine($"  {headingText}");
                            }
                        }
                    }
                    else
                    {
                        string pText = "";
                        foreach (var el in elements) pText += el["textRun"]?["content"]?.ToString() ?? string.Empty;

                        if (!string.IsNullOrEmpty(currentChapterPath) && IsPathChainValid(currentChapterPath, selectedPathsSet) && !string.IsNullOrWhiteSpace(pText))
                        {
                            sb.AppendLine($"  {pText.Replace("\r", "").Replace("\n", "").Trim()}");
                        }
                    }
                }
                currentAbsoluteIndex += elementLength;
            }
            else if (element is JObject tableObj && tableObj["table"] != null)
            {
                if (!string.IsNullOrEmpty(currentChapterPath) && IsPathChainValid(currentChapterPath, selectedPathsSet))
                {
                    sb.AppendLine("  <table>");
                    var rows = tableObj["table"]?["tableRows"] as JArray;
                    if (rows != null)
                    {
                        foreach (var row in rows)
                        {
                            sb.AppendLine("    <tr>");
                            var cells = row["tableCells"] as JArray;
                            if (cells != null)
                            {
                                foreach (var cell in cells)
                                {
                                    var cellContent = cell["content"] as JArray;

                                    // ИСПРАВЛЕНО CS8604: Жесткая проверка ссылки на null полностью блокирует предупреждение Студии!
                                    if (cellContent != null)
                                    {
                                        StringBuilder cellTextSb = new StringBuilder();
                                        AppendContentTextRecursive(cellContent, cellTextSb);
                                        string cellText = cellTextSb.ToString().Replace("\r", "").Replace("\n", "").Trim();
                                        sb.AppendLine($"      <td>{cellText}</td>");
                                    }
                                }
                            }
                            sb.AppendLine("    </tr>");
                        }
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

    private static void AppendContentTextRecursive(JArray contentArray, StringBuilder sb)
    {
        if (contentArray == null) return;
        foreach (var element in contentArray)
        {
            if (element["paragraph"]?["elements"] is JArray elements)
            {
                foreach (var el in elements)
                {
                    sb.Append(el["textRun"]?["content"]?.ToString() ?? string.Empty);
                }
            }
        }
    }

    /// <summary>
    /// Шаблонная реализация генерации издательского PDF из Google Docs кэша.
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

        string bookRootPath = checkedFiles?.FirstOrDefault()?.FullPath ?? string.Empty;
        string docIdentifier = !string.IsNullOrEmpty(bookRootPath) ? Path.GetFileNameWithoutExtension(bookRootPath) : "default";
        string imagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache", "images", docIdentifier);

        // Временная заглушка рендеринга строк, полную логику сборки ProcessPdfLinesAndSaveAsync вызовем из сервиса
        SavePdfToDiskAndOpen(document, outputPath, fileName);
    }

    /// <summary>
    /// Контракт на будущую сборку DocX.
    /// </summary>
    public override Task GenerateDocxAsync(string outputPath, string fileName, List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        return Task.CompletedTask;
    }
} // Финальное закрытие класса!
