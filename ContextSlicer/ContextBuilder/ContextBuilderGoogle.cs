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

public class ContextBuilderGoogle : ContextBuilderBase
{
    public ContextBuilderGoogle(string promptRules, string moduleRules, bool includeDirectoryStructure, bool includeImages)
        : base(promptRules, moduleRules, includeDirectoryStructure, includeImages) { }

    // ================================================================= -->
    // ИСПРАВЛЕНО: КЛАССИЧЕСКИЙ СИНХРОННЫЙ КОНВЕЙЕР РАЗБОРА GOOGLE DOCS -->
    // ================================================================= -->
    public override async Task<StringBuilder> BuildTextContentAsync(
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        var sb = new StringBuilder();
        if (savedEntries == null || savedEntries.Count == 0) return sb;

        string bookRootPath = string.Empty;
        string googleCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");

        // 1. Попытка взять путь из FullPath переданной ноды корня
        if (checkedFiles != null && checkedFiles.Count > 0)
        {
            var firstFile = checkedFiles.FirstOrDefault(f => f != null && !string.IsNullOrEmpty(f.FullPath) && File.Exists(f.FullPath));
            if (firstFile != null) bookRootPath = firstFile.FullPath;
        }

        // 2. Сканируем директорию googlecache
        if (string.IsNullOrEmpty(bookRootPath) || !File.Exists(bookRootPath))
        {
            if (Directory.Exists(googleCacheDir))
            {
                var cacheFiles = Directory.GetFiles(googleCacheDir, "*.json");
                var latestCacheFile = cacheFiles
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();

                if (latestCacheFile != null)
                {
                    bookRootPath = latestCacheFile.FullName;
                }
            }
        }

        // 3. Последний фолбэк по FilePath записи, если папка была пуста
        if (string.IsNullOrEmpty(bookRootPath) || !File.Exists(bookRootPath))
        {
            var firstEntry = savedEntries.FirstOrDefault(e => e != null && !string.IsNullOrEmpty(e.FilePath));
            if (firstEntry != null && !string.IsNullOrEmpty(firstEntry.FilePath))
            {
                string rawName = Path.GetFileName(firstEntry.FilePath);
                bookRootPath = Path.Combine(googleCacheDir, rawName);
            }
        }

        // Жесткий гвардейский выход, если файла физически нет в системе
        if (string.IsNullOrEmpty(bookRootPath) || !File.Exists(bookRootPath))
        {
            System.Diagnostics.Debug.WriteLine($"[Google Parser Fatal] Файл кэша не найден в системе по пути: '{bookRootPath}'");
            return sb;
        }

        // Выгружаем системные правила промпта из абстрактной базы
        AppendSystemRules(sb);

        var selectedPathsSet = new HashSet<string>(
            savedEntries.Where(e => e != null).Select(e => e.EntryPath),
            StringComparer.OrdinalIgnoreCase);

        // Генерация XML-структуры оглавления
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

        // ================================================================= -->
        // РАЗВЕРНУТО В СИНХРОННЫЙ КЛАССИЧЕСКИЙ ВАРИАНТ ДЛЯ ДЕБАГГЕРА        -->
        // ================================================================= -->
        // ================================================================= -->
        // ИСПРАВЛЕНО: ЧЕСТНЫЙ МНОГОВКЛАДОЧНЫЙ ОБХОД СТРУКТУРЫ GOOGLE TABS API -->
        // ================================================================= -->
        try
        {
            string jsonContent = File.ReadAllText(bookRootPath);
            if (string.IsNullOrWhiteSpace(jsonContent)) return sb;

            var docObj = JObject.Parse(jsonContent);
            int currentAbsoluteIndex = 0;

            // Извлекаем имя базового корня для сборщика путей
            var sampleEntry = savedEntries.FirstOrDefault(e => e != null && !string.IsNullOrEmpty(e.EntryPath));
            string initialRootName = string.Empty;
            if (sampleEntry != null && sampleEntry.EntryPath.Contains('/'))
            {
                initialRootName = sampleEntry.EntryPath.Split('/').FirstOrDefault() ?? string.Empty;
            }
            if (string.IsNullOrEmpty(initialRootName))
            {
                initialRootName = Path.GetFileNameWithoutExtension(bookRootPath);
            }

            // ПРОВЕРКА СТРУКТУРЫ: Если это новый формат документов с вкладками "tabs"
            if (docObj["tabs"] is JArray tabsArray)
            {
                foreach (var tab in tabsArray)
                {
                    if (tab == null) continue;

                    // Извлекаем массив контента текущей вкладки по цепочке: tabs[i] -> documentTab -> body -> content
                    var bodyContent = tab["documentTab"]?["body"]?["content"] as JArray;
                    if (bodyContent == null) continue;

                    // Вызываем наш очищенный линейный автомат для обработки параграфов этой вкладки
                    ProcessGoogleBodyContent(bodyContent, initialRootName, selectedPathsSet, sb, ref currentAbsoluteIndex, token);
                }
            }
            else
            {
                // СТАРЫЙ ФОЛБЭК: Если файл сохранен в классическом плоском формате без вкладок
                JArray? bodyContent = null;
                if (docObj["body"]?["content"] is JArray bodyArr) bodyContent = bodyArr;
                else if (docObj["content"] is JArray contentArr) bodyContent = contentArr;

                if (bodyContent != null)
                {
                    ProcessGoogleBodyContent(bodyContent, initialRootName, selectedPathsSet, sb, ref currentAbsoluteIndex, token);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[Google Parser Error] Массив контента документа не найден ни в одном из форматов!");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Google OOP Context Text Error] {ex.Message}");
        }
        // 2. Добавляем холостой await, чтобы НАВЕЧНО погасить предупреждение CS1998!
        await Task.CompletedTask;

        // 3. Возвращаем чистый sb — async/await сам упакует его в Task<StringBuilder> 
        // и полностью уничтожит ошибку компиляции CS0029!
        return sb;
    }
    // ================================================================= -->
    // ИСПРАВЛЕНО: СТРОГАЯ ИДЕНТИФИКАЦИЯ ЗАКРЫВАЮЩИХ ТЕГОВ HEADING ID    -->
    // ================================================================= -->
    private void ProcessGoogleBodyContent(JArray contentArray, string tabPath, HashSet<string> selectedPathsSet, StringBuilder sb, ref int currentAbsoluteIndex, CancellationToken token)
    {
        if (contentArray == null) return;

        var approvedHeadingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in selectedPathsSet)
        {
            if (string.IsNullOrEmpty(path)) continue;
            string pureName = path.Split('/').LastOrDefault()?.Trim().TrimEnd('.') ?? string.Empty;
            if (!string.IsNullOrEmpty(pureName))
            {
                approvedHeadingNames.Add(pureName);
                entryMap[pureName] = path;
            }
        }

        bool isCurrentParagraphApproved = false;
        string activeChapterName = string.Empty;

        // ДОБАВЛЕНО: Хранилище текущего активного ID для закрывающих тегов
        string activeElementId = string.Empty;

        foreach (var element in contentArray)
        {
            token.ThrowIfCancellationRequested();
            if (element == null) continue;

            var paragraph = element["paragraph"] ??
                            (element["elements"] != null || element["paragraphStyle"] != null ? element : null);

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

                    if (namedStyle.StartsWith("HEADING_"))
                    {
                        string headingText = "";
                        foreach (var el in elements) headingText += el["textRun"]?["content"]?.ToString() ?? string.Empty;

                        headingText = headingText.Trim();
                        string cleanHeadingName = headingText.TrimEnd('.');

                        if (!string.IsNullOrEmpty(cleanHeadingName))
                        {
                            // ИСПРАВЛЕНО: Если предыдущая глава закрывается — подставляем её честный ID!
                            if (isCurrentParagraphApproved && !string.IsNullOrEmpty(activeElementId))
                            {
                                sb.AppendLine($"</heading id=\"{activeElementId}\">");
                            }

                            if (approvedHeadingNames.Contains(cleanHeadingName))
                            {
                                isCurrentParagraphApproved = true;
                                activeChapterName = cleanHeadingName;

                                entryMap.TryGetValue(cleanHeadingName, out string? fullPath);
                                string finalPath = fullPath ?? cleanHeadingName;

                                // Вычисляем уникальный хэш-идентификатор главы
                                activeElementId = "h_" + unchecked((uint)finalPath.GetHashCode()).ToString("x8");

                                sb.AppendLine($"<heading id=\"{activeElementId}\" path=\"{finalPath}\">");
                                sb.AppendLine($"  {headingText}");
                            }
                            else
                            {
                                isCurrentParagraphApproved = false;
                                activeChapterName = string.Empty;
                                activeElementId = string.Empty;
                            }
                        }
                    }
                    else
                    {
                        string pText = "";
                        foreach (var el in elements) pText += el["textRun"]?["content"]?.ToString() ?? string.Empty;

                        if (isCurrentParagraphApproved && !string.IsNullOrWhiteSpace(pText))
                        {
                            sb.AppendLine($"  {pText.Replace("\r", "").Replace("\n", "").Trim()}");
                        }
                    }
                }
                currentAbsoluteIndex += elementLength;
            }
            else if (element is JObject tableObj && tableObj["table"] != null)
            {
                if (isCurrentParagraphApproved)
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

        // ИСПРАВЛЕНО: Закрываем самую последнюю главу в документе с её честным ID!
        if (isCurrentParagraphApproved && !string.IsNullOrEmpty(activeElementId))
        {
            sb.AppendLine($"</heading id=\"{activeElementId}\">");
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

    // ================================================================= -->
    // ИСПРАВЛЕНО: ПОЛНОЦЕННЫЙ НАЛИВ СТРОК В PDF ДЛЯ GOOGLE DOCS         -->
    // ================================================================= -->
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

        // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Наливаем строки в параграфы MigraDoc вместо создания пустого файла!
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

    public override Task GenerateDocxAsync(string outputPath, string fileName, List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
