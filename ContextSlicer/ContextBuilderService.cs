using ContextSlicer.Filesystem;
using ContextSlicer.Google;
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
    // ИСПРАВЛЕНО: СОВМЕСТИМЫЕ ПЕРЕГРУЗКИ ТЕКСТОВОГО БИЛДЕРА С КАРТИНКАМИ -->
    // ================================================================= -->
    /// <summary>
    /// ПЕРЕГРУЗКА 1 (СОВМЕСТИМАЯ): Исторический базовый прототип без ProjectType и без картинок.
    /// По умолчанию передает ProjectType.Folder и false для изображений.
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
            checkedFiles, savedEntries, progressHandler, ProjectType.Folder, false, token);
    }

    /// <summary>
    /// ПЕРЕГРУЗКА 2 (СОВМЕСТИМАЯ): Прототип с ProjectType, но без флага картинок.
    /// По умолчанию отключает генерацию картинок (передает false) для совместимости со старыми вызовами.
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
        return await BuildTextContentAsync(
            promptRules, moduleRules, includeDirectoryStructure,
            checkedFiles, savedEntries, progressHandler, projectType, false, token);
    }

    /// <summary>
    /// ОСНОВНОЙ МЕТОД (ПЕРЕГРУЖЕННЫЙ): Принимает полный набор параметров, включая includeImages,
    /// и гибко управляет диспетчеризацией текстового и визуального контента.
    /// </summary>
    public static async Task<StringBuilder> BuildTextContentAsync(
        string promptRules,
        string moduleRules,
        bool includeDirectoryStructure,
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        IProgress<ProgressReport> progressHandler,
        ProjectType projectType,
        bool includeImages, // Новый параметр гибкого управления изображениями
        CancellationToken token)
    {
        var sb = new StringBuilder();

        // 1. Выгрузка системных правил и промптов проекта
        if (!string.IsNullOrWhiteSpace(promptRules) || !string.IsNullOrWhiteSpace(moduleRules))
        {
            sb.AppendLine("=== ПРАВИЛА ОБРАЩЕНИЯ С ТЕКСТОМ И КОДЕМ ===");
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

        // Интеллектуальное автоопределение типа проекта на лету
        bool isActuallyLiterary = projectType == ProjectType.GoogleDoc ||
                                 projectType == ProjectType.WordDoc ||
                                 savedEntries.Any(e => e.Type == EntryType.Tab);

        // 2. МАРШРУТИЗАЦИЯ ПО ИСТИННОМУ ТИУ
        if (isActuallyLiterary)
        {
            string bookRootPath = checkedFiles.FirstOrDefault()?.FullPath ?? string.Empty;

            if (string.IsNullOrEmpty(bookRootPath))
            {
                string googleCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
                var firstEntry = savedEntries.FirstOrDefault(e => !string.IsNullOrEmpty(e.FilePath));
                if (firstEntry != null)
                {
                    bookRootPath = Path.Combine(googleCacheDir, Path.GetFileName(firstEntry.FilePath));
                }
            }

            // Вызываем новую пятипараметрическую версию метода BuildContextTextAsync, прокидывая локальный флаг includeImages!
            string literaryContent = await GoogleDocBuilder.BuildContextTextAsync(
                savedEntries.ToList(),
                bookRootPath,
                includeDirectoryStructure,
                includeImages,
                token);

            sb.Append(literaryContent);
        }
        else
        {
            // === ИСХОДНЫЙ КОД C# И SQL ===
            if (includeDirectoryStructure)
            {
                sb.AppendLine("=== СТРУКТУРА ВЫБРАННОГО КОДА ===");
                // (Твой оригинальный вывод дерева папок кода)
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
    // ИСПРАВЛЕНО: ПЕРЕГРУЗКИ PDF-ГЕНЕРАТОРА С ИСТОРИЧЕСКИМ ПОРЯДКОМ АРГУМЕНТОВ -->
    // ================================================================= -->
    public static async Task GeneratePdfContextFileAsync(
        string outputPath, string fileName, string promptRules, string moduleRules,
        bool includeDirectoryStructure, FileSystemNode rootNode,
        List<SyntaxEntry> checkedEntries, IProgress<ProgressReport> progressHandler,
        CancellationToken token)
    {
        await GeneratePdfContextFileAsync(outputPath, fileName, promptRules, moduleRules,
            includeDirectoryStructure, false, rootNode, checkedEntries, progressHandler, ProjectType.Folder, token);
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: БРОНЕБОЙНЫЙ PDF-РЕНДЕРЕР ЛИТЕРАТУРЫ И ИЛЛЮСТРАЦИЙ    -->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО: ДИАГНОСТИЧЕСКИЙ БЛОК ПРОВЕРКИ ВЕСА МЕТА-ДАННЫХ        -->
    // ================================================================= -->
    public static async Task GeneratePdfContextFileAsync(
        string outputPath, string fileName, string promptRules, string moduleRules,
        bool includeDirectoryStructure, bool includeImages, FileSystemNode rootNode,
        List<SyntaxEntry> checkedEntries, IProgress<ProgressReport> progressHandler,
        ProjectType projectType, CancellationToken token)
    {
        if (checkedEntries == null || checkedEntries.Count == 0 || string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(fileName) || rootNode == null) return;

        // ИСПРАВЛЕНО: Вместо сканирования очищенных FilePath, берем прямой физический путь к файлу кэша
        // из корневого узла дерева UI (точно так же, как это делает рабочий счетчик токенов!)
        string bookRootPath = rootNode.FullPath ?? string.Empty;

        // Фолбэк-подстраховка на случай, если в FullPath папки лежит пустая строка
        if (string.IsNullOrEmpty(bookRootPath) || !File.Exists(bookRootPath))
        {
            string googleCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
            if (projectType == ProjectType.GoogleDoc && System.Windows.Application.Current.MainWindow?.DataContext is MainViewModel vm && vm.SelectedProject != null)
            {
                string fallbackDocId = GoogleDownloader.ExtractDocumentId(vm.SelectedProject.RootPath);
                if (!string.IsNullOrEmpty(fallbackDocId))
                {
                    bookRootPath = Path.Combine(googleCacheDir, $"{fallbackDocId}.json");
                }
            }
        }

        bool isActuallyLiterary = projectType == ProjectType.GoogleDoc || projectType == ProjectType.WordDoc || checkedEntries.Any(e => e.Type == EntryType.Tab);
        string metaDataText = string.Empty;

        if (isActuallyLiterary)
        {
            // Передаем гарантированно восстановленный bookRootPath в сборщик контента книги
            metaDataText = await GoogleDocBuilder.BuildContextTextAsync(checkedEntries, bookRootPath, includeDirectoryStructure, includeImages, token);
        }
        else
        {
            var checkedFilesList = new List<FileSystemNode>();
            GetCheckedFiles(rootNode, checkedFilesList);
            var sb = await BuildTextContentAsync(promptRules, moduleRules, includeDirectoryStructure, checkedFilesList, checkedEntries, progressHandler, projectType, includeImages, token);
            metaDataText = sb.ToString();
        }

        if (string.IsNullOrWhiteSpace(metaDataText)) return;

        var document = new MigraDoc.DocumentObjectModel.Document();

        // Явно инициализируем шрифт по умолчанию для MigraDoc во избежание скрытых крашей кириллицы
        var style = document.Styles["Normal"];
        if (style != null)
        {
            style.Font.Name = "Arial";
            style.Font.Size = 10;
        }

        var currentSection = document.AddSection();
        SetSectionMargins(currentSection);

        // 1. Выгрузка системных правил и промптов проекта
        if (!string.IsNullOrWhiteSpace(promptRules) || !string.IsNullOrWhiteSpace(moduleRules))
        {
            var rulesP = currentSection.AddParagraph("=== ПРАВИЛА ОБРАЩЕНИЯ С ТЕКСТОМ И КОДОМ ===");
            rulesP.Format.Font.Bold = true; rulesP.Format.Font.Color = MigraDoc.DocumentObjectModel.Colors.DarkBlue;
            if (!string.IsNullOrWhiteSpace(promptRules))
            {
                currentSection.AddParagraph("<project_rules>"); currentSection.AddParagraph(promptRules.Trim()); currentSection.AddParagraph("</project_rules>\n");
            }
            if (!string.IsNullOrWhiteSpace(moduleRules))
            {
                currentSection.AddParagraph("<module_rules>"); currentSection.AddParagraph(moduleRules.Trim()); currentSection.AddParagraph("</module_rules>\n");
            }
        }

        string googleDocId = !string.IsNullOrEmpty(bookRootPath) ? Path.GetFileNameWithoutExtension(bookRootPath) : string.Empty;
        string imagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache", "images", googleDocId);

        // 2. БРОНЕБОЙНЫЙ ПОСТРОЧНЫЙ ВЫВОД КНИГИ С ИЛЛЮСТРАЦИЯМИ
        using (var reader = new StringReader(metaDataText))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                token.ThrowIfCancellationRequested();
                string trimmedLine = line.Trim();
                // ================================================================= -->
                // ИСПРАВЛЕНО CS1503: ТОЧНЫЙ ВЫБОР ПЕРВОГО КАДРА ИЗОБРАЖЕНИЯ [0]      -->
                // ================================================================= -->
                if (trimmedLine.StartsWith("<image") && trimmedLine.Contains("src=\""))
                {
                    if (includeImages && !string.IsNullOrEmpty(googleDocId))
                    {
                        try
                        {
                            int startIdx = trimmedLine.IndexOf("src=\"") + 5;
                            int endIdx = trimmedLine.IndexOf("\"", startIdx);

                            if (startIdx > 5 && endIdx > startIdx)
                            {
                                string rawSrc = trimmedLine.Substring(startIdx, endIdx - startIdx);
                                string imgId = Path.GetFileNameWithoutExtension(rawSrc);

                                string fullImgPath = string.Empty;
                                string[] allowedExtensions = { ".jpg", ".png", ".webp" };

                                foreach (var ext in allowedExtensions)
                                {
                                    string checkPath = Path.Combine(imagesDir, imgId + ext);
                                    if (File.Exists(checkPath))
                                    {
                                        fullImgPath = checkPath;
                                        break;
                                    }
                                }

                                if (!string.IsNullOrEmpty(fullImgPath) && File.Exists(fullImgPath))
                                {
                                    string pathToRender = fullImgPath;
                                    bool isWebP = fullImgPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

                                    try
                                    {
                                        bool isTargetImage = imgId.Contains("kcbc4ihbg7ia") || isWebP;

                                        if (isTargetImage)
                                        {
                                            string forceJpgPath = Path.Combine(imagesDir, imgId + "_baseline.jpg");

                                            if (!File.Exists(forceJpgPath))
                                            {
                                                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(fullImgPath), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                                                if (decoder.Frames.Count > 0)
                                                {
                                                    var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                                                    encoder.QualityLevel = 100;
                                                    // ИСПРАВЛЕНО CS1503: Берем первый конкретный кадр по индексу [0]
                                                    encoder.Frames.Add(decoder.Frames[0]);

                                                    using (var outStream = new FileStream(forceJpgPath, FileMode.Create))
                                                    {
                                                        encoder.Save(outStream);
                                                    }
                                                }
                                            }

                                            if (File.Exists(forceJpgPath)) pathToRender = forceJpgPath;
                                        }

                                        var img = currentSection.AddImage(pathToRender);
                                        img.Width = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(14);
                                        img.LockAspectRatio = true;
                                        img.Left = MigraDoc.DocumentObjectModel.Shapes.ShapePosition.Center;
                                        continue;
                                    }
                                    catch (Exception renderEx)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[Сбой рендеринга] Аварийный фолбэк в JPEG: {renderEx.Message}");

                                        try
                                        {
                                            string emergencyJpgPath = Path.Combine(imagesDir, imgId + "_emergency_baseline.jpg");

                                            if (!File.Exists(emergencyJpgPath))
                                            {
                                                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(fullImgPath), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                                                if (decoder.Frames.Count > 0)
                                                {
                                                    var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                                                    encoder.QualityLevel = 95;
                                                    // ИСПРАВЛЕНО CS1503: Берем первый конкретный кадр по индексу [0]
                                                    encoder.Frames.Add(decoder.Frames[0]);
                                                    using (var outStream = new FileStream(emergencyJpgPath, FileMode.Create)) encoder.Save(outStream);
                                                }
                                            }

                                            if (File.Exists(emergencyJpgPath))
                                            {
                                                var img = currentSection.AddImage(emergencyJpgPath);
                                                img.Width = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(14);
                                                img.LockAspectRatio = true;
                                                img.Left = MigraDoc.DocumentObjectModel.Shapes.ShapePosition.Center;
                                                continue;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    continue;
                }



                // ИСПРАВЛЕНО: Все служебные XML-теги и заголовки подсвечиваем синим цветом,
                // но больше НЕ ломаем переключение секций листов!
                if (trimmedLine.StartsWith("<tab") || trimmedLine.StartsWith("</tab") ||
                    trimmedLine.StartsWith("<heading") || trimmedLine.StartsWith("</heading") ||
                    trimmedLine.StartsWith("<document_structure>") || trimmedLine.StartsWith("</document_structure>"))
                {
                    var metaP = currentSection.AddParagraph(line);
                    metaP.Format.Font.Bold = true;
                    metaP.Format.Font.Color = MigraDoc.DocumentObjectModel.Colors.DarkBlue;
                    metaP.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromPoint(2);
                    continue;
                }

                // КРИСТАЛЬНО ЧИСТЫЙ ВЫВОД ХУДОЖЕСТВЕННОЙ ПРОЗЫ РОМАНА СИМВОЛ В СИМВОЛ
                var p = currentSection.AddParagraph(line);
                if (string.IsNullOrWhiteSpace(line))
                {
                    p.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(4);
                }
            }
        }

        // 3. ФИНАЛЬНОЕ СОХРАНЕНИЕ ДОКУМЕНТА НА ДИСК
        var renderer = new MigraDoc.Rendering.PdfDocumentRenderer();
        renderer.Document = document;
        renderer.RenderDocument();

        string cleanFileName = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(fileName) : fileName;
        string baseFullPath = Path.Combine(outputPath, cleanFileName + ".pdf");
        string finalFullPath = baseFullPath;

        if (File.Exists(baseFullPath))
        {
            try { File.Delete(baseFullPath); }
            catch (IOException)
            {
                string timeSuffix = DateTime.Now.ToString("HHmmss");
                finalFullPath = Path.Combine(outputPath, $"{cleanFileName}_{timeSuffix}.pdf");
            }
        }

        try { renderer.PdfDocument.Save(finalFullPath); }
        catch (Exception ex)
        {
            string errTitle = System.Windows.Application.Current.Resources["Str_Err_GeneralErrorTitle"] as string ?? "Ошибка";
            string errTemplate = System.Windows.Application.Current.Resources["Str_Err_PdfSaveFailed"] as string ?? "Не удалось сохранить PDF документ:";
            System.Windows.MessageBox.Show($"{errTemplate} {ex.Message}", errTitle, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
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
