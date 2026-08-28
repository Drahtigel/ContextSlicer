using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ContextSlicer
{
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
                    // ИСПРАВЛЕНО: Если расширение файла находится в глобальном черном списке, мы его даже не добавляем в дерево!
                    if (IsExtensionExcluded(file.FullName)) continue;

                    var relPath = Path.GetRelativePath(rootPath, file.FullName);
                    var childNode = new FileSystemNode { Name = file.Name, FullPath = file.FullName, RelativePath = relPath, IsFile = true, Parent = parentNode };
                    if (savedCheckedFiles.Contains(relPath)) childNode.IsChecked = true;
                    parentNode.Children.Add(childNode);
                }
            }
            catch (UnauthorizedAccessException) { }
        }

        public static void GetCheckedFiles(FileSystemNode node, List<FileSystemNode> result)
        {
            if (node.IsFile && node.IsChecked == true)
            {
                // ИСПРАВЛЕНО: Проверяем расширение по динамическому глобальному списку
                if (!IsExtensionExcluded(node.FullPath))
                {
                    result.Add(node);
                }
            }
            foreach (var child in node.Children) GetCheckedFiles(child, result);
        }

        // Асинхронная сборка текста с поддержкой прогресса и отмены операции
        public static async Task<StringBuilder> BuildTextContentAsync(
            string rules,
            List<FileSystemNode> checkedFiles,
            IProgress<ProgressReport> progress,
            CancellationToken token)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== ПРАВИЛА ОБРАЩЕНИЯ С КОДОМ ===");
            sb.AppendLine(rules ?? "");
            sb.AppendLine();

            sb.AppendLine("=== СТРУКТУРА ВЫБРАННЫХ КАТАЛОГОВ ===");
            foreach (var file in checkedFiles)
            {
                if (file == null) continue;
                token.ThrowIfCancellationRequested();
                sb.AppendLine($"  [Файл] {file.RelativePath ?? ""}");
            }
            sb.AppendLine();

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
                    string content = await File.ReadAllTextAsync(file.FullPath, Encoding.UTF8, token);
                    sb.AppendLine(content ?? "");
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

        // Асинхронная генерация PDF с поддержкой отмены и прогресса
        public static async Task GeneratePdfContextFileAsync(
            string outputPath,
            string fileName,
            string rules,
            FileSystemNode rootNode,
            IProgress<ProgressReport> progress,
            CancellationToken token)
        {
            var checkedFiles = new List<FileSystemNode>();
            GetCheckedFiles(rootNode, checkedFiles);

            var sb = await BuildTextContentAsync(rules, checkedFiles, progress, token);
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

            string fullOutputPath = Path.Combine(outputPath, fileName.EndsWith(".pdf") ? fileName : fileName + ".pdf");

            await Task.Run(() =>
            {
                var renderer = new MigraDoc.Rendering.PdfDocumentRenderer();
                renderer.Document = document;
                renderer.RenderDocument();
                renderer.PdfDocument.Save(fullOutputPath);
            }, token);
        }

        // Асинхронная генерация обычного TXT
        public static async Task GenerateContextFileAsync(
            string outputPath,
            string fileName,
            string rules,
            FileSystemNode rootNode,
            IProgress<ProgressReport> progress,
            CancellationToken token)
        {
            var checkedFiles = new List<FileSystemNode>();
            GetCheckedFiles(rootNode, checkedFiles);

            var sb = await BuildTextContentAsync(rules, checkedFiles, progress, token);
            string fullOutputPath = Path.Combine(outputPath, fileName.EndsWith(".txt") ? fileName : fileName + ".txt");

            await File.WriteAllTextAsync(fullOutputPath, sb.ToString(), Encoding.UTF8, token);
        }
    }
}
