using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
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
        // ИСПРАВЛЕНО: Собираем файл только в том случае, если он выбран ПОЛНОСТЬЮ (true).
        // Если он выбран частично (null), мы его здесь НЕ берем, так как его куски будут сохранены в CheckedEntries.
        if (node.IsFile && node.IsChecked == true)
        {
            if (!IsExtensionExcluded(node.FullPath))
            {
                if (!result.Contains(node)) result.Add(node);
            }
        }
        foreach (var child in node.Children)
        {
            GetCheckedFiles(child, result);
        }
    }
    // Добавьте этот метод в ContextBuilderService.cs для генератора и сохранения
    public static void GetCheckedFilesExtended(FileSystemNode node, List<FileSystemNode> result)
    {
        // Берем файл, если он выбран полностью (true) ИЛИ частично (null)
        if (node.IsFile && (node.IsChecked == true || node.IsChecked == null))
        {
            if (!IsExtensionExcluded(node.FullPath))
            {
                if (!result.Contains(node)) result.Add(node);
            }
        }
        foreach (var child in node.Children)
        {
            GetCheckedFilesExtended(child, result);
        }
    }

    // Асинхронная сборка текста с поддержкой прогресса и отмены операции
    public static async Task<StringBuilder> BuildTextContentAsync(
string projectRules,
string moduleRules,
bool includeDirectoryStructure,
List<FileSystemNode> checkedFiles,
List<SyntaxEntry> savedEntries, // ДОБАВЛЕН ЖЕСТКИЙ ИСТОЧНИК ДАННЫХ
IProgress<ProgressReport> progress,
CancellationToken token)
    {
        var sb = new StringBuilder();

        bool hasProjectRules = !string.IsNullOrWhiteSpace(projectRules);
        bool hasModuleRules = !string.IsNullOrWhiteSpace(moduleRules);

        if (hasProjectRules || hasModuleRules)
        {
            sb.AppendLine("=== ПРАВИЛА ОБРАЩЕНИЯ С КОДОМ ===");
            if (hasProjectRules)
            {
                sb.AppendLine("<project_rules>");
                sb.AppendLine(projectRules.Trim());
                sb.AppendLine("</project_rules>");
                sb.AppendLine();
            }
            if (hasModuleRules)
            {
                sb.AppendLine("<module_rules>");
                sb.AppendLine(moduleRules.Trim());
                sb.AppendLine("</module_rules>");
                sb.AppendLine();
            }
        }

        if (includeDirectoryStructure)
        {
            sb.AppendLine("=== СТРУКТУРА ВЫБРАННЫХ КАТАЛОГОВ ===");
            foreach (var file in checkedFiles)
            {
                if (file == null) continue;
                token.ThrowIfCancellationRequested();
                sb.AppendLine($" [Файл] {file.RelativePath ?? ""}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("=== СОДЕРЖИМОЕ ФАЙЛОВ ===");
        int total = checkedFiles.Count;
        for (int i = 0; i < total; i++)
        {
            var file = checkedFiles[i];
            if (file == null || string.IsNullOrEmpty(file.FullPath)) continue;

            progress?.Report(new ProgressReport
            {
                CurrentIndex = i + 1,
                TotalCount = total,
                CurrentFileName = file.RelativePath ?? ""
            });

            sb.AppendLine($"---{file.RelativePath ?? ""}---");

            try
            {
                token.ThrowIfCancellationRequested();

                // СЦЕНАРИЙ А: Файл выбран полностью
                if (file.IsChecked == true)
                {
                    string content = await File.ReadAllTextAsync(file.FullPath, Encoding.UTF8, token);
                    sb.AppendLine(content ?? "");
                }
                // СЦЕНАРИЙ Б: Файл выбран частично (Интегрирован прямой поиск по JSON-конфигу)
                else
                {
                    sb.AppendLine($"// [Внимание ИИ: Из данного файла извлечены только выбранные структуры и функции]");

                    // Фильтруем записи синтаксиса конкретно для этого файла из сохраненного конфига модуля
                    var fileEntries = savedEntries?.FindAll(e => e.FilePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase)) ?? new List<SyntaxEntry>();

                    if (fileEntries.Count > 0)
                    {
                        string fullContent = await File.ReadAllTextAsync(file.FullPath, Encoding.UTF8, token);

                        foreach (var entry in fileEntries)
                        {
                            token.ThrowIfCancellationRequested();

                            string[] parts = entry.SpanInfo.Split(',');
                            if (parts.Length == 2 && int.TryParse(parts[0], out int start) && int.TryParse(parts[1], out int length))
                            {
                                if (start >= 0 && start + length <= fullContent.Length)
                                {
                                    string fragment = fullContent.Substring(start, length);

                                    // Формируем красивый тег с типом элемента синтаксиса и его внутренностями
                                    string tag = entry.Type.ToString().ToLower();
                                    sb.AppendLine($"<{tag} path=\"{entry.EntryPath}\">");
                                    sb.AppendLine(fragment);
                                    sb.AppendLine($"</{tag}>");
                                    sb.AppendLine();
                                }
                            }
                        }
                    }
                    else
                    {
                        sb.AppendLine("// [Элементы не выбраны]");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sb.AppendLine($"[Ошибка чтения файла: {ex.Message}]");
            }
            sb.AppendLine();
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


    // Асинхронная генерация PDF с поддержкой отмены и прогресса
    // Асинхронная генерация PDF с поддержкой отмены, прогресса и синтаксических записей
    public static async Task GeneratePdfContextFileAsync(
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
        GetCheckedFilesExtended(rootNode, checkedFiles); // Используем расширенный сбор для корректного прогресс-бара

        // ИСПРАВЛЕНО: Передаем savedEntries, полученный из параметров метода
        var sb = await BuildTextContentAsync(projectRules, moduleRules, includeDirectoryStructure,
            checkedFiles, savedEntries, progress, token);

        string textData = sb.ToString();
        token.ThrowIfCancellationRequested();

        var document = new MigraDoc.DocumentObjectModel.Document();
        var section = document.AddSection();
        section.PageSetup.TopMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1);
        section.PageSetup.BottomMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1);
        section.PageSetup.LeftMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1);
        section.PageSetup.RightMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1);

        var style = document.Styles["Normal"];
        if (style?.Font != null)
        {
            style.Font.Name = "Courier New";
            style.Font.Size = 9;
        }

        using (var reader = new StringReader(textData))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                token.ThrowIfCancellationRequested();
                var paragraph = section.AddParagraph(line);
                if (string.IsNullOrWhiteSpace(line))
                {
                    paragraph.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(6);
                }
            }
        }

         // Формируем полный путь к итоговому файлу
    string fullOutputPath = Path.Combine(outputPath, fileName.EndsWith(".pdf") ? fileName : fileName + ".pdf");

    // ИСПРАВЛЕНО: Защита от блокировки процесса при перезаписи существующего PDF
    if (File.Exists(fullOutputPath))
    {
        try
        {
            // Пытаемся физически удалить старую версию файла перед тем, как рендерер начнет монопольно писать данные
            File.Delete(fullOutputPath);
        }
        catch (IOException)
        {
            // Если файл открыт в стороннем просмотрщике (например, в Acrobat Reader или браузере)
            System.Windows.MessageBox.Show(
                $"Не удалось перезаписать файл.\nВозможно, он открыт в другой программе (PDF-просмотрщике или браузере).\n\nЗакройте файл и повторите попытку.",
                "Ошибка доступа к файлу", 
                System.Windows.MessageBoxButton.OK, 
                System.Windows.MessageBoxImage.Warning);
            return; // Мягко выходим из метода, предотвращая жесткий крах всего приложения
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось подготовить файл к перезаписи: {ex.Message}", "Ошибка", 
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }
    }

    // Запускаем асинхронный рендеринг документа в PDF
    await Task.Run(() =>
    {
        var renderer = new MigraDoc.Rendering.PdfDocumentRenderer();
        renderer.Document = document;
        renderer.RenderDocument();
        renderer.PdfDocument.Save(fullOutputPath); // ТЕПЕРЬ ЗАПИСЬ ВСЕГДА ИДЕТ В ЧИСТЫЙ ПУТЬ БЕЗ КОНФЛИКТОВ!
    }, token);
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
