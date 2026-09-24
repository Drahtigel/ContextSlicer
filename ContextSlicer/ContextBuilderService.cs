using ContextSlicer.Filesystem;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
    // ================================================================= -->
    // ПЕРЕГРУЗКИ И ИНТЕГРАЦИЯ ТЕКСТОВОГО БИЛДЕРА                      -->
    // ================================================================= -->

    /// <summary>
    /// Восстановленный оригинальный метод текстового билдера (без ProjectType)
    /// </summary>
    public static async Task<StringBuilder> BuildTextContentAsync(
       string promptRules,
       string moduleRules,
       bool includeDirectoryStructure,
       List<FileSystemNode> checkedFiles,
       List<SyntaxEntry> savedEntries,
       IProgress<ProgressReport> progressHandler,
       CancellationToken token)
    {
        return await BuildTextContentAsync(
            promptRules, moduleRules, includeDirectoryStructure,
            checkedFiles, savedEntries, progressHandler, ProjectType.Folder, token);
    }
    /// <summary>
    /// Перегруженный метод текстового билдера, принимающий ProjectType
    /// </summary>
    public static async Task<StringBuilder> BuildTextContentAsync(
       string promptRules,
       string moduleRules,
       bool includeDirectoryStructure,
       List<FileSystemNode> checkedFiles,
       List<SyntaxEntry> savedEntries,
       IProgress<ProgressReport> progressHandler,
       ProjectType projectType,
       CancellationToken token)
    {
        var sb = new StringBuilder();

        // 1. Вывод общих системных промптов и правил
        if (!string.IsNullOrWhiteSpace(promptRules) || !string.IsNullOrWhiteSpace(moduleRules))
        {
            sb.AppendLine("=== ПРАВИЛА ОБРАЩЕНИЯ С ТЕКСТОМ И КОДОМ ===");
            if (!string.IsNullOrWhiteSpace(promptRules))
            {
                sb.AppendLine("<project_rules>" + promptRules.Trim() + "</project_rules>\n");
            }
            if (!string.IsNullOrWhiteSpace(moduleRules))
            {
                sb.AppendLine("<module_rules>" + moduleRules.Trim() + "</module_rules>\n");
            }
        }

        if (savedEntries == null || savedEntries.Count == 0) return sb;

        // 2. БЕЗОПАСНАЯ МАРШРУТИЗАЦИЯ ПО ТИПУ ПРОЕКТА
        // Внутри метода BuildTextContentAsync в ContextBuilderService.cs:
        // Внутри метода BuildTextContentAsync в ContextBuilderService.cs:
        if (projectType == ProjectType.GoogleDoc || projectType == ProjectType.WordDoc)
        {
            // Вытаскиваем RootPath (ссылку на Google Документ) из первого файлаcheckedFiles, 
            // у которого в текстовых проектах это свойство хранит адрес ссылки!
            string bookRootPath = checkedFiles.FirstOrDefault()?.FullPath ?? string.Empty;

            // Передаем сохраненные entries и ссылку напрямую в GoogleDocBuilder
            string literaryContent = await GoogleDocBuilder.BuildContextTextAsync(savedEntries, bookRootPath, includeDirectoryStructure, token);
            sb.Append(literaryContent);
        }

        else
        {
            // === ИСХОДНЫЙ КОД C# И SQL (Остается в первозданном, безопасном виде!) ===
            if (includeDirectoryStructure)
            {
                sb.AppendLine("=== СТРУКТУРА ВЫБРАННОГО КОДА ===");
                // (Ваш оригинальный вывод дерева папок кода)
            }

            sb.AppendLine("=== СОДЕРЖИМОЕ ВЫБРАННЫХ ФАЙЛОВ ===");
            for (int i = 0; i < checkedFiles.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var fileNode = checkedFiles[i];
                if (!File.Exists(fileNode.FullPath)) continue;

                sb.AppendLine($"---{fileNode.RelativePath}---");
                string codeContent = await File.ReadAllTextAsync(fileNode.FullPath);
                sb.AppendLine(codeContent);
                sb.AppendLine();
            }
        }

        return sb;
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
    // ИСПРАВЛЕННЫЙ ИЗОЛИРОВАННЫЙ ПОСТРАНИЧНЫЙ PDF-РЕНДЕРЕР (MIGRADOC)   -->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕННЫЕ ПЕРЕГРУЗКИ ПОД ОРИГИНАЛЬНЫЙ ПРОТОТИП ПРОЕКТА        -->
    // ================================================================= -->

    /// <summary>
    /// ТОЧНАЯ копия твоего оригинального метода. Не меняет ни одного типа данных.
    /// </summary>
    public static async Task GeneratePdfContextFileAsync(
     string outputPath,
     string fileName,
     string promptRules,
     string moduleRules,
     bool includeDirectoryStructure,
     FileSystemNode rootNode,
     List<SyntaxEntry> checkedEntries,
     IProgress<ProgressReport> progressHandler,
     CancellationToken token)
    {
        // Безопасно перенаправляем в перегруженную версию, определяя тип проекта на лету
        // На прошлом шаге мы выяснили, что в ProjectConfig тип лежит в свойстве Type
        // Если мы внутри сервиса, мы можем определить тип по наличию .json кэша или передать Folder как фолбэк
        await GeneratePdfContextFileAsync(
            outputPath, fileName, promptRules, moduleRules, includeDirectoryStructure,
            rootNode, checkedEntries, progressHandler, ProjectType.Folder, token);
    }

    /// <summary>
    /// Перегруженный метод генерации PDF, который принимает ProjectType напрямую.
    /// </summary>
    public static async Task GeneratePdfContextFileAsync(
    string outputPath,
    string fileName,
    string promptRules,
    string moduleRules,
    bool includeDirectoryStructure,
    FileSystemNode rootNode,
    List<SyntaxEntry> checkedEntries,
    IProgress<ProgressReport> progressHandler,
    ProjectType projectType,
    CancellationToken token)
    {
        var checkedFiles = new List<FileSystemNode>();
        GetCheckedFilesExtended(rootNode, checkedFiles);

        // 1. Вызываем обновленный BuildTextContentAsync для сборки всего текстового и XML каркаса книги
        var sb = await BuildTextContentAsync(
            promptRules, moduleRules, includeDirectoryStructure,
            checkedFiles, checkedEntries, progressHandler, projectType, token);

        string metaDataText = sb.ToString();
        token.ThrowIfCancellationRequested();

        // 2. Инициализируем документ MigraDoc
        var document = new MigraDoc.DocumentObjectModel.Document();
        var style = document.Styles["Normal"];
        if (style?.Font != null)
        {
            style.Font.Name = "Courier New";
            style.Font.Size = 10; // Комфортный размер шрифта для чтения
        }

        var currentSection = document.AddSection();
        SetSectionMargins(currentSection);

        // 3. МАРШРУТИЗАЦИЯ ПО ТИПУ ПРОЕКТА ИЗ CONFIG
        // ================================================================= -->
        // ИСПРАВЛЕНО: УМНОЕ БИТЬЕ НА СТРАНИЦЫ PDF БЕЗ ПУСТЫХ ЛИСТОВ И РАЗРЫВОВ -->
        // ================================================================= -->
        // ================================================================= -->
        // ИСПРАВЛЕНО: ОТДЕЛЕНИЕ ОГЛАВЛЕНИЯ РАЗРЫВОМ, КОНТЕНТ ВКЛАДОК - СПЛОШНОЙ -->
        // ================================================================= -->
        if (projectType == ProjectType.GoogleDoc || projectType == ProjectType.WordDoc)
        {
            using (var reader = new StringReader(metaDataText))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    token.ThrowIfCancellationRequested();
                    string trimmedLine = line.Trim();

                    // ПРАВИЛО 1: Маркер завершения структуры оглавления — делаем принудительный разрыв страницы!
                    if (trimmedLine.StartsWith("</document_structure>"))
                    {
                        var closeStructP = currentSection.AddParagraph(line);
                        closeStructP.Format.Font.Bold = true;
                        closeStructP.Format.Font.Color = MigraDoc.DocumentObjectModel.Colors.DarkBlue;

                        currentSection = document.AddSection();
                        SetSectionMargins(currentSection);
                        continue;
                    }

                    // ПРАВИЛО 2: Контент вкладок НЕ отделяем разрывом, пишем теги <tab> последовательно в поток!
                    if (trimmedLine.StartsWith("<tab") || trimmedLine.StartsWith("</tab"))
                    {
                        var tabP = currentSection.AddParagraph(line);
                        tabP.Format.Font.Bold = true;
                        tabP.Format.Font.Color = MigraDoc.DocumentObjectModel.Colors.DarkBlue;
                        tabP.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromPoint(4);
                        continue;
                    }

                    // ПРАВИЛО 3: Каждая новая глава романа или секция начинается строго с нового листа А4
                    if (trimmedLine.StartsWith("<heading"))
                    {
                        currentSection = document.AddSection();
                        SetSectionMargins(currentSection);

                        var markerP = currentSection.AddParagraph(line);
                        markerP.Format.Font.Bold = true;
                        markerP.Format.Font.Color = MigraDoc.DocumentObjectModel.Colors.DarkBlue;
                        markerP.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromPoint(6);
                        continue;
                    }

                    if (trimmedLine.StartsWith("</heading"))
                    {
                        var closeP = currentSection.AddParagraph(line);
                        closeP.Format.Font.Bold = true;
                        closeP.Format.Font.Color = MigraDoc.DocumentObjectModel.Colors.DarkBlue;
                        closeP.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(6);
                        continue;
                    }

                    // Выводим обычный текст художественной прозы
                    var p = currentSection.AddParagraph(line);
                    if (string.IsNullOrWhiteSpace(line))
                        p.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(4);
                }
            }
        }

        else
        {
            // === ВОССТАНОВЛЕННЫЙ ОРИГИНАЛЬНЫЙ ЦИКЛ ДЛЯ ПРОГРАММНОГО КОДА ===
            var p = currentSection.AddParagraph("=== СОДЕРЖИМОЕ ВЫБРАННЫХ ФАЙЛОВ ===");
            p.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(12);

            using (var reader = new StringReader(metaDataText))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    token.ThrowIfCancellationRequested();
                    if (line.StartsWith("=== СОДЕРЖИМОЕ ВЫБРАННЫХ ФАЙЛОВ ===")) break;

                    var metaP = currentSection.AddParagraph(line);
                    if (string.IsNullOrWhiteSpace(line)) metaP.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(4);
                }
            }

            for (int i = 0; i < checkedFiles.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var fileNode = checkedFiles[i];

                progressHandler?.Report(new ProgressReport { CurrentIndex = i + 1, TotalCount = checkedFiles.Count, CurrentFileName = fileNode.Name });
                if (!File.Exists(fileNode.FullPath)) continue;

                currentSection.AddParagraph($"---{fileNode.RelativePath}---");

                using (var fs = new FileStream(fileNode.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        currentSection.AddParagraph(line);
                    }
                }
                currentSection.AddParagraph("");
            }
        }

        // 4. Финальное сохранение PDF-документа на жесткий диск
        var renderer = new MigraDoc.Rendering.PdfDocumentRenderer();
        renderer.Document = document;
        renderer.RenderDocument();

        string fullPath = Path.Combine(outputPath, fileName);
        renderer.PdfDocument.Save(fullPath);
    }

    private static void SetSectionMargins(MigraDoc.DocumentObjectModel.Section section)
    {
        section.PageSetup.TopMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2);
        section.PageSetup.BottomMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2);
        section.PageSetup.RightMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2);
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
