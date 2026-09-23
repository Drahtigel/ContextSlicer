using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ContextSlicer.Filesystem;

namespace ContextSlicer;

// Структура для отправки отчетов о прогрессе в UI
public struct ProgressReport
{
    public int CurrentIndex { get; set; }
    public int TotalCount { get; set; }
    public string CurrentFileName { get; set; }
}

public static class ContextBuilderService
{
    // Динамическая проверка папок по глобальному списку исключений
    private static bool IsFolderExcluded(string folderName)
    {
        if (GlobalFilterService.Current?.ExcludedFolders == null) return false;
        foreach (var f in GlobalFilterService.Current.ExcludedFolders)
        {
            if (folderName.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Динамическая проверка расширений файлов по глобальному списку исключений
    private static bool IsExtensionExcluded(string filePath)
    {
        if (GlobalFilterService.Current?.ExcludedExtensions == null) return false;
        string ext = Path.GetExtension(filePath);
        foreach (var e in GlobalFilterService.Current.ExcludedExtensions)
        {
            if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Метод рекурсивного сканирования дерева
    public static FileSystemNode BuildTree(string rootPath, List<string> savedCheckedFiles)
    {
        var rootInfo = new DirectoryInfo(rootPath);
        var rootNode = new FileSystemNode { Name = rootInfo.Name, FullPath = rootInfo.FullName, RelativePath = "" };
        FillNodesRecursive(rootInfo, rootNode, rootPath, savedCheckedFiles);
        return rootNode;
    }

    private static void FillNodesRecursive(DirectoryInfo dir, FileSystemNode parentNode, string rootPath, List<string> savedCheckedFiles)
    {
        try
        {
            foreach (var subDir in dir.GetDirectories())
            {
                if (IsFolderExcluded(subDir.Name)) continue;

                var childNode = new FileSystemNode { Name = subDir.Name, FullPath = subDir.FullName, RelativePath = Path.GetRelativePath(rootPath, subDir.FullName), IsFile = false, Parent = parentNode };
                parentNode.Children.Add(childNode);
                FillNodesRecursive(subDir, childNode, rootPath, savedCheckedFiles);
            }
            foreach (var file in dir.GetFiles())
            {
                if (IsExtensionExcluded(file.FullName)) continue;
                var relPath = Path.GetRelativePath(rootPath, file.FullName);

                var childNode = new FileSystemNode
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    RelativePath = relPath,
                    IsFile = true,
                    Parent = parentNode
                };

                if (savedCheckedFiles.Contains(relPath)) childNode.IsChecked = true;

                // НОВОЕ: Если расширение файла поддерживается синтаксическими парсерами,
                // добавляем фиктивную ноду-заглушку, чтобы WPF отобразил стрелочку раскрытия узла [▶]
                string ext = Path.GetExtension(file.FullName);
                if (SyntaxParserFactory.IsSupported(ext))
                {
                    childNode.Children.Add(new FileSystemNode { Name = "LoadingStub...", Parent = childNode });
                }

                parentNode.Children.Add(childNode);
            }

        }
        catch (UnauthorizedAccessException) { }
    }

    public static void GetCheckedFiles(FileSystemNode node, List<FileSystemNode> result)
    {
        // ЖЕСТКАЯ ЗАЩИТА: Если корень дерева или текущий узел аннулирован, прерываем рекурсию
        if (node == null || result == null) return;

        if (node.IsFile && node.IsChecked == true)
        {
            if (!IsExtensionExcluded(node.FullPath))
            {
                if (!result.Contains(node)) result.Add(node);
            }
        }

        // Безопасный обход дочерних элементов через локальную копию
        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
        {
            if (child != null)
            {
                GetCheckedFiles(child, result);
            }
        }
    }

    public static void GetCheckedFilesExtended(FileSystemNode node, List<FileSystemNode> result)
    {
        // ЖЕСТКАЯ ЗАЩИТА: Ликвидация System.NullReferenceException при закрытии/смене проекта
        if (node == null || result == null) return;

        if (node.IsFile && (node.IsChecked == true || node.IsChecked == null))
        {
            if (!IsExtensionExcluded(node.FullPath))
            {
                if (!result.Contains(node)) result.Add(node);
            }
        }

        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
        {
            if (child != null)
            {
                GetCheckedFilesExtended(child, result);
            }
        }
    }


    // ================================================================= -->
    // ЭТАЛОННЫЙ СБОРЩИК ЛИТЕРАТУРНОГО КОНТЕКСТА GOOGLE DOCS (XML-ТЕГИ)   -->
    // ================================================================= -->
    public static async Task<StringBuilder> BuildTextContentAsync(
        string projectRules,
        string moduleRules,
        bool includeDirectoryStructure,
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        IProgress<ProgressReport> progress,
        CancellationToken token)
    {
        var sb = new StringBuilder();

        // 1. Выгрузка системных правил и промптов
        if (!string.IsNullOrWhiteSpace(projectRules) || !string.IsNullOrWhiteSpace(moduleRules))
        {
            sb.AppendLine("=== ПРАВИЛА ОБРАЩЕНИЯ С ТЕКСТОМ И КОДОМ ===");
            if (!string.IsNullOrWhiteSpace(projectRules))
            {
                sb.AppendLine("<project_rules>");
                sb.AppendLine(projectRules.Trim());
                sb.AppendLine("</project_rules>\n");
            }
            if (!string.IsNullOrWhiteSpace(moduleRules))
            {
                sb.AppendLine("<module_rules>");
                sb.AppendLine(moduleRules.Trim());
                sb.AppendLine("</module_rules>\n");
            }
        }

        // ================================================================= -->
        // ЧАСТЬ 1: СЕМАНТИЧЕСКИЕ XML-ТЕГИ ДЛЯ СТРУКТУРЫ ОГЛАВЛЕНИЯ КНИГИ    -->
        // ================================================================= -->
        // 2. Безопасный вывод структуры оглавления книги, обернутый в XML для ИИ
        if (includeDirectoryStructure && savedEntries != null && savedEntries.Count > 0)
        {
            sb.AppendLine("=== СТРУКТУРА ВЫБРАННОГО ЛИТЕРАТУРНОГО КОНТЕКСТА ===");
            sb.AppendLine("<document_structure>"); // Открывающий тег для ИИ

            var uniqueTabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in savedEntries)
            {
                int slashIdx = entry.EntryPath.IndexOf('/');
                if (slashIdx > 0)
                {
                    string tabName = entry.EntryPath.Substring(0, slashIdx);
                    if (uniqueTabs.Add(tabName)) sb.AppendLine($"📁 [Вкладка] {tabName}");
                    sb.AppendLine($"    ├─ 📄 {entry.DisplayName}");
                }
                else
                {
                    sb.AppendLine($"├─ 📄 {entry.DisplayName}");
                }
            }

            sb.AppendLine("</document_structure>\n"); // Закрывающий тег для ИИ
        }


        sb.AppendLine("=== СОДЕРЖИМОЕ ВЫБРАННЫХ РАЗДЕЛОВ ===");

        if (savedEntries == null || savedEntries.Count == 0) return sb;

        // Группируем выбранные главы по файлам (на случай если проектов несколько)
        var entriesByFile = savedEntries.GroupBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase);

        foreach (var fileGroup in entriesByFile)
        {
            token.ThrowIfCancellationRequested();

            // Восстанавливаем физический путь к файлу кэша JSON в нашей папке googlecache
            string googleCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
            string docId = Path.GetFileNameWithoutExtension(fileGroup.Key);
            // Если FilePath был пустой (наша оптимизация), берем ID из настроек или имени
            if (string.IsNullOrEmpty(docId) && checkedFiles.Count > 0) docId = Path.GetFileNameWithoutExtension(checkedFiles[0].FullPath);

            string fullCachePath = Path.Combine(googleCacheDir, $"{docId}.json");
            if (!File.Exists(fullCachePath)) continue;

            // Загружаем объект JSON документа
            string jsonContent = await File.ReadAllTextAsync(fullCachePath, Encoding.UTF8, token);
            var docJson = Newtonsoft.Json.Linq.JObject.Parse(jsonContent);

            var totalEntries = fileGroup.Count();
            var entriesList = fileGroup.ToList();

            // 3. БРИТВЕННО-ЧИСТЫЙ СБОР ХУДОЖЕСТВЕННОЙ ПРОЗЫ ПО ИМЕНАМ ГЛАВ
            for (int i = 0; i < totalEntries; i++)
            {
                var entry = entriesList[i];
                token.ThrowIfCancellationRequested();

                progress?.Report(new ProgressReport
                {
                    CurrentIndex = i + 1,
                    TotalCount = totalEntries,
                    CurrentFileName = entry.DisplayName
                });

                // Находим нужную вкладку и извлекаем текст конкретной главы
                string pureChapterText = ExtractChapterTextFromGoogleJson(docJson, entry.EntryPath, entry.DisplayName);

                if (!string.IsNullOrWhiteSpace(pureChapterText))
                {
                    // Заворачиваем в идеальные семантические XML-теги, которые обожает ИИ!
                    sb.AppendLine($"<heading path=\"{entry.EntryPath}\">");
                    sb.AppendLine(pureChapterText.Trim());
                    sb.AppendLine($"</heading>\n");
                }
            }
        }

        return sb;
    }

    /// <summary>
    /// Извлекает бритвенно-чистый текст параграфов, идущих строго внутри выбранной главы
    /// </summary>
    private static string ExtractChapterTextFromGoogleJson(Newtonsoft.Json.Linq.JObject docJson, string entryPath, string chapterName)
    {
        var sb = new StringBuilder();

        // Ищем имя вкладки (все что до первого слэша)
        string targetTabTitle = entryPath.Contains('/') ? entryPath.Substring(0, entryPath.IndexOf('/')) : string.Empty;

        if (docJson["tabs"] is Newtonsoft.Json.Linq.JArray tabsArray)
        {
            foreach (var tab in tabsArray)
            {
                string tabTitle = tab["tabProperties"]?["title"]?.ToString() ?? "";
                // Если документ с вкладками — ищем строго нашу целевую вкладку
                if (!string.IsNullOrEmpty(targetTabTitle) && !tabTitle.Equals(targetTabTitle, StringComparison.OrdinalIgnoreCase)) continue;

                var content = tab["documentTab"]?["body"]?["content"] as Newtonsoft.Json.Linq.JArray;
                if (content != null) CollectTextForHeading(content, chapterName, sb);
            }
        }
        else if (docJson["body"]?["content"] is Newtonsoft.Json.Linq.JArray content)
        {
            // Плоский документ без вкладок
            CollectTextForHeading(content, chapterName, sb);
        }

        return sb.ToString();
    }

    private static void CollectTextForHeading(Newtonsoft.Json.Linq.JArray contentArray, string chapterName, StringBuilder sb)
    {
        bool insideTargetHeading = false;

        foreach (var element in contentArray)
        {
            var paragraph = element["paragraph"];
            if (paragraph == null) continue;

            string namedStyle = paragraph["paragraphStyle"]?["namedStyleType"]?.ToString() ?? string.Empty;

            // Собираем текст текущего параграфа из его элементов textRun
            var textBuilder = new StringBuilder();
            if (paragraph["elements"] is Newtonsoft.Json.Linq.JArray elements)
            {
                foreach (var el in elements)
                {
                    textBuilder.Append(el["textRun"]?["content"]?.ToString() ?? string.Empty);
                }
            }
            string currentText = textBuilder.ToString();

            // Если встретили ЛЮБОЙ заголовок HEADING_
            if (namedStyle.StartsWith("HEADING_"))
            {
                if (insideTargetHeading)
                {
                    // Если мы уже были внутри нашей главы и наткнулись на СЛЕДУЮЩИЙ заголовок — сбор окончен!
                    break;
                }

                // Проверяем, совпадает ли этот заголовок с искомой главой
                if (currentText.Trim().Equals(chapterName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    insideTargetHeading = true;
                    // Записываем сам заголовок главы в начало
                    sb.AppendLine(currentText.Trim());
                    continue;
                }
            }

            // Если мы находимся внутри границ выбранной главы — бережно собираем её чистую прозу абзацев!
            if (insideTargetHeading)
            {
                sb.Append(currentText);
            }
        }
    }

    // Вспомогательный метод для рекурсивного сбора чекнутых синтаксических нод внутри файла
    private static void FindCheckedSyntaxNodes(FileSystemNode node, List<FileSystemNode> result)
    {
        if (node.IsSyntaxNode && node.IsChecked == true)
        {
            result.Add(node);
        }
        foreach (var child in node.Children)
        {
            FindCheckedSyntaxNodes(child, result);
        }
    }


    // ================================================================= -->
    // ЧАСТЬ 2: ПОСТРАНИЧНЫЙ PDF-РЕНДЕРЕР С УЧЕТОМ КАСКАДА СТРУКТУРЫ    -->
    // ================================================================= -->
    // ================================================================= -->
    // ЧАСТЬ 2: ИСПРАВЛЕННЫЙ ПОСТРАНИЧНЫЙ РЕНДЕРЕР ЧИСТОГО ТЕКСТА PDF   -->
    // ================================================================= -->
    public static async Task GeneratePdfContextFileAsync(
        string outputPath,
        string fileName,
        string projectRules,
        string moduleRules,
        bool includeDirectoryStructure,
        FileSystemNode rootNode,
        List<SyntaxEntry> savedEntries,
        IProgress<ProgressReport> progress,
        CancellationToken token)
    {
        var checkedFiles = new List<FileSystemNode>();
        GetCheckedFilesExtended(rootNode, checkedFiles);

        // 1. Собираем чистый текстовый каркас (правила, промпты, оглавление в XML)
        var sb = await BuildTextContentAsync(projectRules, moduleRules, includeDirectoryStructure, checkedFiles, savedEntries, progress, token);
        string metaDataText = sb.ToString();
        token.ThrowIfCancellationRequested();

        // 2. Инициализируем документ MigraDoc
        var document = new MigraDoc.DocumentObjectModel.Document();
        var style = document.Styles["Normal"];
        if (style?.Font != null)
        {
            style.Font.Name = "Courier New";
            style.Font.Size = 10; // Комфортный книжный размер шрифта
        }

        var currentSection = document.AddSection();
        SetSectionMargins(currentSection);

        // Выводим системную метаинформацию (правила, промпты, XML-оглавление)
        using (var reader = new StringReader(metaDataText))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                token.ThrowIfCancellationRequested();
                if (line.StartsWith("=== СОДЕРЖИМОЕ ВЫБРАННЫХ РАЗДЕЛОВ ===")) break;

                var p = currentSection.AddParagraph(line);
                if (string.IsNullOrWhiteSpace(line)) p.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(4);
            }
        }

        if (savedEntries == null || savedEntries.Count == 0) return;

        // Группируем элементы по файлам кэша
        var entriesByFile = savedEntries.GroupBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase);

        foreach (var fileGroup in entriesByFile)
        {
            token.ThrowIfCancellationRequested();

            string googleCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
            string docId = Path.GetFileNameWithoutExtension(fileGroup.Key);
            if (string.IsNullOrEmpty(docId) && checkedFiles.Count > 0) docId = Path.GetFileNameWithoutExtension(checkedFiles[0].FullPath);

            string fullCachePath = Path.Combine(googleCacheDir, $"{docId}.json");
            if (!File.Exists(fullCachePath)) continue;

            // Загружаем JSON документа один раз для всей группы глав
            string jsonContent = await File.ReadAllTextAsync(fullCachePath, Encoding.UTF8, token);
            var docJson = Newtonsoft.Json.Linq.JObject.Parse(jsonContent);

            var entriesList = fileGroup.ToList();
            for (int i = 0; i < entriesList.Count; i++)
            {
                var entry = entriesList[i];
                token.ThrowIfCancellationRequested();

                if (entry.Type != EntryType.Heading && entry.Type != EntryType.Section) continue;

                // ИСПРАВЛЕНО: Вызываем метод интеллектуального поглавного чтения текста из JSON!
                string pureChapterText = ExtractChapterTextFromGoogleJson(docJson, entry.EntryPath, entry.DisplayName);

                if (!string.IsNullOrWhiteSpace(pureChapterText))
                {
                    // БИТЬЕ НА СТРАНИЦЫ: Каждая новая выбранная глава романа начинается с нового листа!
                    currentSection = document.AddSection();
                    SetSectionMargins(currentSection);

                    // Открывающий тег-маркер для ИИ (темно-синий жирный шрифт)
                    var markerP = currentSection.AddParagraph($"<heading path=\"{entry.EntryPath}\">");
                    markerP.Format.Font.Bold = true;
                    markerP.Format.Font.Color = MigraDoc.DocumentObjectModel.Colors.DarkBlue;
                    markerP.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromPoint(12);

                    // Накатываем чистый художественный текст прозы на страницу PDF
                    AppendTextToSection(pureChapterText.Trim(), currentSection);

                    // Закрывающий тег для ИИ в самом низу раздела главы
                    var closeP = currentSection.AddParagraph($"</heading>");
                    closeP.Format.Font.Bold = true;
                    closeP.Format.Font.Color = MigraDoc.DocumentObjectModel.Colors.DarkBlue;
                    closeP.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(10);
                }
            }
        }

        // Сохранение готового PDF на диск (ваша стандартная логика защиты процессов)
        string fullOutputPath = Path.Combine(outputPath, fileName.EndsWith(".pdf") ? fileName : fileName + ".pdf");
        if (File.Exists(fullOutputPath))
        {
            try { File.Delete(fullOutputPath); }
            catch (IOException) { return; }
        }

        await Task.Run(() =>
        {
            var renderer = new MigraDoc.Rendering.PdfDocumentRenderer();
            renderer.Document = document;
            renderer.RenderDocument();
            renderer.PdfDocument.Save(fullOutputPath);
        }, token);
    }

