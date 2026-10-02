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

                // ИСПРАВЛЕНО: Принудительно нормализуем слэши к единому стандарту '/' перед проверкой Contains!
                string normalizedRelPath = relPath.Replace('\\', '/');
                var normalizedSavedFiles = savedCheckedFiles.Select(f => f.Replace('\\', '/')).ToList();

                if (normalizedSavedFiles.Contains(normalizedRelPath))
                {
                    childNode.IsChecked = true;
                }

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

    // ================================================================= -->
    // ДОБАВЛЕНО: УМНЫЙ ФОНОВЫЙ СЛИЯТЕЛЬ (MERGE) ДИСКА И КАРКАСА JSON    -->
    // ================================================================= -->
    /// <summary>
    /// Сканирует физический диск и аккуратно доселяет отсутствующие файлы/папки в уже построенный JSON-каркас,
    /// полностью защищая существующие Tri-State квадратики и галочки от затирания!
    /// </summary>
    public static void MergeDirectoryWithConfigTree(string currentPath, FileSystemNode parentNode, string rootPath)
    {
        if (string.IsNullOrEmpty(currentPath) || parentNode == null || !Directory.Exists(currentPath)) return;

        try
        {
            var dirInfo = new DirectoryInfo(currentPath);

            // 1. АВТОДОПИСЫВАНИЕ ОТСУТСТВУЮЩИХ ПОДДИРЕКТОРИЙ
            foreach (var subDir in dirInfo.GetDirectories())
            {
                if (IsFolderExcluded(subDir.Name)) continue;

                string relDirPath = Path.GetRelativePath(rootPath, subDir.FullName);

                // Ищем, не был ли этот каталог уже упреждающе создан нашим каркасом из JSON
                var existingDirNode = parentNode.Children.FirstOrDefault(c => c.Name.Equals(subDir.Name, StringComparison.OrdinalIgnoreCase) && !c.IsFile);

                if (existingDirNode == null)
                {
                    // Если папки на диске не было в конфиге — создаем её в дефолтном пустом состоянии false
                    existingDirNode = new FileSystemNode
                    {
                        Name = subDir.Name,
                        FullPath = subDir.FullName,
                        RelativePath = relDirPath,
                        IsFile = false,
                        Parent = parentNode,
                        IsChecked = false
                    };
                    parentNode.Children.Add(existingDirNode);
                }

                // Рекурсивно шагаем вглубь папки для слияния файлов
                MergeDirectoryWithConfigTree(subDir.FullName, existingDirNode, rootPath);
            }

            // 2. АВТОДОПИСЫВАНИЕ ОТСУТСТВУЮЩИХ ФАЙЛОВ КОДА (.cs, .sql)
            foreach (var file in dirInfo.GetFiles())
            {
                if (IsExtensionExcluded(file.FullName)) continue;

                string relFilePath = Path.GetRelativePath(rootPath, file.FullName);

                // Ищем, не был ли этот файл уже упреждающе создан каркасом
                var existingFileNode = parentNode.Children.FirstOrDefault(c => c.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase) && c.IsFile && !c.IsSyntaxNode);

                if (existingFileNode == null)
                {
                    // Если файла с диска не было в конфиге — аккуратно доселяем его на экран в статусе false
                    var childNode = new FileSystemNode
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        RelativePath = relFilePath,
                        IsFile = true,
                        Parent = parentNode,
                        IsChecked = false
                    };

                    // Если файл поддерживает ленивый синтаксис — вешаем стандартную заглушку раскрытия
                    string ext = Path.GetExtension(file.FullName);
                    if (SyntaxParserFactory.IsSupported(ext))
                    {
                        childNode.Children.Add(new FileSystemNode { Name = "LoadingStub...", Parent = childNode, IsFile = true });
                    }

                    parentNode.Children.Add(childNode);
                }
            }
        }
        catch (UnauthorizedAccessException) { /* Защита от системных заблокированных папок Windows */ }
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
    // ИСПРАВЛЕНО: ТОТАЛЬНОЕ РАЗДЕЛЕНИЕ ТЕКСТОВЫХ СБОРЩИКОВ КОДА И КНИГ   -->
    // ================================================================= -->
    /// <summary>
    /// ГЛАВНЫЙ ДИСПЕТЧЕР (ПЕРЕГРУЗКА 1): Исторический прототип для совместимости с ViewModel.
    /// Маршрутизирует вызовы на основе типа проекта.
    /// </summary>
    public static async Task<StringBuilder> BuildTextContentAsync(
        string promptRules, string moduleRules, bool includeDirectoryStructure,
        List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries,
        IProgress<ProgressReport> progressHandler, CancellationToken token)
    {
        return await BuildTextContentAsync(promptRules, moduleRules, includeDirectoryStructure,
            checkedFiles, savedEntries, progressHandler, ProjectType.Folder, false, token);
    }

    /// <summary>
    /// ГЛАВНЫЙ ДИСПЕТЧЕР (ПЕРЕГРУЗКА 2): Принимает полный набор параметров и жестко
    /// изолирует обработку книг от исходного кода на уровне вызовов функций.
    /// </summary>
    public static async Task<StringBuilder> BuildTextContentAsync(
        string promptRules, string moduleRules, bool includeDirectoryStructure,
        List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries,
        IProgress<ProgressReport> progressHandler, ProjectType projectType,
        bool includeImages, CancellationToken token)
    {
        bool isActuallyLiterary = projectType == ProjectType.GoogleDoc ||
                                 projectType == ProjectType.WordDoc ||
                                 savedEntries.Any(e => e.Type == EntryType.Tab);

        if (isActuallyLiterary)
        {
            // Огород разделен: Книги уходят в свой персональный метод
            return await BuildGoogleDocTextContextAsync(includeDirectoryStructure, includeImages, checkedFiles, savedEntries, token);
        }
        else
        {
            // Исходный код уходит в свой чистый технический метод
            // ИСПРАВЛЕНО CS7036: Добавлен обязательный параметр token в конец вызова
            return await BuildCodeTextContextAsync(promptRules, moduleRules, includeDirectoryStructure, checkedFiles, savedEntries, token);
          //  return await BuildCodeTextContextAsync(promptRules, moduleRules, includeDirectoryStructure, checkedFiles, token);
        }
    }
    // ================================================================= -->
    // ИСПРАВЛЕНО: ХИРУРГИЧЕСКИЙ СБОР ФУНКЦИЙ КОДА ПО КООРДИНАТАМ SPAN   -->
    // ================================================================= -->
    private static async Task<StringBuilder> BuildCodeTextContextAsync(
        string promptRules, string moduleRules, bool includeDirectoryStructure,
        List<FileSystemNode> checkedFiles, List<SyntaxEntry> checkedEntries, CancellationToken token)
    {
        var sb = new StringBuilder();
        if (checkedFiles == null || checkedFiles.Count == 0) return sb;

        // 1. Выгрузка системных правил проекта и модуля
        if (!string.IsNullOrWhiteSpace(promptRules) || !string.IsNullOrWhiteSpace(moduleRules))
        {
            sb.AppendLine("=== ПРАВИЛА ОБРАЩЕНИЯ С ТЕКСТОМ И КОДЕМ ===");
            if (!string.IsNullOrWhiteSpace(promptRules)) sb.AppendLine("<project_rules>" + promptRules.Trim() + "</project_rules>\n");
            if (!string.IsNullOrWhiteSpace(moduleRules)) sb.AppendLine("<module_rules>" + moduleRules.Trim() + "</module_rules>\n");
        }

        // 2. ИСПРАВЛЕНО: Генерация оглавления строго без дубликатов
        if (includeDirectoryStructure)
        {
            sb.AppendLine("=== СТРУКТУРА ВЫБРАННОГО КОДА ===");
            var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in checkedFiles)
            {
                if (file != null && uniquePaths.Add(file.RelativePath))
                {
                    sb.AppendLine($"├─ {file.RelativePath}");
                }
            }
            sb.AppendLine();
        }

        // Группируем сохраненные синтаксические элементы по файлам для быстрого поиска
        var entriesByFile = checkedEntries != null
            ? checkedEntries.Where(e => !string.IsNullOrEmpty(e.FilePath)).ToLookup(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
            : null;

        // ================================================================= -->
        // ИСПРАВЛЕНО: АВТОНОМНЫЙ ХИРУРГИЧЕСКИЙ ДЕТЕКТОР ВЫРЕЗАНИЯ ФУНКЦИЙ   -->
        // ================================================================= -->
        // 3. Выгрузка содержимого файлов исходного кода
        if (checkedFiles == null || checkedFiles.Count == 0) return sb;

        for (int i = 0; i < checkedFiles!.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var fileNode = checkedFiles[i];
            if (fileNode == null || !File.Exists(fileNode.FullPath)) continue;

            // БРОНЕБОЙНЫЙ ПЕРЕХВАТ: Ищем, выбраны ли конкретные методы (FUNC, PROP, SECT) внутри ноды этого файла.
            // Мы обходим живые UI-ноды внутри дерева Children самого файла, полностью игнорируя текстовые коллизии JSON!
            var selectedMethods = new List<FileSystemNode>();
            CollectCheckedLeavesOnly(fileNode, selectedMethods);

            // Если файл отмечен галочкой True (выбран целиком) ИЛИ внутри него не выбрано ни одной точечной функции
            bool isFullFileMode = (fileNode.IsChecked == true) || (selectedMethods.Count == 0);

            // ИСПРАВЛЕНО: Строка заголовка файла пишется строго один раз за проход
            sb.AppendLine($"---{fileNode.RelativePath}---");
            string fullCodeText = await File.ReadAllTextAsync(fileNode.FullPath);

            if (isFullFileMode)
            {
                // Наливаем файл целиком, если точечные функции не выбраны
                sb.AppendLine(fullCodeText);
            }
            else
            {
                // ХИРУРГИЧЕСКИЙ РЕЖИМ НАБЛЮДЕНИЯ: Вырезаем только usings и выбранные функции по SpanInfo!

                // А) Автоматически вырезаем блок импортов (using), если он присутствует в структуре файла
                FileSystemNode? usingsNode = null;
                foreach (var child in fileNode.Children)
                {
                    if (child != null && child.IsSyntaxNode && child.SyntaxType == EntryType.Section && child.Name.Contains("using"))
                    {
                        usingsNode = child;
                        break;
                    }
                }
                if (usingsNode != null)
                {
                    var span = GetSpanValues(usingsNode.SyntaxSpanInfo);
                    if (span.Start + span.Length <= fullCodeText.Length)
                    {
                        sb.AppendLine(fullCodeText.Substring(span.Start, span.Length));
                        sb.AppendLine();
                    }
                }

                // Б) Вырезаем чистый код только тех функций, напротив которых стоит честная галочка [✓]
                foreach (var methodNode in selectedMethods)
                {
                    if (methodNode == null || string.IsNullOrEmpty(methodNode.SyntaxSpanInfo)) continue;

                    var span = GetSpanValues(methodNode.SyntaxSpanInfo);
                    if (span.Start + span.Length <= fullCodeText.Length)
                    {
                        sb.AppendLine($"// [ВЫРЕЗАННЫЙ БЛОК: {methodNode.Name}]");
                        sb.AppendLine(fullCodeText.Substring(span.Start, span.Length));
                        sb.AppendLine();
                    }
                }
            }
            sb.AppendLine();
        }



        return sb;
    }
    /// <summary>
    /// Рекурсивно собирает только конечные выбранные функции (листья) внутри ноды файла
    /// </summary>
    private static void CollectCheckedLeavesOnly(FileSystemNode node, List<FileSystemNode> result)
    {
        if (node == null || result == null) return;

        // Если это синтаксический метод/свойство и у него стоит честная галочка выбора
        if (node.IsSyntaxNode && node.IsChecked == true && (node.SyntaxType == EntryType.Function || node.SyntaxType == EntryType.Property || node.SyntaxType == EntryType.Section))
        {
            if (!node.Name.Contains("using")) result.Add(node);
        }

        foreach (var child in node.Children)
        {
            CollectCheckedLeavesOnly(child, result);
        }
    }

    /// <summary>
    /// Безопасно парсит строку координат SpanInfo "start,length"
    /// </summary>
    private static (int Start, int Length) GetSpanValues(string spanInfo)
    {
        if (string.IsNullOrEmpty(spanInfo)) return (0, 0);
        var parts = spanInfo.Split(',');
        int start = parts.Length > 0 && int.TryParse(parts[0], out int s) ? start = s : 0;
        int len = parts.Length > 1 && int.TryParse(parts[1], out int l) ? len = l : 0;
        return (start, len);
    }

    /// <summary>
    /// ИЗОЛИРОВАННАЯ ФУНКЦИЯ КНИГ: Вызывает GoogleDocBuilder для сборки прозы и глав романа
    /// </summary>
    private static async Task<StringBuilder> BuildGoogleDocTextContextAsync(
        bool includeDirectoryStructure, bool includeImages,
        List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        var sb = new StringBuilder();
        string bookRootPath = checkedFiles.FirstOrDefault()?.FullPath ?? string.Empty;

        if (string.IsNullOrEmpty(bookRootPath))
        {
            string googleCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
            var firstEntry = savedEntries.FirstOrDefault(e => !string.IsNullOrEmpty(e.FilePath));
            if (firstEntry != null) bookRootPath = Path.Combine(googleCacheDir, Path.GetFileName(firstEntry.FilePath));
        }

        string literaryContent = await GoogleDocBuilder.BuildContextTextAsync(savedEntries.ToList(), bookRootPath, includeDirectoryStructure, includeImages, token);
        sb.Append(literaryContent);
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
    // ИСПРАВЛЕНО: МАТЕМАТИЧЕСКИ ТОЧНЫЙ ДИСПЕТЧЕР МАРШРУТИЗАЦИИ PDF      -->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО: АДАПТИВНАЯ ВАЛИДАЦИЯ ВХОДА БЕЗ ЛОЖНЫХ RETURN ДЛЯ КОДА -->
    // ================================================================= -->
    public static async Task GeneratePdfContextFileAsync(
        string outputPath, string fileName, string promptRules, string moduleRules,
        bool includeDirectoryStructure, bool includeImages, FileSystemNode rootNode,
        List<SyntaxEntry> checkedEntries, IProgress<ProgressReport> progressHandler,
        ProjectType projectType, CancellationToken token)
    {
        // Базовая жесткая защита на null критических параметров путей диска
        if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(fileName) || rootNode == null) return;

        // Интеллектуально определяем, является ли целевой проект литературным (книгой)
        bool isTargetLiterary = (projectType == ProjectType.GoogleDoc || projectType == ProjectType.WordDoc);

        if (isTargetLiterary)
        {
            // ДЛЯ КНИГ: Требуем, чтобы список глав и вкладок в CheckedEntries не был пустым
            if (checkedEntries == null || checkedEntries.Count == 0) return;

            // Конвейер Книг: Уходит в свой изолированный метод с графикой
            await GenerateGoogleDocPdfAsync(outputPath, fileName, promptRules, moduleRules, includeDirectoryStructure, includeImages, rootNode, checkedEntries, token);
        }
        else
        {
            // ДЛЯ ИСХОДНОГО КОДА: Нам абсолютно ВСЕ РАВНО, пустой ли checkedEntries! 
            // Главное, чтобы были выбраны файлы на диске. Проверка выполнится внутри BuildCodeTextContextAsync.
            // Конвейер Кода: ТЕПЕРЬ ГАРАНТИРОВАННО И ВСЕГДА ВЫЗЫВАЕТ МЕТОД ОТРЕСОВКИ ИСХОДНИКОВ C#/SQL!
            await GenerateCodePdfAsync(outputPath, fileName, promptRules, moduleRules, includeDirectoryStructure, rootNode, checkedEntries, progressHandler, token);
        }
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: НЕУЯЗВИМЫЙ ИЗДАТЕЛЬСКИЙ PDF-РЕНДЕРЕР ИСХОДНОГО КОДА   -->
    // ================================================================= -->
    private static async Task GenerateCodePdfAsync(
        string outputPath, string fileName, string promptRules, string moduleRules,
        bool includeDirectoryStructure, FileSystemNode rootNode,
        List<SyntaxEntry> checkedEntries, IProgress<ProgressReport> progressHandler, CancellationToken token)
    {
        var checkedFilesList = new List<FileSystemNode>();
        GetCheckedFiles(rootNode, checkedFilesList);

        // 1. Собираем весь текстовый контекст проекта кода в памяти через разделенный билдер
        // var sb = await BuildCodeTextContextAsync(promptRules, moduleRules, includeDirectoryStructure, checkedFilesList, token);
        // ИСПРАВЛЕНО CS7036: Добавлен обязательный параметр token в конец вызова
        var sb = await BuildCodeTextContextAsync(promptRules, moduleRules, includeDirectoryStructure, checkedFilesList, checkedEntries, token);

        string metaDataText = sb.ToString();

        if (string.IsNullOrWhiteSpace(metaDataText)) return;

        var document = new MigraDoc.DocumentObjectModel.Document();

        // Создаем выделенный технический стиль для кода C#/SQL во избежание сбоев рендеринга
        var codeStyle = document.Styles.AddStyle("CodeStyle", "Normal");
        if (codeStyle != null)
        {
            codeStyle.Font.Name = "Courier New"; // Моноширинный шрифт для идеальных отступов
            codeStyle.Font.Size = 8.5;            // Компактный размер для экономии страниц
            codeStyle.ParagraphFormat.LineSpacing = 11;
            codeStyle.ParagraphFormat.KeepTogether = true; // Защита от разрыва одной строки кода
        }

        var currentSection = document.AddSection();
        SetSectionMargins(currentSection);

        // 2. БРОНЕБОЙНАЯ ОТРИСОВКА СТРОК КОДА В МЕТОДЕ
        using (var reader = new StringReader(metaDataText))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                token.ThrowIfCancellationRequested();

                // Выпрямляем символы табуляции, чтобы они не ломали координатную сетку MigraDoc
                string cleanLine = line.Replace("\t", "    ");

                // Добавляем параграф, жестко завязанный на наш изолированный CodeStyle
                var p = currentSection.AddParagraph();
                p.Style = "CodeStyle";

                // Используем AddFormattedText для экранирования управляющих символов C# от движка верстки
                var fmtText = p.AddFormattedText(cleanLine);

                if (string.IsNullOrWhiteSpace(cleanLine))
                {
                    p.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(2);
                }
            }
        }

        // 3. Физически компилируем и сохраняем готовый PDF на жесткий диск
        SavePdfToDiskAndOpen(document, outputPath, fileName);
    }

    /// <summary>
    /// КОНВЕЙЕР КНИГ: Строит PDF для прозы с полной поддержкой разметки, ID и автоконвертации Progressive JPEG/WebP
    /// </summary>
    private static async Task GenerateGoogleDocPdfAsync(
        string outputPath, string fileName, string promptRules, string moduleRules,
        bool includeDirectoryStructure, bool includeImages, FileSystemNode rootNode,
        List<SyntaxEntry> checkedEntries, CancellationToken token)
    {
        string bookRootPath = rootNode.FullPath ?? string.Empty;
        if (string.IsNullOrEmpty(bookRootPath) || !File.Exists(bookRootPath))
        {
            string googleCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
            if (System.Windows.Application.Current.MainWindow?.DataContext is MainViewModel vm && vm.SelectedProject != null)
            {
                string fallbackDocId = GoogleDownloader.ExtractDocumentId(vm.SelectedProject.RootPath);
                if (!string.IsNullOrEmpty(fallbackDocId)) bookRootPath = Path.Combine(googleCacheDir, $"{fallbackDocId}.json");
            }
        }

        string metaDataText = await GoogleDocBuilder.BuildContextTextAsync(checkedEntries, bookRootPath, includeDirectoryStructure, includeImages, token);
        if (string.IsNullOrWhiteSpace(metaDataText)) return;

        var document = new MigraDoc.DocumentObjectModel.Document();
        var style = document.Styles["Normal"];
        if (style != null) { style.Font.Name = "Arial"; style.Font.Size = 10; }

        var currentSection = document.AddSection();
        SetSectionMargins(currentSection);

        // Накатываем правила проекта в начало PDF
        if (!string.IsNullOrWhiteSpace(promptRules) || !string.IsNullOrWhiteSpace(moduleRules))
        {
            var rulesP = currentSection.AddParagraph("=== ПРАВИЛА ОБРАЩЕНИЯ С ТЕКСТОМ И КОДОМ ===");
            rulesP.Format.Font.Bold = true; rulesP.Format.Font.Color = MigraDoc.DocumentObjectModel.Colors.DarkBlue;
            if (!string.IsNullOrWhiteSpace(promptRules)) { currentSection.AddParagraph("<project_rules>"); currentSection.AddParagraph(promptRules.Trim()); currentSection.AddParagraph("</project_rules>\n"); }
            if (!string.IsNullOrWhiteSpace(moduleRules)) { currentSection.AddParagraph("<module_rules>"); currentSection.AddParagraph(moduleRules.Trim()); currentSection.AddParagraph("</module_rules>\n"); }
        }

        string googleDocId = !string.IsNullOrEmpty(bookRootPath) ? Path.GetFileNameWithoutExtension(bookRootPath) : string.Empty;
        string imagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache", "images", googleDocId);

        // Вызываем нашу Часть 2.1 — изолированный построчный XML/HTML парсер списков и графики
        await ProcessPdfLinesAndSaveAsync(metaDataText, document, currentSection, imagesDir, googleDocId, includeImages, outputPath, fileName, token);
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

    // ================================================================= -->
    // ЧАСТЬ 2.1: МЕЖДУНАРОДНЫЙ PDF-ПАРСЕР HTML-СПИСКОВ И ОГЛАВЛЕНИЯ    -->
    // ================================================================= -->
    private static async Task ProcessPdfLinesAndSaveAsync(
        string metaDataText, MigraDoc.DocumentObjectModel.Document document,
        MigraDoc.DocumentObjectModel.Section currentSection, string imagesDir,
        string googleDocId, bool includeImages, string outputPath, string fileName,
        CancellationToken token)
    {
        using (var reader = new StringReader(metaDataText))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                token.ThrowIfCancellationRequested();
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine)) continue;

                // 1. ДИНАМИЧЕСКИЙ ПЕРЕХВАТ МАРКЕРА КАРТИНКИ
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
                                    if (File.Exists(checkPath)) { fullImgPath = checkPath; break; }
                                }

                                if (!string.IsNullOrEmpty(fullImgPath) && File.Exists(fullImgPath))
                                {
                                    string pathToRender = fullImgPath;
                                    bool isWebP = fullImgPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

                                    try
                                    {
                                        if (imgId.Contains("kcbc4ihbg7ia") || isWebP)
                                        {
                                            string forceJpgPath = Path.Combine(imagesDir, imgId + "_baseline.jpg");
                                            if (!File.Exists(forceJpgPath))
                                            {
                                                using (var stream = new FileStream(fullImgPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                                                {
                                                    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                                                    if (decoder.Frames.Count > 0)
                                                    {
                                                        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                                                        encoder.QualityLevel = 100; encoder.Frames.Add(decoder.Frames[0]);
                                                        using (var outStream = new FileStream(forceJpgPath, FileMode.Create)) encoder.Save(outStream);
                                                    }
                                                }
                                            }
                                            if (File.Exists(forceJpgPath)) pathToRender = forceJpgPath;
                                        }

                                        var img = currentSection.AddImage(pathToRender);
                                        img.Width = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(14);
                                        img.LockAspectRatio = true; img.Left = MigraDoc.DocumentObjectModel.Shapes.ShapePosition.Center;
                                        continue;
                                    }
                                    catch
                                    {
                                        try
                                        {
                                            string emergencyJpgPath = Path.Combine(imagesDir, imgId + "_emergency_baseline.jpg");
                                            if (!File.Exists(emergencyJpgPath))
                                            {
                                                using (var stream = new FileStream(fullImgPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                                                {
                                                    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                                                    if (decoder.Frames.Count > 0)
                                                    {
                                                        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                                                        encoder.QualityLevel = 95; encoder.Frames.Add(decoder.Frames[0]);
                                                        using (var outStream = new FileStream(emergencyJpgPath, FileMode.Create)) encoder.Save(outStream);
                                                    }
                                                }
                                            }
                                            if (File.Exists(emergencyJpgPath))
                                            {
                                                var img = currentSection.AddImage(emergencyJpgPath);
                                                img.Width = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(14);
                                                img.LockAspectRatio = true; img.Left = MigraDoc.DocumentObjectModel.Shapes.ShapePosition.Center;
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

                // 2. ИСПРАВЛЕНО: ПЕРЕХВАТ ЭЛЕМЕНТОВ СПИСКА И ОГЛАВЛЕНИЯ С ЛЕСЕНКОЙ В 15 ПУНКТОВ
                if (trimmedLine.StartsWith("<li") && trimmedLine.Contains("level=\""))
                {
                    var liP = currentSection.AddParagraph(line);
                    liP.Format.Font.Bold = trimmedLine.Contains("id=\""); // Выделяем жирным элементы оглавления
                    liP.Format.Font.Color = trimmedLine.Contains("id=\"") ? MigraDoc.DocumentObjectModel.Colors.DarkBlue : MigraDoc.DocumentObjectModel.Colors.Black;

                    try
                    {
                        int startIdx = trimmedLine.IndexOf("level=\"") + 7;
                        int endIdx = trimmedLine.IndexOf("\"", startIdx);
                        if (startIdx > 7 && endIdx > startIdx && int.TryParse(trimmedLine.Substring(startIdx, endIdx - startIdx), out int liLevel))
                        {
                            // МАТЕМАТИЧЕСКИ ИДЕАЛЬНО: Сдвигаем каждый пункт списка вправо ровно на level * 15 пунктов!
                            liP.Format.LeftIndent = MigraDoc.DocumentObjectModel.Unit.FromPoint(liLevel * 15 + 10);
                        }
                    }
                    catch { }

                    liP.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromPoint(2);
                    continue;
                }

                // 3. УНИВЕРСАЛЬНЫЙ ПЕРЕХВАТ ВНЕШНИХ XML-КОНТЕЙНЕРОВ С ID
                if (trimmedLine.StartsWith("<tab") || trimmedLine.StartsWith("</tab") ||
                    trimmedLine.StartsWith("<heading") || trimmedLine.StartsWith("</heading") ||
                    trimmedLine.StartsWith("<ul") || trimmedLine.StartsWith("</ul") ||
                    trimmedLine.StartsWith("<document_structure>") || trimmedLine.StartsWith("</document_structure>") ||
                    trimmedLine.StartsWith("=== СТРУКТУРА"))
                {
                    var metaP = currentSection.AddParagraph(line);
                    metaP.Format.Font.Bold = true; metaP.Format.Font.Color = MigraDoc.DocumentObjectModel.Colors.DarkBlue;
                    metaP.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromPoint(2);
                    continue;
                }

                // 4. СТРОКИ ХУДОЖЕСТВЕННОЙ ПРОЗЫ РОМАНА
                var p = currentSection.AddParagraph(line);
                if (string.IsNullOrWhiteSpace(line)) p.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(4);
            }
        }

        SavePdfToDiskAndOpen(document, outputPath, fileName);
    }

    // ================================================================= -->
    // ЧАСТЬ 2.2: ФИЗИЧЕСКИЙ РЕНДЕРИНГ PDF НА ДИСК И АВТООТКРЫТИЕ ПАПКИ   -->
    // ================================================================= -->
    // ================================================================= -->
    // ЧАСТЬ 2.2: ФИЗИЧЕСКИЙ РЕНДЕРИНГ PDF НА ДИСК И АВТООТКРЫТИЕ ПАПКИ   -->
    // ================================================================= -->
    private static void SavePdfToDiskAndOpen(
        MigraDoc.DocumentObjectModel.Document document,
        string outputPath,
        string fileName)
    {
        var renderer = new MigraDoc.Rendering.PdfDocumentRenderer();
        renderer.Document = document;
        renderer.RenderDocument();

        string cleanFileName = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;

        string baseFullPath = Path.Combine(outputPath, cleanFileName + ".pdf");
        string finalFullPath = baseFullPath;

        if (File.Exists(baseFullPath))
        {
            try
            {
                File.Delete(baseFullPath);
            }
            catch (IOException)
            {
                string timeSuffix = DateTime.Now.ToString("HHmmss");
                finalFullPath = Path.Combine(outputPath, $"{cleanFileName}_{timeSuffix}.pdf");
            }
        }

        try
        {
            renderer.PdfDocument.Save(finalFullPath);

            // Автоматически открываем Проводник Windows и подсвечиваем сгенерированный контекст
            if (File.Exists(finalFullPath))
            {
                string argument = $"/select,\"{finalFullPath}\"";
                System.Diagnostics.Process.Start("explorer.exe", argument);
            }
        }
        catch (Exception ex)
        {
            string errTitle = System.Windows.Application.Current.Resources["Str_Err_GeneralErrorTitle"] as string ?? "Ошибка";
            string errTemplate = System.Windows.Application.Current.Resources["Str_Err_PdfSaveFailed"] as string ?? "Не удалось сохранить PDF документ:";
            System.Windows.MessageBox.Show($"{errTemplate} {ex.Message}", errTitle, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
} // <--- Финальная закрывающая фигурная скобка всего класса ContextBuilderService!