    // Вспомогательный метод выставления компактных литературных отступов полей А4
    private static void SetSectionMargins(MigraDoc.DocumentObjectModel.Section sec)
    {
        sec.PageSetup.TopMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.5);
        sec.PageSetup.BottomMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.5);
        sec.PageSetup.LeftMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.8);
        sec.PageSetup.RightMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.5);
    }

    // Вспомогательный построчный перенос абзацев прозы внутрь PDF-секции
    private static void AppendTextToSection(string text, MigraDoc.DocumentObjectModel.Section sec)
    {
        using (var reader = new StringReader(text))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var paragraph = sec.AddParagraph(line);
                // Задаем красивый абзацный отступ для строк художественного текста рассказов
                if (string.IsNullOrWhiteSpace(line))
                {
                    paragraph.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(6);
                }
            }
        }
    }


    // Асинхронная генерация обычного TXT с поддержкой синтаксических записей
    public static async Task GenerateContextFileAsync(
        string outputPath,
        string fileName,
        string projectRules,
        string moduleRules,
        bool includeDirectoryStructure,
        FileSystemNode rootNode,
        List<SyntaxEntry> savedEntries, // ИСПРАВЛЕНО: Добавлен параметр в сигнатуру метода
        IProgress<ProgressReport> progress,
        CancellationToken token)
    {
        var checkedFiles = new List<FileSystemNode>();
        GetCheckedFilesExtended(rootNode, checkedFiles); // Используем расширенный сбор

        // ИСПРАВЛЕНО: Передаем savedEntries, полученный из параметров метода
        var sb = await BuildTextContentAsync(projectRules, moduleRules, includeDirectoryStructure,
            checkedFiles, savedEntries, progress, token);

        string fullOutputPath = Path.Combine(outputPath, fileName.EndsWith(".txt") ? fileName : fileName + ".txt");
        await File.WriteAllTextAsync(fullOutputPath, sb.ToString(), Encoding.UTF8, token);
    }

}
